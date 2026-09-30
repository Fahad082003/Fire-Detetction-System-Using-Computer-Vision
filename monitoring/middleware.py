from urllib.parse import parse_qs

from channels.security.websocket import AllowedHostsOriginValidator

from .permissions import is_valid_device_key


def extract_device_token(scope):
    headers = dict(scope.get("headers") or [])
    token = headers.get(b"x-device-key", b"").decode(errors="ignore")
    if token:
        return token
    query = parse_qs(scope.get("query_string", b"").decode(errors="ignore"))
    return (query.get("token") or [""])[0]


class DeviceOrOriginValidator:
    """
    Browsers must pass the AllowedHosts origin check. Headless producers (AI pipelines,
    IoT gateways) usually send no Origin header, so they are admitted when they present
    a valid device key instead.
    """

    def __init__(self, app):
        self.app = app
        self.origin_checked_app = AllowedHostsOriginValidator(app)

    async def __call__(self, scope, receive, send):
        if scope["type"] == "websocket" and is_valid_device_key(extract_device_token(scope)):
            return await self.app(dict(scope, device_authenticated=True), receive, send)
        return await self.origin_checked_app(dict(scope, device_authenticated=False), receive, send)
