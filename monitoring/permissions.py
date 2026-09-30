import hmac

from django.conf import settings
from rest_framework.permissions import BasePermission


def open_access():
    return bool(settings.SENTINEL["OPEN_ACCESS"])


def is_valid_device_key(candidate):
    configured = settings.SENTINEL["DEVICE_API_KEY"]
    if not configured or not candidate:
        return False
    return hmac.compare_digest(str(candidate).encode(), configured.encode())


class OperatorOrDevice(BasePermission):
    """Authenticated operators (session) or devices presenting X-Device-Key."""

    message = "Operator session or valid X-Device-Key required."

    def has_permission(self, request, view):
        if open_access():
            return True
        if request.user and request.user.is_authenticated:
            return True
        return is_valid_device_key(request.headers.get("X-Device-Key"))


class OperatorOnly(BasePermission):
    """Actions that must be performed by a human operator (e.g. emergency dispatch)."""

    message = "Operator session required."

    def has_permission(self, request, view):
        if open_access():
            return True
        return bool(request.user and request.user.is_authenticated)
