from django.core.management.base import BaseCommand
from django.db import transaction

from monitoring.models import CameraFeed, IncidentAlert, SensorNode, SensorReading, SmartSprinkler

CAMERAS = [
    ("cam-lobby", "Main Lobby", CameraFeed.FeedType.CCTV, "Zone A"),
    ("cam-warehouse", "Warehouse Floor", CameraFeed.FeedType.CCTV, "Zone B"),
    ("cam-server", "Server Hall", CameraFeed.FeedType.CCTV, "Zone C"),
    ("drone-01", "Perimeter Drone 01", CameraFeed.FeedType.DRONE, "Zone B"),
]

ZONES = ["Zone A", "Zone B", "Zone C"]
SMOKE_THRESHOLD_PPM = 300.0
TEMP_THRESHOLD_C = 57.0  # standard glass-bulb sprinkler activation temperature

SPRINKLERS = [
    ("spr-a1", "Zone A"), ("spr-a2", "Zone A"),
    ("spr-b1", "Zone B"), ("spr-b2", "Zone B"),
    ("spr-c1", "Zone C"),
]


class Command(BaseCommand):
    help = "Seed demo cameras, sensors and sprinklers (idempotent)."

    def add_arguments(self, parser):
        parser.add_argument("--reset", action="store_true", help="Delete existing devices, readings and incidents first.")

    @transaction.atomic
    def handle(self, *args, **options):
        if options["reset"]:
            IncidentAlert.objects.all().delete()
            SensorReading.objects.all().delete()
            for model in (CameraFeed, SensorNode, SmartSprinkler):
                model.objects.all().delete()
            self.stdout.write(self.style.WARNING("Existing monitoring data removed."))

        for feed_id, name, feed_type, zone in CAMERAS:
            CameraFeed.objects.update_or_create(
                feed_id=feed_id,
                defaults={"name": name, "feed_type": feed_type, "location_zone": zone,
                          "stream_format": CameraFeed.StreamFormat.SIMULATED, "status": CameraFeed.Status.ONLINE},
            )

        for zone in ZONES:
            suffix = zone.split()[-1].lower()
            SensorNode.objects.update_or_create(
                sensor_id=f"smoke-{suffix}1",
                defaults={"type": SensorNode.SensorType.SMOKE, "threshold": SMOKE_THRESHOLD_PPM,
                          "location_zone": zone, "current_value": 18.0},
            )
            SensorNode.objects.update_or_create(
                sensor_id=f"temp-{suffix}1",
                defaults={"type": SensorNode.SensorType.TEMPERATURE, "threshold": TEMP_THRESHOLD_C,
                          "location_zone": zone, "current_value": 22.0},
            )

        for device_id, zone in SPRINKLERS:
            SmartSprinkler.objects.update_or_create(
                device_id=device_id,
                defaults={"zone": zone, "status": SmartSprinkler.Status.INACTIVE, "mode": SmartSprinkler.Mode.AUTO},
            )

        self.stdout.write(self.style.SUCCESS(
            f"Seeded {len(CAMERAS)} cameras, {len(ZONES) * 2} sensors, {len(SPRINKLERS)} sprinklers."
        ))
