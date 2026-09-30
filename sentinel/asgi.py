"""ASGI entrypoint: HTTP via Django, WebSockets via Channels."""
import os

from django.core.asgi import get_asgi_application

os.environ.setdefault("DJANGO_SETTINGS_MODULE", "sentinel.settings")

# Initialise Django before importing anything that touches models.
django_asgi_app = get_asgi_application()

from channels.auth import AuthMiddlewareStack  # noqa: E402
from channels.routing import ProtocolTypeRouter, URLRouter  # noqa: E402

from monitoring.middleware import DeviceOrOriginValidator  # noqa: E402
from monitoring.routing import websocket_urlpatterns  # noqa: E402

application = ProtocolTypeRouter(
    {
        "http": django_asgi_app,
        "websocket": DeviceOrOriginValidator(AuthMiddlewareStack(URLRouter(websocket_urlpatterns))),
    }
)
