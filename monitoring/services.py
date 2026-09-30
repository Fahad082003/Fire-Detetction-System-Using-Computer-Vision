"""
Alert engine: turns detection frames and sensor telemetry into incidents, drives
automatic sprinkler activation and hands incidents to the notification dispatcher.
"""
import logging
from datetime import timedelta

from django.conf import settings
from django.core.cache import cache
from django.db import transaction
from django.utils import timezone

from .models import (
    CameraFeed,
    IncidentAlert,
    SensorNode,
    SensorReading,
    Severity,
    SmartSprinkler,
    severity_rank,
)
from .realtime import broadcast, log_event
from .serializers import (
    CameraFeedSerializer,
    DetectionPayloadSerializer,
    IncidentAlertSerializer,
    SensorNodeSerializer,
    SmartSprinklerSerializer,
)

logger = logging.getLogger(__name__)

# Vision frames arrive at video rate; re-evaluate a camera's incident at most this often
# unless the severity rises.
VISION_EVAL_INTERVAL_SECONDS = 2


def _cfg(key):
    return settings.SENTINEL[key]


# --------------------------------------------------------------------------- classification
def classify_detection(data):
    """Map a validated detection frame to a severity, or None when nothing is alert-worthy."""
    fire = data["fire_detected"]
    fire_sev = data.get("fire_severity") or 0.0
    humans = data["human_count"]
    danger = data["danger_count"]

    if not fire and danger == 0:
        return None
    if fire_sev >= 0.8 or danger >= 3 or (fire and humans > 0 and (danger > 0 or fire_sev >= 0.5)):
        return Severity.CRITICAL
    if (fire and (fire_sev >= 0.4 or humans > 0)) or danger >= 2:
        return Severity.HIGH
    return Severity.MEDIUM


def classify_sensor_state(state):
    if state == SensorNode.State.CRITICAL:
        return Severity.CRITICAL
    if state == SensorNode.State.ALARM:
        return Severity.HIGH
    return None


# --------------------------------------------------------------------------- incidents
def upsert_incident(
    *, source, zone, severity, camera=None, sensor=None, fire_detected=False,
    fire_severity=None, human_count=0, danger_count=0, message="",
):
    """
    Merge into the open incident for the same origin if it was touched within the merge
    window, otherwise open a new one. Returns (incident, action) where action is one of
    "created", "escalated" or "updated".
    """
    window_start = timezone.now() - timedelta(seconds=_cfg("INCIDENT_MERGE_WINDOW_SECONDS"))
    with transaction.atomic():
        qs = IncidentAlert.objects.select_for_update().filter(
            resolved=False, source=source, zone=zone, updated_at__gte=window_start
        )
        if camera is not None:
            qs = qs.filter(camera=camera)
        if sensor is not None:
            qs = qs.filter(sensor=sensor)
        incident = qs.order_by("-updated_at").first()

        if incident is None:
            incident = IncidentAlert.objects.create(
                source=source, zone=zone, severity=severity, camera=camera, sensor=sensor,
                fire_detected=fire_detected, fire_severity=fire_severity,
                human_count=human_count, danger_count=danger_count, message=message[:500],
            )
            return incident, "created"

        escalated = severity_rank(severity) > severity_rank(incident.severity)
        if escalated:
            incident.severity = severity
            incident.message = message[:500] or incident.message
        incident.fire_detected = incident.fire_detected or fire_detected
        if fire_severity is not None:
            incident.fire_severity = max(incident.fire_severity or 0.0, fire_severity)
        incident.human_count = max(incident.human_count, human_count)
        incident.danger_count = max(incident.danger_count, danger_count)
        incident.save()
        return incident, "escalated" if escalated else "updated"


def publish_incident(incident, action, force_notify=False):
    broadcast("incident", IncidentAlertSerializer(incident).data, action=action)
    if action not in ("created", "escalated"):
        return
    level = "critical" if incident.severity in (Severity.HIGH, Severity.CRITICAL) else "warning"
    verb = "opened" if action == "created" else "escalated to"
    log_event(
        f"Incident #{incident.pk} {verb} {incident.severity}: {incident.message or incident.location_label}",
        level=level, zone=incident.zone,
    )
    from .notifications import dispatcher  # local import: notifications depends on this module's models

    transaction.on_commit(lambda: dispatcher.dispatch(incident, force=force_notify))


def resolve_incident(incident, actor="operator"):
    if incident.resolved:
        return incident
    incident.resolved = True
    incident.resolved_at = timezone.now()
    incident.save(update_fields=["resolved", "resolved_at", "updated_at"])
    broadcast("incident", IncidentAlertSerializer(incident).data, action="resolved")
    log_event(f"Incident #{incident.pk} resolved by {actor}", level="success", zone=incident.zone)
    return incident


