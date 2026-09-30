from unittest import mock

from channels.auth import AuthMiddlewareStack
from channels.routing import URLRouter
from channels.testing import WebsocketCommunicator
from django.conf import settings
from django.core.cache import cache
from django.test import TestCase, TransactionTestCase, override_settings
from rest_framework.test import APIClient

from . import services
from .middleware import DeviceOrOriginValidator
from .models import CameraFeed, IncidentAlert, SensorNode, Severity, SmartSprinkler
from .routing import websocket_urlpatterns

TEST_SENTINEL = {**settings.SENTINEL, "OPEN_ACCESS": False, "DEVICE_API_KEY": "test-key"}


def frame(**overrides):
    base = {"fire_detected": False, "fire_severity": None, "human_count": 0, "danger_count": 0}
    return {**base, **overrides}


class ClassificationTests(TestCase):
    def test_nothing_alertworthy(self):
        self.assertIsNone(services.classify_detection(frame(human_count=5)))

    def test_danger_only_is_medium(self):
        self.assertEqual(services.classify_detection(frame(danger_count=1)), Severity.MEDIUM)

    def test_fire_with_humans_is_high(self):
        self.assertEqual(services.classify_detection(frame(fire_detected=True, human_count=2)), Severity.HIGH)

    def test_fire_with_humans_at_risk_is_critical(self):
        result = services.classify_detection(frame(fire_detected=True, human_count=2, danger_count=1))
        self.assertEqual(result, Severity.CRITICAL)

    def test_intense_fire_is_critical(self):
        self.assertEqual(services.classify_detection(frame(fire_detected=True, fire_severity=0.9)), Severity.CRITICAL)


@override_settings(SENTINEL=TEST_SENTINEL)
@mock.patch("monitoring.notifications.dispatcher.dispatch")
class AlertEngineTests(TestCase):
    def setUp(self):
        cache.clear()
        self.sensor = SensorNode.objects.create(sensor_id="temp-b1", type="Temperature", threshold=57, location_zone="Zone B")
        self.auto = SmartSprinkler.objects.create(device_id="spr-b1", zone="Zone B")
        self.manual = SmartSprinkler.objects.create(device_id="spr-b2", zone="Zone B", mode="Manual")
        self.client = APIClient()
        self.client.credentials(HTTP_X_DEVICE_KEY="test-key")

    def test_sensor_over_limit_opens_incident_and_triggers_auto_sprinklers(self, _dispatch):
        with self.captureOnCommitCallbacks(execute=True):
            services.ingest_telemetry(self.sensor, 64.0)
        incident = IncidentAlert.objects.get()
        self.assertEqual(incident.severity, Severity.HIGH)
        self.assertEqual(incident.source, IncidentAlert.Source.SENSOR)
        self.auto.refresh_from_db()
        self.manual.refresh_from_db()
        self.assertEqual(self.auto.status, "Active")
        self.assertIsNotNone(self.auto.last_triggered_at)
        self.assertEqual(self.manual.status, "Inactive")
        _dispatch.assert_called_once()

    def test_repeated_readings_merge_and_escalate(self, _dispatch):
        services.ingest_telemetry(self.sensor, 60.0)
        services.ingest_telemetry(self.sensor, 61.0)
        services.ingest_telemetry(self.sensor, 80.0)  # >= 57 * 1.25 -> critical
        self.assertEqual(IncidentAlert.objects.count(), 1)
        self.assertEqual(IncidentAlert.objects.get().severity, Severity.CRITICAL)

    def test_telemetry_endpoint_requires_device_key(self, _dispatch):
        anon = APIClient()
        resp = anon.post("/api/telemetry/", {"readings": [{"sensor_id": "temp-b1", "value": 30}]}, format="json")
        self.assertEqual(resp.status_code, 403)
        resp = self.client.post("/api/telemetry/", {"readings": [{"sensor_id": "temp-b1", "value": 30}]}, format="json")
        self.assertEqual(resp.status_code, 200)
        self.sensor.refresh_from_db()
        self.assertEqual(self.sensor.current_value, 30)

    def test_sprinkler_toggle_and_master(self, _dispatch):
        resp = self.client.post("/api/sprinklers/spr-b2/toggle/", {}, format="json")
        self.assertEqual(resp.json()["status"], "Active")
        resp = self.client.post("/api/sprinklers/spr-b2/toggle/", {"mode": "Auto"}, format="json")
        self.assertEqual(resp.json()["mode"], "Auto")
        resp = self.client.post("/api/sprinklers/master/", {"status": "Inactive"}, format="json")
        self.assertEqual(resp.status_code, 200)
        self.assertFalse(SmartSprinkler.objects.filter(status="Active").exists())

    def test_detection_rest_ingestion_creates_vision_incident(self, _dispatch):
        CameraFeed.objects.create(feed_id="cam-b", name="Warehouse", location_zone="Zone B")
        payload = {
            "camera_id": "cam-b", "frame_width": 1280, "frame_height": 720,
            "detections": [
                {"category": "fire", "confidence": 0.9, "bbox": [100, 100, 200, 200]},
                {"category": "human", "confidence": 0.95, "bbox": [400, 300, 60, 150], "at_risk": True},
            ],
        }
        resp = self.client.post("/api/detections/", payload, format="json")
        self.assertEqual(resp.status_code, 202)
        incident = IncidentAlert.objects.get()
        self.assertEqual(incident.severity, Severity.CRITICAL)
        self.assertTrue(incident.fire_detected)
        self.assertEqual((incident.human_count, incident.danger_count), (1, 1))

    def test_invalid_detection_rejected(self, _dispatch):
        resp = self.client.post("/api/detections/", {"camera_id": "cam-b", "detections": [{"bbox": [1, 2]}]}, format="json")
        self.assertEqual(resp.status_code, 400)


@override_settings(SENTINEL=TEST_SENTINEL)
class LiveStreamConsumerTests(TransactionTestCase):
    def app(self):
        return DeviceOrOriginValidator(AuthMiddlewareStack(URLRouter(websocket_urlpatterns)))

    async def test_anonymous_browser_is_rejected(self):
        comm = WebsocketCommunicator(self.app(), "/ws/live-stream/", headers=[(b"origin", b"http://localhost")])
        connected, _ = await comm.connect()
        self.assertFalse(connected)

    @mock.patch("monitoring.notifications.dispatcher.dispatch")
    async def test_device_detection_is_broadcast(self, _dispatch):
        producer = WebsocketCommunicator(self.app(), "/ws/live-stream/?token=test-key")
        connected, _ = await producer.connect()
        self.assertTrue(connected)
        snapshot = await producer.receive_json_from()
        self.assertEqual(snapshot["type"], "snapshot")

        await producer.send_json_to({
            "type": "detection", "camera_id": "cam-x", "normalized": True,
            "detections": [{"category": "human", "bbox": [0.1, 0.1, 0.2, 0.4], "track_id": "p1"}],
        })
        message = await producer.receive_json_from(timeout=2)
        self.assertEqual(message["type"], "detection")
        self.assertEqual(message["data"]["human_count"], 1)
        await producer.disconnect()
