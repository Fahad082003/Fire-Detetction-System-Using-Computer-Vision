# Sentinel — Emergency Monitoring Platform

Realtime command center for fire/threat monitoring. It takes in structured detections from external AI vision pipelines, telemetry from smoke and temperature sensors, and smart-sprinkler relay state. From these it opens incidents, fires the sprinklers automatically, and alerts responders by SMS, email and Web Push.

```
┌─────────────────┐  WS detection JSON   ┌──────────────────────────────────────┐   WS fan-out   ┌──────────────────┐
│ AI vision procs │ ───────────────────▶ │ Django + DRF + Channels (ASGI)       │ ─────────────▶ │ Vanilla JS       │
├─────────────────┤  REST telemetry      │  consumers ─▶ services (alert engine)│                │ dashboard        │
│ IoT gateways    │ ───────────────────▶ │                 ├─▶ sprinklers (auto)│ ◀───────────── │ (canvas overlay, │
├─────────────────┤                      │                 └─▶ notifications    │   REST control │  gauges, log)    │
│ CCTV / drones   │ ── MJPEG / HLS ────▶ │  MJPEG relay  /api/cameras/…/stream/ │                └──────────────────┘
└─────────────────┘                      └───────────┬──────────────────────────┘
                                                     └─▶ Twilio SMS · SMTP email · Web Push (VAPID)
```

## File structure

```
├── manage.py
├── requirements.txt · .env.example · Dockerfile · docker-compose.yml
├── sentinel/                 # project: settings, ASGI (HTTP + WebSocket), URLs
├── monitoring/
│   ├── models.py             # CameraFeed, SensorNode, SensorReading, SmartSprinkler, IncidentAlert, PushSubscription
│   ├── serializers.py        # model serializers + validated AI detection payload contract
│   ├── services.py           # alert engine: classification, incident merge/escalation, auto-sprinklers, snapshot
│   ├── consumers.py          # /ws/live-stream/ producer + dashboard consumer
│   ├── routing.py            # WebSocket URL routes
│   ├── middleware.py         # device-key auth for headless WS producers, origin check for browsers
│   ├── notifications.py      # Twilio SMS, email, Web Push, cooldown, async dispatch
│   ├── realtime.py           # channel-layer broadcast helpers
│   ├── views.py · urls.py    # REST API, MJPEG relay, dashboard, service worker
│   ├── permissions.py        # operator session / X-Device-Key
│   ├── management/commands/  # seed_demo, generate_vapid_keys
│   ├── templates/monitoring/email/
│   └── tests.py
├── templates/index.html      # dashboard shell + <template> blocks
├── static/css/style.css
├── static/js/
│   ├── app.js                # bootstrap / wiring
│   ├── sw.js                 # service worker (push)
│   ├── core/                 # api.js, socket.js (reconnecting WS), dom.js
│   └── features/             # overlay.js (rAF canvas engine), video.js, sensors.js, sprinklers.js, incidents.js, push.js, alarm.js
└── simulator/iot_simulator.py
```

## Quick start

```bash
python -m venv .venv && source .venv/bin/activate      # Windows: .venv\Scripts\activate
pip install -r requirements.txt
cp .env.example .env
python manage.py makemigrations monitoring
python manage.py migrate
python manage.py seed_demo
python manage.py runserver                             # Daphne ASGI server (HTTP + WebSockets)
```

Open http://127.0.0.1:8000/. In a second terminal, start the field simulator:

```bash
python simulator/iot_simulator.py --ignite "Zone B"
```

To see the full escalation (sensor alarm, then Critical, then SMS), switch the Zone B sprinklers to **Manual** before you ignite. In Auto mode, the first High fire detection opens the relays and puts the fire out.

Production: `docker compose up --build` runs Redis for the channel layer and cache, Postgres, and Daphne. Set `SENTINEL_OPEN_ACCESS=False`, a strong `DJANGO_SECRET_KEY` and `SENTINEL_DEVICE_API_KEY`, and put a TLS proxy in front. If you serve plain HTTP, set `DJANGO_SECURE_SSL_REDIRECT=False`.

Tests: `python manage.py test monitoring`.

## Access model

| Actor | Auth | Can |
|---|---|---|
| Operator (browser) | Django session (`/admin/login/`) | Everything, including emergency dispatch |
| Device / AI producer | `X-Device-Key` header (REST) or `?token=` / `X-Device-Key` (WS) | Publish detections and telemetry, operate relays |
| Anyone | only when `SENTINEL_OPEN_ACCESS=True` | Everything (local demo only) |

## Producer protocol — `ws://host/ws/live-stream/?token=<DEVICE_KEY>`

Detection frame (one per camera frame; unknown keys are ignored):

