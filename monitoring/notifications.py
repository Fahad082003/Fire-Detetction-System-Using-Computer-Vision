"""
Notification dispatch service: Twilio SMS, SMTP/Django email and Web Push.

Dispatch is non-blocking: incidents are handed to a small thread pool so the WebSocket
and REST pipelines never wait on third-party APIs. A cache-backed cooldown suppresses
duplicate alerts for the same origin/zone/severity.
"""
import json
import logging
import threading
from concurrent.futures import ThreadPoolExecutor
from dataclasses import asdict, dataclass

from django.conf import settings
from django.core.cache import cache
from django.core.mail import EmailMultiAlternatives
from django.db import close_old_connections, transaction
from django.template.loader import render_to_string
from django.utils import timezone

from .models import IncidentAlert, PushSubscription, Severity, severity_rank
from .realtime import broadcast

logger = logging.getLogger(__name__)


# --------------------------------------------------------------------------- context
@dataclass(frozen=True)
class AlertContext:
    incident_id: int
    severity: str
    zone: str
    location: str
    source: str
    fire_detected: bool
    fire_severity: float | None
    human_count: int
    danger_count: int
    message: str
    timestamp: str
    site_name: str
    dashboard_url: str

    @classmethod
    def from_incident(cls, incident):
        cfg = settings.SENTINEL
        return cls(
            incident_id=incident.pk,
            severity=str(incident.severity),
            zone=incident.zone,
            location=incident.location_label,
            source=str(incident.source),
            fire_detected=incident.fire_detected,
            fire_severity=incident.fire_severity,
            human_count=incident.human_count,
            danger_count=incident.danger_count,
            message=incident.message,
            timestamp=timezone.localtime(incident.timestamp).strftime("%Y-%m-%d %H:%M:%S %Z"),
            site_name=cfg["SITE_NAME"],
            dashboard_url=cfg["DASHBOARD_URL"],
        )

    @property
    def headline(self):
        if self.source == IncidentAlert.Source.MANUAL:
            return "EMERGENCY DISPATCH"
        if self.fire_detected:
            return "FIRE DETECTED"
        if self.danger_count:
            return "THREAT DETECTED"
        if self.source == IncidentAlert.Source.SENSOR:
            return "SENSOR SAFETY LIMIT EXCEEDED"
        return "INCIDENT"

    @property
    def fire_severity_pct(self):
        return None if self.fire_severity is None else round(self.fire_severity * 100)

    def as_template_context(self):
        return {**asdict(self), "headline": self.headline, "fire_severity_pct": self.fire_severity_pct}


def _result(ok, delivered=0, failed=0, detail=""):
    return {"ok": ok, "delivered": delivered, "failed": failed, "detail": detail}


# --------------------------------------------------------------------------- channels
class NotificationChannel:
    name = "base"
    min_severity_setting = None

    def enabled(self):
        raise NotImplementedError

    def accepts(self, ctx, force=False):
        if force:
            return True
        minimum = settings.SENTINEL.get(self.min_severity_setting, Severity.LOW)
        return severity_rank(ctx.severity) >= severity_rank(minimum)

    def send(self, ctx):
        raise NotImplementedError


class TwilioSMSChannel(NotificationChannel):
    name = "sms"
    min_severity_setting = "SMS_MIN_SEVERITY"

    @property
    def recipients(self):
        return settings.SENTINEL["SMS_RECIPIENTS"]

    def enabled(self):
        return bool(
            settings.TWILIO_ACCOUNT_SID and settings.TWILIO_AUTH_TOKEN
            and settings.TWILIO_FROM_NUMBER and self.recipients
        )

    @staticmethod
    def render(ctx):
        lines = [
            f"[{ctx.site_name}] {ctx.severity.upper()} - {ctx.headline}",
            f"Location: {ctx.location}",
            f"Humans in area: {ctx.human_count} | Threats: {ctx.danger_count}",
        ]
        if ctx.fire_severity_pct is not None:
            lines.append(f"Fire severity: {ctx.fire_severity_pct}%")
        if ctx.message:
            lines.append(ctx.message)
        lines.append(f"Incident #{ctx.incident_id} at {ctx.timestamp}")
        lines.append(ctx.dashboard_url)
        return "\n".join(lines)[:1500]

    def send(self, ctx):
        from twilio.base.exceptions import TwilioRestException
        from twilio.rest import Client

        client = Client(settings.TWILIO_ACCOUNT_SID, settings.TWILIO_AUTH_TOKEN)
        body = self.render(ctx)
        delivered, errors = 0, []
        for number in self.recipients:
            try:
                client.messages.create(body=body, from_=settings.TWILIO_FROM_NUMBER, to=number)
                delivered += 1
            except TwilioRestException as exc:
                logger.warning("Twilio SMS to %s failed: %s", number, exc.msg)
                errors.append(f"{number}: {exc.msg}")
        return _result(delivered > 0, delivered, len(errors), "; ".join(errors)[:500])


