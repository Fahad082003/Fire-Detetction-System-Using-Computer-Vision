from django.urls import include, path
from rest_framework.routers import DefaultRouter

from . import views

router = DefaultRouter()
router.register("cameras", views.CameraFeedViewSet, basename="camera")
router.register("sensors", views.SensorNodeViewSet, basename="sensor")
router.register("sprinklers", views.SmartSprinklerViewSet, basename="sprinkler")
router.register("incidents", views.IncidentAlertViewSet, basename="incident")

urlpatterns = [
    path("cameras/<slug:feed_id>/stream/", views.camera_stream_proxy, name="camera-stream"),
    path("telemetry/", views.BulkTelemetryView.as_view(), name="telemetry-bulk"),
    path("detections/", views.DetectionIngestView.as_view(), name="detections"),
    path("dispatch/", views.EmergencyDispatchView.as_view(), name="dispatch"),
    path("system/snapshot/", views.SystemSnapshotView.as_view(), name="snapshot"),
    path("push/", views.PushSubscriptionView.as_view(), name="push"),
    path("", include(router.urls)),
]
