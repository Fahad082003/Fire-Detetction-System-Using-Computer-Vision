import logging

import httpx
from django.conf import settings
from django.contrib.auth.views import redirect_to_login
from django.http import FileResponse, Http404, HttpResponse, HttpResponseForbidden, StreamingHttpResponse
from django.shortcuts import render
from django.utils.dateparse import parse_datetime
from django.views.decorators.csrf import ensure_csrf_cookie
from rest_framework import mixins, status, viewsets
from rest_framework.decorators import action
from rest_framework.pagination import LimitOffsetPagination
from rest_framework.response import Response
from rest_framework.views import APIView

from . import services
from .models import CameraFeed, IncidentAlert, PushSubscription, SensorNode, SmartSprinkler
from .permissions import OperatorOnly, open_access
from .serializers import (
    BulkTelemetrySerializer,
    CameraFeedSerializer,
    EmergencyDispatchSerializer,
    IncidentAlertSerializer,
    PushSubscriptionSerializer,
    SensorNodeSerializer,
    SensorReadingSerializer,
    SmartSprinklerSerializer,
    SprinklerMasterSerializer,
    SprinklerToggleSerializer,
    TelemetrySerializer,
)

logger = logging.getLogger(__name__)


def actor_name(request):
    if request.user and request.user.is_authenticated:
        return request.user.get_username()
    if request.headers.get("X-Device-Key"):
        return "device"
    return "operator"


# --------------------------------------------------------------------------- pages
@ensure_csrf_cookie
def dashboard(request):
    if not (open_access() or request.user.is_authenticated):
        return redirect_to_login(request.get_full_path())
    config = {
        "wsPath": "/ws/live-stream/",
        "apiBase": "/api",
        "siteName": settings.SENTINEL["SITE_NAME"],
        "pushEnabled": bool(settings.VAPID_PUBLIC_KEY),
    }
    return render(request, "index.html", {"app_config": config})


def service_worker(request):
    path = settings.BASE_DIR / "static" / "js" / "sw.js"
    if not path.exists():
        raise Http404
    response = FileResponse(open(path, "rb"), content_type="application/javascript")
    response["Service-Worker-Allowed"] = "/"
    response["Cache-Control"] = "no-cache"
    return response


# --------------------------------------------------------------------------- cameras
class CameraFeedViewSet(viewsets.ModelViewSet):
    queryset = CameraFeed.objects.all()
    serializer_class = CameraFeedSerializer
    lookup_field = "feed_id"

    def get_queryset(self):
        qs = super().get_queryset()
        feed_type = self.request.query_params.get("feed_type")
        return qs.filter(feed_type=feed_type) if feed_type else qs


async def camera_stream_proxy(request, feed_id):
    """
    Same-origin MJPEG relay. Browsers render multipart/x-mixed-replace in <img>, but
    upstream NVRs/drones rarely send CORS headers or serve HTTPS; relaying avoids both.
    """
    user = await request.auser()
    if not (open_access() or user.is_authenticated):
        return HttpResponseForbidden("Authentication required.")

    camera = await CameraFeed.objects.filter(feed_id=feed_id).afirst()
    if camera is None or not camera.stream_url:
        raise Http404("Unknown camera or no stream configured.")
    if camera.stream_format != CameraFeed.StreamFormat.MJPEG:
        return HttpResponse("Proxy supports MJPEG feeds only; play HLS/MP4 directly.", status=400)
    if not camera.stream_url.startswith(("http://", "https://")):
        return HttpResponse("MJPEG proxy requires an http(s) upstream.", status=400)

    client = httpx.AsyncClient(timeout=httpx.Timeout(10.0, read=30.0), follow_redirects=True)
    try:
        upstream = await client.send(client.build_request("GET", camera.stream_url), stream=True)
    except httpx.HTTPError as exc:
        await client.aclose()
        logger.warning("MJPEG upstream for %s unreachable: %s", feed_id, exc)
        return HttpResponse("Upstream stream unavailable.", status=502)

    if upstream.status_code != 200:
        await upstream.aclose()
        await client.aclose()
        return HttpResponse(f"Upstream returned {upstream.status_code}.", status=502)

    async def relay():
        try:
            async for chunk in upstream.aiter_raw():
                yield chunk
        except httpx.HTTPError as exc:
            logger.info("MJPEG relay for %s ended: %s", feed_id, exc)
        finally:
            await upstream.aclose()
            await client.aclose()

    response = StreamingHttpResponse(
        relay(), content_type=upstream.headers.get("content-type", "multipart/x-mixed-replace")
    )
    response["Cache-Control"] = "no-cache, no-store, must-revalidate"
    response["X-Accel-Buffering"] = "no"
    return response


# --------------------------------------------------------------------------- sensors
class SensorNodeViewSet(viewsets.ModelViewSet):
    queryset = SensorNode.objects.all()
    serializer_class = SensorNodeSerializer
    lookup_field = "sensor_id"

    def get_queryset(self):
        qs = super().get_queryset()
        params = self.request.query_params
        if params.get("type"):
            qs = qs.filter(type__iexact=params["type"])
        if params.get("zone"):
            qs = qs.filter(location_zone__iexact=params["zone"])
        return qs

    def perform_update(self, serializer):
        sensor = serializer.save()
        # Threshold changes can move a sensor into/out of alarm; re-evaluate with the current value.
        services.ingest_telemetry(sensor, sensor.current_value)

    @action(detail=True, methods=["post"])
    def telemetry(self, request, sensor_id=None):
        sensor = self.get_object()
        serializer = TelemetrySerializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        services.ingest_telemetry(sensor, serializer.validated_data["value"], serializer.validated_data.get("recorded_at"))
        sensor.refresh_from_db()
        return Response(SensorNodeSerializer(sensor).data)

    @action(detail=True, methods=["get"])
    def history(self, request, sensor_id=None):
        sensor = self.get_object()
        try:
            limit = max(1, min(int(request.query_params.get("limit", 120)), 2000))
        except ValueError:
            limit = 120
        readings = sensor.readings.all()
        since = parse_datetime(request.query_params.get("since", "") or "")
        if since:
            readings = readings.filter(recorded_at__gte=since)
        data = SensorReadingSerializer(list(readings[:limit])[::-1], many=True).data
        return Response({"sensor_id": sensor.sensor_id, "unit": sensor.unit, "readings": data})


