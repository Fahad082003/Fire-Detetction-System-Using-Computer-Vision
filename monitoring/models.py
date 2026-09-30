from django.conf import settings
from django.db import models
from django.utils import timezone


class Severity(models.TextChoices):
    LOW = "Low", "Low"
    MEDIUM = "Medium", "Medium"
    HIGH = "High", "High"
    CRITICAL = "Critical", "Critical"


SEVERITY_RANK = {Severity.LOW: 0, Severity.MEDIUM: 1, Severity.HIGH: 2, Severity.CRITICAL: 3}


def severity_rank(value):
    return SEVERITY_RANK.get(value, -1)


class CameraFeed(models.Model):
    class FeedType(models.TextChoices):
        CCTV = "CCTV", "CCTV"
        DRONE = "Drone", "Drone"

    class StreamFormat(models.TextChoices):
        MJPEG = "MJPEG", "MJPEG"
        HLS = "HLS", "HLS"
        MP4 = "MP4", "MP4 / File"
        SIMULATED = "SIM", "Simulated"

    class Status(models.TextChoices):
        ONLINE = "Online", "Online"
        DEGRADED = "Degraded", "Degraded"
        OFFLINE = "Offline", "Offline"

    feed_id = models.SlugField(max_length=64, unique=True)
    name = models.CharField(max_length=120)
    feed_type = models.CharField(max_length=8, choices=FeedType.choices, default=FeedType.CCTV)
    stream_url = models.CharField(max_length=500, blank=True)
    stream_format = models.CharField(
        max_length=8, choices=StreamFormat.choices, default=StreamFormat.SIMULATED
    )
    status = models.CharField(max_length=10, choices=Status.choices, default=Status.ONLINE)
    location_zone = models.CharField(max_length=64, db_index=True)
    created_at = models.DateTimeField(auto_now_add=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        ordering = ["feed_type", "name"]

    def __str__(self):
        return f"{self.name} ({self.feed_id})"


class SensorNode(models.Model):
    class SensorType(models.TextChoices):
        SMOKE = "Smoke", "Smoke"
        TEMPERATURE = "Temperature", "Temperature"

    class State(models.TextChoices):
        NORMAL = "normal", "Normal"
        WARNING = "warning", "Warning"
        ALARM = "alarm", "Alarm"
        CRITICAL = "critical", "Critical"

    UNITS = {SensorType.SMOKE: "ppm", SensorType.TEMPERATURE: "°C"}

    sensor_id = models.SlugField(max_length=64, unique=True)
    type = models.CharField(max_length=16, choices=SensorType.choices)
    current_value = models.FloatField(default=0.0)
    threshold = models.FloatField()
    location_zone = models.CharField(max_length=64, db_index=True)
    last_reading_at = models.DateTimeField(null=True, blank=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        ordering = ["location_zone", "type", "sensor_id"]

    def __str__(self):
        return f"{self.sensor_id} [{self.type} @ {self.location_zone}]"

    @property
    def unit(self):
        return self.UNITS.get(self.type, "")

    @property
    def critical_threshold(self):
        cfg = settings.SENTINEL
        multiplier = (
            cfg["SMOKE_CRITICAL_MULTIPLIER"]
            if self.type == self.SensorType.SMOKE
            else cfg["TEMP_CRITICAL_MULTIPLIER"]
        )
        return self.threshold * multiplier

    @property
    def state(self):
        value = self.current_value
        if value >= self.critical_threshold:
            return self.State.CRITICAL
        if value >= self.threshold:
            return self.State.ALARM
        if value >= self.threshold * settings.SENTINEL["WARNING_RATIO"]:
            return self.State.WARNING
        return self.State.NORMAL


class SensorReading(models.Model):
    sensor = models.ForeignKey(SensorNode, on_delete=models.CASCADE, related_name="readings")
    value = models.FloatField()
    recorded_at = models.DateTimeField(default=timezone.now)

    class Meta:
        ordering = ["-recorded_at"]
        indexes = [models.Index(fields=["sensor", "-recorded_at"])]


class SmartSprinkler(models.Model):
    class Status(models.TextChoices):
        ACTIVE = "Active", "Active"
        INACTIVE = "Inactive", "Inactive"

    class Mode(models.TextChoices):
        AUTO = "Auto", "Auto"
        MANUAL = "Manual", "Manual"

    device_id = models.SlugField(max_length=64, unique=True)
    zone = models.CharField(max_length=64, db_index=True)
    status = models.CharField(max_length=8, choices=Status.choices, default=Status.INACTIVE)
    mode = models.CharField(max_length=8, choices=Mode.choices, default=Mode.AUTO)
    last_triggered_at = models.DateTimeField(null=True, blank=True)
    updated_at = models.DateTimeField(auto_now=True)

    class Meta:
        ordering = ["zone", "device_id"]

    def __str__(self):
        return f"{self.device_id} [{self.zone}] {self.status}"


class IncidentAlert(models.Model):
    class Source(models.TextChoices):
        VISION = "vision", "Vision (AI)"
        SENSOR = "sensor", "Sensor"
        MANUAL = "manual", "Manual dispatch"

    timestamp = models.DateTimeField(default=timezone.now, db_index=True)
    updated_at = models.DateTimeField(auto_now=True)
    camera = models.ForeignKey(
        CameraFeed, null=True, blank=True, on_delete=models.SET_NULL, related_name="incidents"
    )
    sensor = models.ForeignKey(
        SensorNode, null=True, blank=True, on_delete=models.SET_NULL, related_name="incidents"
    )
    source = models.CharField(max_length=8, choices=Source.choices, default=Source.VISION)
    zone = models.CharField(max_length=64, db_index=True)
    severity = models.CharField(max_length=8, choices=Severity.choices, default=Severity.LOW)
    fire_detected = models.BooleanField(default=False)
    fire_severity = models.FloatField(null=True, blank=True)
    human_count = models.PositiveIntegerField(default=0)
    danger_count = models.PositiveIntegerField(default=0)
    message = models.CharField(max_length=500, blank=True)
    resolved = models.BooleanField(default=False, db_index=True)
    resolved_at = models.DateTimeField(null=True, blank=True)
    notification_log = models.JSONField(default=list, blank=True)

    class Meta:
        ordering = ["-timestamp"]
        indexes = [models.Index(fields=["resolved", "-timestamp"])]

    def __str__(self):
        return f"#{self.pk} {self.severity} @ {self.zone}"

    @property
    def location_label(self):
        if self.camera_id and self.camera:
            return f"{self.zone} ({self.camera.name})"
        if self.sensor_id and self.sensor:
            return f"{self.zone} (sensor {self.sensor.sensor_id})"
        return self.zone


class PushSubscription(models.Model):
    endpoint = models.CharField(max_length=500, unique=True)
    p256dh = models.CharField(max_length=255)
    auth = models.CharField(max_length=255)
    user = models.ForeignKey(
        settings.AUTH_USER_MODEL, null=True, blank=True, on_delete=models.CASCADE
    )
    user_agent = models.CharField(max_length=255, blank=True)
    active = models.BooleanField(default=True)
    created_at = models.DateTimeField(auto_now_add=True)

    def as_subscription_info(self):
        return {"endpoint": self.endpoint, "keys": {"p256dh": self.p256dh, "auth": self.auth}}