class EmailChannel(NotificationChannel):
    name = "email"
    min_severity_setting = "EMAIL_MIN_SEVERITY"

    @property
    def recipients(self):
        return settings.SENTINEL["EMAIL_RECIPIENTS"]

    def enabled(self):
        return bool(self.recipients)

    def send(self, ctx):
        template_ctx = ctx.as_template_context()
        subject = f"[{ctx.site_name}] {ctx.severity.upper()}: {ctx.headline} - {ctx.zone} (#{ctx.incident_id})"
        message = EmailMultiAlternatives(
            subject=subject,
            body=render_to_string("monitoring/email/incident_alert.txt", template_ctx),
            from_email=settings.DEFAULT_FROM_EMAIL,
            to=self.recipients,
        )
        message.attach_alternative(render_to_string("monitoring/email/incident_alert.html", template_ctx), "text/html")
        message.extra_headers["X-Priority"] = "1" if ctx.severity in (Severity.HIGH, Severity.CRITICAL) else "3"
        sent = message.send(fail_silently=False)
        return _result(bool(sent), len(self.recipients) if sent else 0, 0 if sent else len(self.recipients))


class WebPushChannel(NotificationChannel):
    name = "push"
    min_severity_setting = "PUSH_MIN_SEVERITY"

    def enabled(self):
        return bool(settings.VAPID_PRIVATE_KEY and settings.VAPID_PUBLIC_KEY)

    @staticmethod
    def build_payload(ctx):
        return {
            "title": f"{ctx.severity.upper()} · {ctx.headline}",
            "body": f"{ctx.location} — humans: {ctx.human_count}, threats: {ctx.danger_count}",
            "tag": f"incident-{ctx.incident_id}",
            "severity": ctx.severity,
            "incident_id": ctx.incident_id,
            "url": ctx.dashboard_url,
            "requireInteraction": ctx.severity in (Severity.HIGH, Severity.CRITICAL),
        }

    def send(self, ctx):
        from pywebpush import WebPushException, webpush

        data = json.dumps(self.build_payload(ctx))
        delivered, failed, stale = 0, 0, []
        for sub in PushSubscription.objects.filter(active=True).iterator():
            try:
                webpush(
                    subscription_info=sub.as_subscription_info(),
                    data=data,
                    vapid_private_key=settings.VAPID_PRIVATE_KEY,
                    vapid_claims={"sub": settings.VAPID_CLAIM_EMAIL},  # fresh dict: pywebpush mutates it
                    ttl=3600,
                    timeout=10,
                )
                delivered += 1
            except WebPushException as exc:
                failed += 1
                status = getattr(exc.response, "status_code", None)
                if status in (404, 410):
                    stale.append(sub.pk)
                else:
                    logger.warning("Web push to %s failed: %s", sub.endpoint[:60], exc)
        if stale:
            PushSubscription.objects.filter(pk__in=stale).update(active=False)
        detail = f"{len(stale)} expired subscription(s) removed" if stale else ""
        return _result(delivered > 0 or failed == 0, delivered, failed, detail)


# --------------------------------------------------------------------------- dispatcher
class NotificationDispatcher:
    def __init__(self, channels=None):
        self.channels = channels if channels is not None else [TwilioSMSChannel(), EmailChannel(), WebPushChannel()]
        self._executor = None
        self._lock = threading.Lock()

    @property
    def executor(self):
        with self._lock:
            if self._executor is None:
                self._executor = ThreadPoolExecutor(
                    max_workers=settings.SENTINEL["NOTIFICATION_WORKERS"], thread_name_prefix="sentinel-notify"
                )
            return self._executor

    def _cooldown_key(self, ctx):
        return f"sentinel:notify:{ctx.source}:{ctx.zone}:{ctx.severity}"

    def dispatch(self, incident, force=False):
        """Queue delivery for an incident. Returns a Future, or None when suppressed by cooldown."""
        ctx = AlertContext.from_incident(incident)
        if not force and not cache.add(self._cooldown_key(ctx), ctx.incident_id, settings.SENTINEL["ALERT_COOLDOWN_SECONDS"]):
            logger.info("Notification for incident #%s suppressed by cooldown", ctx.incident_id)
            return None
        return self.executor.submit(self.deliver, ctx, force)

    def deliver(self, ctx, force=False):
        close_old_connections()
        results = []
        try:
            for channel in self.channels:
                if not channel.accepts(ctx, force):
                    continue
                if not channel.enabled():
                    results.append({"channel": channel.name, "ok": False, "skipped": True, "detail": "not configured"})
                    continue
                try:
                    outcome = channel.send(ctx)
                except Exception as exc:
                    logger.exception("%s notification for incident #%s failed", channel.name, ctx.incident_id)
                    outcome = _result(False, detail=str(exc)[:500])
                outcome.update(channel=channel.name, at=timezone.now().isoformat())
                results.append(outcome)
                broadcast("notification", {"incident_id": ctx.incident_id, "severity": ctx.severity, **outcome})
            self._record(ctx.incident_id, results)
            return results
        finally:
            close_old_connections()

    @staticmethod
    def _record(incident_id, results):
        if not results:
            return
        with transaction.atomic():
            incident = IncidentAlert.objects.select_for_update().filter(pk=incident_id).first()
            if incident is None:
                return
            incident.notification_log = (incident.notification_log or []) + results
            incident.save(update_fields=["notification_log"])


dispatcher = NotificationDispatcher()
