from django.utils import timezone
from rest_framework import serializers

from .models import (
    CameraFeed,
    IncidentAlert,
    PushSubscription,
    SensorNode,
    SensorReading,
    Severity,
    SmartSprinkler,
)

MAX_DETECTIONS = 200
MAX_ZONES = 32
MAX_POLYGON_POINTS = 64
MAX_HEATMAP_CELLS = 64 * 64


# --------------------------------------------------------------------------- models
class CameraFeedSerializer(serializers.ModelSerializer):
    class Meta:
        model = CameraFeed
        fields = [
            "feed_id", "name", "feed_type", "stream_url", "stream_format",
            "status", "location_zone", "updated_at",
        ]
        read_only_fields = ["updated_at"]


class SensorNodeSerializer(serializers.ModelSerializer):
    unit = serializers.CharField(read_only=True)
    state = serializers.CharField(read_only=True)
    critical_threshold = serializers.FloatField(read_only=True)

    class Meta:
        model = SensorNode
        fields = [
            "sensor_id", "type", "current_value", "threshold", "critical_threshold",
            "location_zone", "unit", "state", "last_reading_at", "updated_at",
        ]
        # Telemetry values change only through the telemetry endpoints (which run the alert engine).
        read_only_fields = ["current_value", "last_reading_at", "updated_at"]

    def validate_threshold(self, value):
        if value <= 0:
            raise serializers.ValidationError("Threshold must be positive.")
        return value


class SensorReadingSerializer(serializers.ModelSerializer):
    class Meta:
        model = SensorReading
        fields = ["value", "recorded_at"]


class SmartSprinklerSerializer(serializers.ModelSerializer):
    class Meta:
        model = SmartSprinkler
        fields = ["device_id", "zone", "status", "mode", "last_triggered_at", "updated_at"]
        # Relay state changes only through the toggle/master endpoints.
        read_only_fields = ["status", "last_triggered_at", "updated_at"]


class IncidentAlertSerializer(serializers.ModelSerializer):
    camera = serializers.SlugRelatedField(slug_field="feed_id", read_only=True)
    camera_name = serializers.CharField(source="camera.name", read_only=True, default=None)
    sensor = serializers.SlugRelatedField(slug_field="sensor_id", read_only=True)
    location = serializers.CharField(source="location_label", read_only=True)

    class Meta:
        model = IncidentAlert
        fields = [
            "id", "timestamp", "updated_at", "camera", "camera_name", "sensor", "source",
            "zone", "location", "severity", "fire_detected", "fire_severity", "human_count",
            "danger_count", "message", "resolved", "resolved_at", "notification_log",
        ]
        read_only_fields = fields


class PushSubscriptionSerializer(serializers.Serializer):
    endpoint = serializers.URLField(max_length=500)
    keys = serializers.DictField(child=serializers.CharField(max_length=255))

    def validate_keys(self, value):
        if not value.get("p256dh") or not value.get("auth"):
            raise serializers.ValidationError("Both 'p256dh' and 'auth' keys are required.")
        return value

    def save(self, **kwargs):
        request = self.context.get("request")
        user = request.user if request and request.user.is_authenticated else None
        sub, _ = PushSubscription.objects.update_or_create(
            endpoint=self.validated_data["endpoint"],
            defaults={
                "p256dh": self.validated_data["keys"]["p256dh"],
                "auth": self.validated_data["keys"]["auth"],
                "user": user,
                "user_agent": (request.headers.get("User-Agent", "") if request else "")[:255],
                "active": True,
            },
        )
        return sub


# --------------------------------------------------------------------------- commands
class TelemetrySerializer(serializers.Serializer):
    value = serializers.FloatField(min_value=-100.0, max_value=100000.0)
    recorded_at = serializers.DateTimeField(required=False)


class BulkTelemetryItemSerializer(TelemetrySerializer):
    sensor_id = serializers.SlugField(max_length=64)


class BulkTelemetrySerializer(serializers.Serializer):
    readings = BulkTelemetryItemSerializer(many=True, allow_empty=False, max_length=500)


class SprinklerToggleSerializer(serializers.Serializer):
    status = serializers.ChoiceField(choices=SmartSprinkler.Status.choices, required=False)
    mode = serializers.ChoiceField(choices=SmartSprinkler.Mode.choices, required=False)


class SprinklerMasterSerializer(serializers.Serializer):
    status = serializers.ChoiceField(choices=SmartSprinkler.Status.choices)
    zone = serializers.CharField(max_length=64, required=False, allow_blank=True)