# --------------------------------------------------------------------------- vision pipeline
def validate_detection(payload):
    serializer = DetectionPayloadSerializer(data=payload)
    serializer.is_valid(raise_exception=True)
    return serializer


def evaluate_detection(data):
    """Alert routine for a validated frame. Runs only when the frame is alert-worthy."""
    severity = classify_detection(data)
    if severity is None:
        return None

    throttle_key = f"sentinel:vision-eval:{data['camera_id']}"
    last = cache.get(throttle_key)
    if last is not None and severity_rank(severity) <= severity_rank(last):
        return None
    cache.set(throttle_key, str(severity), timeout=VISION_EVAL_INTERVAL_SECONDS)

    camera = CameraFeed.objects.filter(feed_id=data["camera_id"]).first()
    zone = camera.location_zone if camera else "Unassigned"
    label = camera.name if camera else data["camera_id"]

    parts = []
    if data["fire_detected"]:
        pct = f" ({round((data.get('fire_severity') or 0) * 100)}%)" if data.get("fire_severity") else ""
        parts.append(f"Fire detected{pct}")
    if data["danger_count"]:
        parts.append(f"{data['danger_count']} threat(s)")
    if data["human_count"]:
        parts.append(f"{data['human_count']} human(s) in view")
    message = f"{', '.join(parts)} on {label}"

    incident, action = upsert_incident(
        source=IncidentAlert.Source.VISION, zone=zone, severity=severity, camera=camera,
        fire_detected=data["fire_detected"], fire_severity=data.get("fire_severity"),
        human_count=data["human_count"], danger_count=data["danger_count"], message=message,
    )
    publish_incident(incident, action)

    if data["fire_detected"] and severity in (Severity.HIGH, Severity.CRITICAL):
        auto_activate_sprinklers(zone, reason=f"vision incident #{incident.pk}")
    return incident


def process_detection(payload):
    """Synchronous path (REST ingestion): validate, fan out overlay data, evaluate alerts."""
    serializer = validate_detection(payload)
    broadcast("detection", serializer.to_broadcast())
    return evaluate_detection(serializer.validated_data)


# --------------------------------------------------------------------------- sensors
def _maybe_prune_readings():
    if cache.add("sentinel:prune-readings", 1, timeout=600):
        cutoff = timezone.now() - timedelta(hours=_cfg("SENSOR_HISTORY_RETENTION_HOURS"))
        deleted, _ = SensorReading.objects.filter(recorded_at__lt=cutoff).delete()
        if deleted:
            logger.info("Pruned %s sensor readings older than %s", deleted, cutoff)


def ingest_telemetry(sensor, value, recorded_at=None):
    recorded_at = recorded_at or timezone.now()
    previous_state = sensor.state

    sensor.current_value = round(float(value), 2)
    sensor.last_reading_at = recorded_at
    sensor.save(update_fields=["current_value", "last_reading_at", "updated_at"])
    SensorReading.objects.create(sensor=sensor, value=sensor.current_value, recorded_at=recorded_at)
    _maybe_prune_readings()

    broadcast("telemetry", SensorNodeSerializer(sensor).data)

    state = sensor.state
    if state != previous_state:
        level = {"normal": "success", "warning": "warning"}.get(state, "critical")
        log_event(
            f"{sensor.type} sensor {sensor.sensor_id} {previous_state} → {state} "
            f"({sensor.current_value}{sensor.unit})",
            level=level, zone=sensor.location_zone,
        )

    severity = classify_sensor_state(state)
    if severity is not None:
        message = (
            f"{sensor.type} {sensor.current_value}{sensor.unit} exceeds safety limit "
            f"{sensor.threshold:g}{sensor.unit} ({sensor.sensor_id})"
        )
        incident, action = upsert_incident(
            source=IncidentAlert.Source.SENSOR, zone=sensor.location_zone, severity=severity,
            sensor=sensor, message=message,
        )
        publish_incident(incident, action)
        auto_activate_sprinklers(sensor.location_zone, reason=f"{sensor.sensor_id} {state}")
    return sensor


def ingest_bulk_telemetry(readings):
    """readings: iterable of {"sensor_id", "value", "recorded_at"?}. Returns (updated, errors)."""
    by_id = {s.sensor_id: s for s in SensorNode.objects.filter(sensor_id__in=[r["sensor_id"] for r in readings])}
    updated, errors = [], []
    for reading in readings:
        sensor = by_id.get(reading["sensor_id"])
        if sensor is None:
            errors.append({"sensor_id": reading["sensor_id"], "error": "unknown sensor"})
            continue
        ingest_telemetry(sensor, reading["value"], reading.get("recorded_at"))
        updated.append(sensor.sensor_id)
    return updated, errors