```json
{
  "type": "detection",
  "camera_id": "cam-warehouse",
  "timestamp": "2026-09-30T12:00:00Z",
  "frame_width": 1280, "frame_height": 720, "normalized": false,
  "detections": [
    {"category": "fire",  "label": "fire",   "confidence": 0.91, "bbox": [520, 300, 180, 210], "track_id": "F1"},
    {"category": "human", "label": "person", "confidence": 0.95, "bbox": [610, 280, 58, 144], "track_id": "p7", "at_risk": true}
  ],
  "danger_zones": [{"label": "danger zone", "level": "critical", "polygon": [[450,250],[780,250],[780,560],[450,560]]}],
  "heatmap": {"rows": 18, "cols": 32, "values": [0.0, 0.1, "... rows*cols values in 0..1, row-major"]},
  "human_coordinates": [[639, 424]],
  "fire_detected": true, "fire_severity": 0.62, "human_count": 1, "danger_count": 1
}
```

- `bbox` is `[x, y, w, h]` in source-frame pixels. With `normalized: true` it is 0..1 instead.
- `category` is one of `human | fire | smoke | danger | vehicle | other`.
- If you omit `fire_detected`, `human_count` or `danger_count`, the server works them out from `detections`. `danger_count` is the number of `danger` boxes plus humans marked `at_risk`.

Telemetry over the same WS: `{"type": "telemetry", "sensor_id": "temp-b1", "value": 61.4}`

Server → dashboard events: `snapshot`, `detection`, `telemetry`, `sprinkler`, `incident` (with `action`: created/escalated/updated/resolved), `notification`, `log`, `error`, `pong`.

## Alert rules

| Condition | Severity |
|---|---|
| fire severity ≥ 0.8, **or** ≥ 3 threats, **or** fire + humans + (threat or severity ≥ 0.5) | Critical |
| fire + (severity ≥ 0.4 or humans present), **or** ≥ 2 threats | High |
| any fire, **or** 1 threat | Medium |
| sensor ≥ threshold × critical multiplier (smoke 1.5, temperature 1.25) | Critical |
| sensor ≥ threshold | High |

- Frames from the same camera or sensor merge into one open incident. The merge window (`SENTINEL_INCIDENT_MERGE_WINDOW_SECONDS`) restarts each time the incident is touched.
- A merged incident only notifies again when its severity escalates.
- Notifications for the same source, zone and severity are rate-limited by `SENTINEL_ALERT_COOLDOWN_SECONDS`. Manual dispatch skips this cooldown.
- Sprinklers in **Auto** mode open when a sensor alarm fires or on a High/Critical fire detection in their zone. Sprinklers in **Manual** mode respond only to operator commands or the master switch.

## REST API (`/api/`)

| Method & path | Purpose |
|---|---|
| `GET /system/snapshot/` | Full dashboard state |
| `GET/POST/PATCH /cameras/`, `/cameras/{feed_id}/` | Camera registry |
| `GET /cameras/{feed_id}/stream/` | Same-origin MJPEG relay |
| `GET/PATCH /sensors/`, `/sensors/{sensor_id}/` | Sensor registry and thresholds (`?type=&zone=`) |
| `POST /sensors/{sensor_id}/telemetry/` | `{"value": 42.1}` |
| `GET /sensors/{sensor_id}/history/` | `?limit=&since=` |
| `POST /telemetry/` | Bulk: `{"readings": [{"sensor_id", "value"}]}` |
| `GET /sprinklers/` | Relay list |
| `POST /sprinklers/{device_id}/toggle/` | `{"status"?: "Active"/"Inactive", "mode"?: "Auto"/"Manual"}`. An empty body flips the relay |
| `POST /sprinklers/master/` | `{"status": "Active"/"Inactive", "zone"?: "Zone B"}` |
| `GET /incidents/` | `?resolved=&severity=High,Critical&zone=&since=&limit=&offset=` |
| `POST /incidents/{id}/resolve/` | Resolve an incident |
| `POST /detections/` | REST ingestion of one frame, or a list of up to 100 |
| `POST /dispatch/` | Manual emergency dispatch (operator only) |
| `GET/POST/DELETE /push/` | VAPID public key, subscribe, unsubscribe |

## Notifications

- **SMS**: set `TWILIO_ACCOUNT_SID`, `TWILIO_AUTH_TOKEN`, `TWILIO_FROM_NUMBER` and `SENTINEL_SMS_RECIPIENTS` (comma-separated E.164 numbers).
- **Email**: set the `EMAIL_*` variables and `SENTINEL_EMAIL_RECIPIENTS`. When `EMAIL_HOST` is empty, emails print to the console.
- **Web Push**: run `python manage.py generate_vapid_keys`, copy the keys into `.env`, then click **Enable Alerts** on the dashboard.
  - Browsers need HTTPS for push; `localhost` is the only exception.
  - Without VAPID keys, the dashboard falls back to local desktop notifications while it is open.

Each channel has a minimum severity (`SENTINEL_*_MIN_SEVERITY`). Results appear in the event log and in `IncidentAlert.notification_log`.

## Video feeds

Set `stream_format` per camera:

- `MJPEG`: relayed through `/api/cameras/{id}/stream/`.
- `HLS`: native playback in Safari; other browsers load hls.js on demand.
- `MP4`: plays directly.
- `SIM`: synthetic backdrop for overlay-only feeds.

The overlay canvas maps source-frame coordinates onto the letterboxed video area (`object-fit: contain`). It eases boxes between frames by `track_id` and fades them out after 1.5 s without data. Canvases that are off-screen are not drawn.