class EmergencyDispatchSerializer(serializers.Serializer):
    zone = serializers.CharField(max_length=64)
    message = serializers.CharField(max_length=500, required=False, allow_blank=True)
    severity = serializers.ChoiceField(choices=Severity.choices, default=Severity.CRITICAL)
    activate_sprinklers = serializers.BooleanField(default=False)


# --------------------------------------------------------------------------- AI detection payload
class DetectionSerializer(serializers.Serializer):
    CATEGORIES = ["human", "fire", "smoke", "danger", "vehicle", "other"]

    label = serializers.CharField(max_length=48, required=False, allow_blank=True)
    category = serializers.ChoiceField(choices=CATEGORIES, default="other")
    confidence = serializers.FloatField(min_value=0.0, max_value=1.0, default=1.0)
    bbox = serializers.ListField(
        child=serializers.FloatField(), min_length=4, max_length=4,
        help_text="[x, y, width, height] in frame pixels (or 0..1 when normalized=true).",
    )
    track_id = serializers.CharField(max_length=48, required=False, allow_blank=True)
    at_risk = serializers.BooleanField(default=False)

    def validate_bbox(self, value):
        if value[2] < 0 or value[3] < 0:
            raise serializers.ValidationError("bbox width/height must be non-negative.")
        return value


class DangerZoneSerializer(serializers.Serializer):
    label = serializers.CharField(max_length=48, required=False, allow_blank=True)
    level = serializers.ChoiceField(choices=["warning", "danger", "critical"], default="danger")
    polygon = serializers.ListField(
        child=serializers.ListField(child=serializers.FloatField(), min_length=2, max_length=2),
        min_length=3, max_length=MAX_POLYGON_POINTS,
    )


class HeatmapSerializer(serializers.Serializer):
    rows = serializers.IntegerField(min_value=1, max_value=64)
    cols = serializers.IntegerField(min_value=1, max_value=64)
    values = serializers.ListField(
        child=serializers.FloatField(min_value=0.0, max_value=1.0), max_length=MAX_HEATMAP_CELLS
    )

    def validate(self, attrs):
        if len(attrs["values"]) != attrs["rows"] * attrs["cols"]:
            raise serializers.ValidationError("values length must equal rows * cols (row-major).")
        return attrs


class DetectionPayloadSerializer(serializers.Serializer):
    """Structured output produced by the external vision pipeline for a single frame."""

    camera_id = serializers.SlugField(max_length=64)
    timestamp = serializers.DateTimeField(required=False)
    frame_width = serializers.IntegerField(min_value=1, max_value=16384, default=1920)
    frame_height = serializers.IntegerField(min_value=1, max_value=16384, default=1080)
    normalized = serializers.BooleanField(default=False)
    detections = DetectionSerializer(many=True, required=False, max_length=MAX_DETECTIONS)
    danger_zones = DangerZoneSerializer(many=True, required=False, max_length=MAX_ZONES)
    heatmap = HeatmapSerializer(required=False, allow_null=True)
    human_coordinates = serializers.ListField(
        child=serializers.ListField(child=serializers.FloatField(), min_length=2, max_length=2),
        required=False, max_length=MAX_DETECTIONS,
    )
    fire_detected = serializers.BooleanField(required=False)
    fire_severity = serializers.FloatField(min_value=0.0, max_value=1.0, required=False, allow_null=True)
    human_count = serializers.IntegerField(min_value=0, required=False)
    danger_count = serializers.IntegerField(min_value=0, required=False)

    def validate(self, attrs):
        detections = attrs.setdefault("detections", [])
        attrs.setdefault("danger_zones", [])
        attrs.setdefault("human_coordinates", [])
        attrs.setdefault("heatmap", None)
        attrs.setdefault("fire_severity", None)
        attrs.setdefault("timestamp", timezone.now())

        humans = [d for d in detections if d["category"] == "human"]
        if "fire_detected" not in attrs:
            attrs["fire_detected"] = any(d["category"] == "fire" for d in detections)
        if "human_count" not in attrs:
            attrs["human_count"] = len(humans) or len(attrs["human_coordinates"])
        if "danger_count" not in attrs:
            attrs["danger_count"] = sum(
                1 for d in detections if d["category"] == "danger" or d.get("at_risk")
            )
        return attrs

    def to_broadcast(self):
        """JSON-safe dict for the WebSocket fan-out."""
        data = dict(self.validated_data)
        data["timestamp"] = data["timestamp"].isoformat()
        data["detections"] = [dict(d) for d in data["detections"]]
        data["danger_zones"] = [dict(z) for z in data["danger_zones"]]
        if data["heatmap"] is not None:
            data["heatmap"] = dict(data["heatmap"])
        return data
