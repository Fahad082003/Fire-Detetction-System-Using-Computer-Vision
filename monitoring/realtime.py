"""Channel-layer fan-out helpers shared by views, services and the notification engine."""
import logging

from asgiref.sync import async_to_sync
from channels.layers import get_channel_layer
from django.utils import timezone

logger = logging.getLogger(__name__)

LIVE_GROUP = "live_stream"


def _envelope(event_type, data, extra):
    return {
        "type": "broadcast.event",
        "payload": {"type": event_type, "data": data, "ts": timezone.now().isoformat(), **extra},
    }


def broadcast(event_type, data, **extra):
    """Send an event to every connected dashboard. Safe to call from sync code."""
    layer = get_channel_layer()
    if layer is None:
        return
    try:
        async_to_sync(layer.group_send)(LIVE_GROUP, _envelope(event_type, data, extra))
    except Exception:  # never let a fan-out failure break the request/alert pipeline
        logger.exception("Broadcast of %s event failed", event_type)


async def abroadcast(event_type, data, **extra):
    layer = get_channel_layer()
    if layer is None:
        return
    try:
        await layer.group_send(LIVE_GROUP, _envelope(event_type, data, extra))
    except Exception:
        logger.exception("Broadcast of %s event failed", event_type)


def log_event(message, level="info", zone=None):
    """Push a human-readable line to the dashboard event log."""
    broadcast("log", {"level": level, "message": message, "zone": zone})