# --------------------------------------------------------------------------- sprinklers
def set_sprinkler_state(sprinkler, *, status=None, mode=None, actor="operator", reason=""):
    changed = []
    if mode and mode != sprinkler.mode:
        sprinkler.mode = mode
        changed.append("mode")
    if status and status != sprinkler.status:
        sprinkler.status = status
        changed.append("status")
        if status == SmartSprinkler.Status.ACTIVE:
            sprinkler.last_triggered_at = timezone.now()
            changed.append("last_triggered_at")
    if not changed:
        return sprinkler, False

    sprinkler.save(update_fields=changed + ["updated_at"])
    broadcast("sprinkler", SmartSprinklerSerializer(sprinkler).data)

    detail = f" — {reason}" if reason else ""
    if "status" in changed:
        active = sprinkler.status == SmartSprinkler.Status.ACTIVE
        log_event(
            f"Sprinkler {sprinkler.device_id} {'ACTIVATED' if active else 'deactivated'} by {actor}{detail}",
            level="warning" if active else "info", zone=sprinkler.zone,
        )
    else:
        log_event(f"Sprinkler {sprinkler.device_id} set to {sprinkler.mode} mode by {actor}", zone=sprinkler.zone)
    return sprinkler, True


def toggle_sprinkler(sprinkler, *, status=None, mode=None, actor="operator"):
    if status is None and mode is None:
        status = (
            SmartSprinkler.Status.INACTIVE
            if sprinkler.status == SmartSprinkler.Status.ACTIVE
            else SmartSprinkler.Status.ACTIVE
        )
    return set_sprinkler_state(sprinkler, status=status, mode=mode, actor=actor)[0]


def set_all_sprinklers(status, *, zone=None, actor="operator", reason=""):
    qs = SmartSprinkler.objects.all()
    if zone:
        qs = qs.filter(zone__iexact=zone)
    changed = [s for s in qs if set_sprinkler_state(s, status=status, actor=actor, reason=reason)[1]]
    scope = f"zone {zone}" if zone else "all zones"
    log_event(
        f"Master sprinkler switch → {status} ({scope}, {len(changed)} device(s)) by {actor}",
        level="critical" if status == SmartSprinkler.Status.ACTIVE else "info", zone=zone,
    )
    return changed


def auto_activate_sprinklers(zone, reason):
    targets = SmartSprinkler.objects.filter(
        zone__iexact=zone, mode=SmartSprinkler.Mode.AUTO, status=SmartSprinkler.Status.INACTIVE
    )
    return [
        s for s in targets
        if set_sprinkler_state(s, status=SmartSprinkler.Status.ACTIVE, actor="auto-engine", reason=reason)[1]
    ]


# --------------------------------------------------------------------------- manual dispatch
def manual_dispatch(*, zone, message="", severity=Severity.CRITICAL, activate_sprinklers=False, actor="operator"):
    incident = IncidentAlert.objects.create(
        source=IncidentAlert.Source.MANUAL, zone=zone, severity=severity,
        message=(message or f"Manual emergency dispatch initiated by {actor}")[:500],
    )
    publish_incident(incident, "created", force_notify=True)
    if activate_sprinklers:
        set_all_sprinklers(SmartSprinkler.Status.ACTIVE, zone=zone, actor=actor, reason=f"dispatch #{incident.pk}")
    return incident


# --------------------------------------------------------------------------- snapshot
def system_status():
    open_qs = IncidentAlert.objects.filter(resolved=False)
    top = max((severity_rank(s) for s in open_qs.values_list("severity", flat=True)), default=-1)
    sensor_states = {s.state for s in SensorNode.objects.all()}

    if top >= severity_rank(Severity.HIGH) or sensor_states & {"alarm", "critical"}:
        level = "critical"
    elif top >= severity_rank(Severity.LOW) or "warning" in sensor_states:
        level = "elevated"
    else:
        level = "nominal"
    return {
        "level": level,
        "open_incidents": open_qs.count(),
        "active_sprinklers": SmartSprinkler.objects.filter(status=SmartSprinkler.Status.ACTIVE).count(),
    }


def build_snapshot():
    limit = _cfg("SENSOR_HISTORY_POINTS")
    sensors = list(SensorNode.objects.all())
    sensor_data = SensorNodeSerializer(sensors, many=True).data
    for item, sensor in zip(sensor_data, sensors):
        values = list(sensor.readings.order_by("-recorded_at").values_list("value", flat=True)[:limit])
        item["history"] = values[::-1]

    return {
        "cameras": CameraFeedSerializer(CameraFeed.objects.all(), many=True).data,
        "sensors": sensor_data,
        "sprinklers": SmartSprinklerSerializer(SmartSprinkler.objects.all(), many=True).data,
        "incidents": IncidentAlertSerializer(
            IncidentAlert.objects.select_related("camera", "sensor")[:50], many=True
        ).data,
        "system": system_status(),
        "server_time": timezone.now().isoformat(),
    }
