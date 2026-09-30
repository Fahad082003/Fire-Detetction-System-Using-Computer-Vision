"""
/ws/live-stream/ — single realtime channel for the command center.

Producers (AI vision pipelines, IoT gateways) authenticate with a device key and push:
    {"type": "detection", "camera_id": "...", "detections": [...], ...}
    {"type": "telemetry", "sensor_id": "...", "value": 61.2}
Dashboards connect read-only and receive the fan-out events:
    snapshot | detection | telemetry | sprinkler | incident | notification | log
"""
import json
import logging
import time

from channels.db import database_sync_to_async
from channels.generic.websocket import AsyncJsonWebsocketConsumer
from django.utils import timezone
from rest_framework.exceptions import ValidationError

from . import services
from .models import SensorNode
from .permissions import open_access
from .realtime import LIVE_GROUP, abroadcast
from .serializers import TelemetrySerializer

logger = logging.getLogger(__name__)

MAX_MESSAGE_BYTES = 512 * 1024
PRODUCER_MAX_MESSAGES_PER_SECOND = 60


class LiveStreamConsumer(AsyncJsonWebsocketConsumer):
    async def connect(self):
        self.is_device = bool(self.scope.get("device_authenticated"))
        user = self.scope.get("user")
        self.is_operator = bool(user and user.is_authenticated)
        if not (self.is_device or self.is_operator or open_access()):
            await self.close(code=4401)
            return

        self._window_start = time.monotonic()
        self._window_count = 0

        await self.channel_layer.group_add(LIVE_GROUP, self.channel_name)
        await self.accept()
        snapshot = await database_sync_to_async(services.build_snapshot)()
        await self.send_json({"type": "snapshot", "data": snapshot, "ts": timezone.now().isoformat()})

    async def disconnect(self, code):
        await self.channel_layer.group_discard(LIVE_GROUP, self.channel_name)

    # ------------------------------------------------------------------ inbound
    async def receive(self, text_data=None, bytes_data=None, **kwargs):
        raw = text_data if text_data is not None else (bytes_data or b"").decode("utf-8", "ignore")
        if len(raw) > MAX_MESSAGE_BYTES:
            await self._error("Message too large.")
            return
        try:
            content = json.loads(raw)
        except (TypeError, ValueError):
            await self._error("Invalid JSON.")
            return
        if not isinstance(content, dict):
            await self._error("Message must be a JSON object.")
            return
        await self.receive_json(content)

    async def receive_json(self, content, **kwargs):
        msg_type = content.get("type")
        if msg_type == "ping":
            await self.send_json({"type": "pong", "ts": timezone.now().isoformat()})
            return
        if msg_type not in ("detection", "telemetry"):
            await self._error(f"Unsupported message type: {msg_type!r}")
            return
        if not (self.is_device or open_access()):
            await self._error("Only authenticated devices may publish.", code="forbidden")
            return
        if self._rate_limited():
            await self._error("Rate limit exceeded.", code="rate_limited")
            return

        try:
            if msg_type == "detection":
                await self._handle_detection(content)
            else:
                await self._handle_telemetry(content)
        except ValidationError as exc:
            await self.send_json({"type": "error", "code": "invalid", "errors": exc.detail})
        except Exception:
            logger.exception("Failed to process %s message", msg_type)
            await self._error("Internal processing error.", code="server_error")

    async def _handle_detection(self, content):
        # Validation is pure Python; the overlay fan-out happens before any DB work so
        # dashboards render boxes at stream rate.
        serializer = services.validate_detection(content)
        await abroadcast("detection", serializer.to_broadcast())
        data = serializer.validated_data
        if data["fire_detected"] or data["danger_count"] > 0:
            await database_sync_to_async(services.evaluate_detection)(data)

    async def _handle_telemetry(self, content):
        serializer = TelemetrySerializer(data=content)
        serializer.is_valid(raise_exception=True)
        sensor_id = content.get("sensor_id")
        ok = await database_sync_to_async(self._ingest_telemetry)(sensor_id, serializer.validated_data)
        if not ok:
            raise ValidationError({"sensor_id": [f"Unknown sensor {sensor_id!r}."]})

    @staticmethod
    def _ingest_telemetry(sensor_id, validated):
        sensor = SensorNode.objects.filter(sensor_id=sensor_id).first()
        if sensor is None:
            return False
        services.ingest_telemetry(sensor, validated["value"], validated.get("recorded_at"))
        return True

    def _rate_limited(self):
        now = time.monotonic()
        if now - self._window_start >= 1.0:
            self._window_start, self._window_count = now, 0
        self._window_count += 1
        return self._window_count > PRODUCER_MAX_MESSAGES_PER_SECOND

    async def _error(self, detail, code="bad_request"):
        await self.send_json({"type": "error", "code": code, "detail": detail})

    # ------------------------------------------------------------------ outbound
    async def broadcast_event(self, event):
        await self.send_json(event["payload"])