class BulkTelemetryView(APIView):
    """IoT gateway ingestion: {"readings": [{"sensor_id": "smoke-a1", "value": 42.0}, ...]}"""

    def post(self, request):
        serializer = BulkTelemetrySerializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        updated, errors = services.ingest_bulk_telemetry(serializer.validated_data["readings"])
        code = status.HTTP_207_MULTI_STATUS if errors and updated else (
            status.HTTP_400_BAD_REQUEST if errors else status.HTTP_200_OK
        )
        return Response({"updated": updated, "errors": errors}, status=code)


# --------------------------------------------------------------------------- sprinklers
class SmartSprinklerViewSet(viewsets.ModelViewSet):
    queryset = SmartSprinkler.objects.all()
    serializer_class = SmartSprinklerSerializer
    lookup_field = "device_id"

    @action(detail=True, methods=["post"])
    def toggle(self, request, device_id=None):
        """Body (optional): {"status": "Active"|"Inactive", "mode": "Auto"|"Manual"}. Empty body flips the relay."""
        sprinkler = self.get_object()
        serializer = SprinklerToggleSerializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        sprinkler = services.toggle_sprinkler(
            sprinkler,
            status=serializer.validated_data.get("status"),
            mode=serializer.validated_data.get("mode"),
            actor=actor_name(request),
        )
        return Response(SmartSprinklerSerializer(sprinkler).data)

    @action(detail=False, methods=["post"])
    def master(self, request):
        """System-wide (or per-zone) relay switch: {"status": "Active"|"Inactive", "zone"?: "Zone B"}"""
        serializer = SprinklerMasterSerializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        changed = services.set_all_sprinklers(
            serializer.validated_data["status"],
            zone=serializer.validated_data.get("zone") or None,
            actor=actor_name(request),
        )
        return Response({
            "changed": [s.device_id for s in changed],
            "sprinklers": SmartSprinklerSerializer(SmartSprinkler.objects.all(), many=True).data,
        })


# --------------------------------------------------------------------------- incidents
class IncidentPagination(LimitOffsetPagination):
    default_limit = 50
    max_limit = 500


class IncidentAlertViewSet(mixins.ListModelMixin, mixins.RetrieveModelMixin, viewsets.GenericViewSet):
    serializer_class = IncidentAlertSerializer
    pagination_class = IncidentPagination

    def get_queryset(self):
        qs = IncidentAlert.objects.select_related("camera", "sensor")
        params = self.request.query_params
        if params.get("resolved") in ("true", "false"):
            qs = qs.filter(resolved=params["resolved"] == "true")
        if params.get("severity"):
            qs = qs.filter(severity__in=[s.strip() for s in params["severity"].split(",")])
        if params.get("zone"):
            qs = qs.filter(zone__iexact=params["zone"])
        since = parse_datetime(params.get("since", "") or "")
        if since:
            qs = qs.filter(timestamp__gte=since)
        return qs

    @action(detail=True, methods=["post"])
    def resolve(self, request, pk=None):
        incident = services.resolve_incident(self.get_object(), actor=actor_name(request))
        return Response(IncidentAlertSerializer(incident).data)


# --------------------------------------------------------------------------- ingestion / control
class DetectionIngestView(APIView):
    """REST alternative to the WebSocket producer path for batch/offline AI pipelines."""

    def post(self, request):
        payloads = request.data if isinstance(request.data, list) else [request.data]
        if len(payloads) > 100:
            return Response({"detail": "At most 100 frames per request."}, status=status.HTTP_400_BAD_REQUEST)
        incidents = [services.process_detection(p) for p in payloads]
        return Response(
            {"accepted": len(payloads), "incidents": [i.pk for i in incidents if i is not None]},
            status=status.HTTP_202_ACCEPTED,
        )


class EmergencyDispatchView(APIView):
    permission_classes = [OperatorOnly]

    def post(self, request):
        serializer = EmergencyDispatchSerializer(data=request.data)
        serializer.is_valid(raise_exception=True)
        incident = services.manual_dispatch(actor=actor_name(request), **serializer.validated_data)
        return Response(IncidentAlertSerializer(incident).data, status=status.HTTP_201_CREATED)


class SystemSnapshotView(APIView):
    def get(self, request):
        return Response(services.build_snapshot())


class PushSubscriptionView(APIView):
    def get(self, request):
        return Response({"vapid_public_key": settings.VAPID_PUBLIC_KEY or None})

    def post(self, request):
        serializer = PushSubscriptionSerializer(data=request.data, context={"request": request})
        serializer.is_valid(raise_exception=True)
        serializer.save()
        return Response({"subscribed": True}, status=status.HTTP_201_CREATED)

    def delete(self, request):
        endpoint = request.data.get("endpoint") if isinstance(request.data, dict) else None
        if not endpoint:
            return Response({"detail": "endpoint required"}, status=status.HTTP_400_BAD_REQUEST)
        PushSubscription.objects.filter(endpoint=endpoint).update(active=False)
        return Response(status=status.HTTP_204_NO_CONTENT)
