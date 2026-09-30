from django.contrib import admin

from .models import CameraFeed, IncidentAlert, PushSubscription, SensorNode, SensorReading, SmartSprinkler


@admin.register(CameraFeed)
class CameraFeedAdmin(admin.ModelAdmin):
    list_display = ("feed_id", "name", "feed_type", "stream_format", "status", "location_zone")
    list_filter = ("feed_type", "stream_format", "status", "location_zone")
    search_fields = ("feed_id", "name")


@admin.register(SensorNode)
class SensorNodeAdmin(admin.ModelAdmin):
    list_display = ("sensor_id", "type", "current_value", "threshold", "location_zone", "last_reading_at")
    list_filter = ("type", "location_zone")
    search_fields = ("sensor_id",)


@admin.register(SensorReading)
class SensorReadingAdmin(admin.ModelAdmin):
    list_display = ("sensor", "value", "recorded_at")
    list_filter = ("sensor__type",)
    date_hierarchy = "recorded_at"


@admin.register(SmartSprinkler)
class SmartSprinklerAdmin(admin.ModelAdmin):
    list_display = ("device_id", "zone", "status", "mode", "last_triggered_at")
    list_filter = ("status", "mode", "zone")


@admin.register(IncidentAlert)
class IncidentAlertAdmin(admin.ModelAdmin):
    list_display = ("id", "timestamp", "severity", "source", "zone", "camera", "fire_detected",
                    "human_count", "danger_count", "resolved")
    list_filter = ("severity", "source", "resolved", "fire_detected", "zone")
    search_fields = ("message", "zone")
    date_hierarchy = "timestamp"


@admin.register(PushSubscription)
class PushSubscriptionAdmin(admin.ModelAdmin):
    list_display = ("endpoint", "user", "active", "created_at")
    list_filter = ("active",)
