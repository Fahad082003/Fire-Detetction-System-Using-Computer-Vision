"""
Sentinel field simulator.

Stands in for the external systems that feed the platform:
  * Vision producer  -> WebSocket /ws/live-stream/  (structured detection JSON per frame)
  * IoT gateway      -> REST POST /api/telemetry/   (smoke ppm / temperature °C)
  * Sprinkler relays -> REST GET  /api/sprinklers/  (active relays suppress simulated fire)

Fire events ignite randomly (or on demand with --ignite), grow over time, push sensor
readings over their safety limits, place humans at risk, and are suppressed once the
zone's sprinklers are active — exercising the full alert → notify → sprinkler loop.

Usage:
    python simulator/iot_simulator.py --base-url http://127.0.0.1:8000 --device-key dev-device-key
    python simulator/iot_simulator.py --ignite "Zone B"
"""
import argparse
import asyncio
import json
import logging
import math
import random
import signal
import sys
from dataclasses import dataclass, field
from urllib.parse import quote, urlparse

import httpx
import websockets

log = logging.getLogger("sentinel.simulator")

HEATMAP_COLS, HEATMAP_ROWS = 32, 18


# --------------------------------------------------------------------------- world model
@dataclass
class ZoneFire:
    intensity: float = 0.0          # 0..1
    center: tuple = (0.5, 0.6)      # normalized frame coords
    suppressed: bool = False

    @property
    def burning(self):
        return self.intensity > 0.05


@dataclass
class Walker:
    track_id: str
    x: float
    y: float
    vx: float
    vy: float

    def step(self, dt, flee_from=None):
        if flee_from is not None:
            dx, dy = self.x - flee_from[0], self.y - flee_from[1]
            dist = math.hypot(dx, dy) or 1e-3
            self.vx += (dx / dist) * 0.25 * dt
            self.vy += (dy / dist) * 0.12 * dt
        speed = math.hypot(self.vx, self.vy)
        if speed > 0.18:
            self.vx, self.vy = self.vx / speed * 0.18, self.vy / speed * 0.18
        self.vx += random.uniform(-0.02, 0.02)
        self.vy += random.uniform(-0.01, 0.01)
        self.x += self.vx * dt
        self.y += self.vy * dt
        if not 0.04 < self.x < 0.92:
            self.vx *= -1
            self.x = min(max(self.x, 0.04), 0.92)
        if not 0.35 < self.y < 0.78:
            self.vy *= -1
            self.y = min(max(self.y, 0.35), 0.78)


@dataclass
class CameraSim:
    feed_id: str
    zone: str
    width: int
    height: int
    walkers: list = field(default_factory=list)

    @classmethod
    def create(cls, camera):
        drone = camera["feed_type"] == "Drone"
        cam = cls(camera["feed_id"], camera["location_zone"], 1920 if drone else 1280, 1080 if drone else 720)
        for i in range(random.randint(1, 4)):
            cam.walkers.append(Walker(
                track_id=f"{camera['feed_id'][-3:]}-{i + 1}",
                x=random.uniform(0.1, 0.85), y=random.uniform(0.4, 0.75),
                vx=random.uniform(-0.08, 0.08), vy=random.uniform(-0.03, 0.03),
            ))
        return cam


class World:
    def __init__(self, fire_probability):
        self.fire_probability = fire_probability
        self.fires = {}
        self.active_sprinkler_zones = set()

    def fire(self, zone):
        return self.fires.setdefault(zone, ZoneFire())

    def ignite(self, zone):
        f = self.fire(zone)
        f.intensity = max(f.intensity, 0.12)
        f.center = (random.uniform(0.25, 0.75), random.uniform(0.5, 0.7))
        f.suppressed = False
        log.warning("FIRE ignited in %s", zone)

    def step(self, dt, zones):
        for zone in zones:
            f = self.fire(zone)
            if not f.burning and random.random() < self.fire_probability * dt:
                self.ignite(zone)
            if not f.burning:
                f.intensity = 0.0
                continue
            if zone in self.active_sprinkler_zones:
                f.intensity = max(0.0, f.intensity - 0.025 * dt)
                if not f.suppressed:
                    log.info("Sprinklers suppressing fire in %s", zone)
                    f.suppressed = True
            else:
                f.intensity = min(1.0, f.intensity + 0.035 * dt * (1.2 - f.intensity))


# --------------------------------------------------------------------------- frame builders
def build_detection(cam, fire, dt):
    detections, zones, points = [], [], []
    w, h = cam.width, cam.height
    danger = 0

    if fire.burning:
        fx, fy = fire.center
        size = 0.06 + fire.intensity * 0.28
        fw, fh = size * w, size * h * 1.2
        detections.append({
            "label": "fire", "category": "fire", "confidence": round(0.7 + fire.intensity * 0.29, 2),
            "bbox": [fx * w - fw / 2, fy * h - fh / 2, fw, fh], "track_id": f"{cam.feed_id[-3:]}-F",
        })
        if fire.intensity > 0.25:
            sw, sh = fw * 1.6, fh * 0.9
            detections.append({
                "label": "smoke", "category": "smoke", "confidence": round(0.6 + fire.intensity * 0.3, 2),
                "bbox": [max(0.0, fx * w - sw / 2), max(0.0, fy * h - fh / 2 - sh), sw, sh],
                "track_id": f"{cam.feed_id[-3:]}-S",
            })
        radius = size * 1.4
        zones.append({
            "label": "danger zone", "level": "critical" if fire.intensity > 0.6 else "danger",
            "polygon": [
                [(fx + radius * math.cos(a)) * w, (fy + radius * 0.8 * math.sin(a)) * h]
                for a in (i * math.pi / 5 for i in range(10))
            ],
        })

    for walker in cam.walkers:
        near_fire = fire.burning and math.hypot(walker.x - fire.center[0], walker.y - fire.center[1]) < 0.12 + fire.intensity * 0.3
        walker.step(dt, flee_from=fire.center if near_fire else None)
        bw, bh = 0.045 * w, 0.2 * h
        at_risk = bool(near_fire)
        danger += at_risk
        detections.append({
            "label": "person", "category": "human", "confidence": round(random.uniform(0.82, 0.98), 2),
            "bbox": [walker.x * w - bw / 2, walker.y * h - bh, bw, bh],
            "track_id": walker.track_id, "at_risk": at_risk,
        })
        points.append([walker.x * w, walker.y * h])

    heatmap = None
    if fire.burning:
        fx, fy = fire.center
        spread = 0.05 + fire.intensity * 0.2
        values = []
        for r in range(HEATMAP_ROWS):
            for c in range(HEATMAP_COLS):
                x, y = (c + 0.5) / HEATMAP_COLS, (r + 0.5) / HEATMAP_ROWS
                d2 = (x - fx) ** 2 + ((y - fy) * 1.3) ** 2
                values.append(round(min(1.0, fire.intensity * 1.1 * math.exp(-d2 / (2 * spread ** 2))), 3))
        heatmap = {"rows": HEATMAP_ROWS, "cols": HEATMAP_COLS, "values": values}

    return {
        "type": "detection",
        "camera_id": cam.feed_id,
        "frame_width": w,
        "frame_height": h,
        "normalized": False,
        "detections": detections,
        "danger_zones": zones,
        "heatmap": heatmap,
        "human_coordinates": points,
        "fire_detected": fire.burning,
        "fire_severity": round(fire.intensity, 3) if fire.burning else None,
        "human_count": len(cam.walkers),
        "danger_count": danger,
    }


def sensor_value(sensor, fire):
    noise = random.gauss(0, 1)
    if sensor["type"] == "Temperature":
        return round(22.0 + fire.intensity * 75.0 + noise * 0.6, 2)
    return round(max(0.0, 15.0 + fire.intensity * 620.0 + noise * 4.0), 1)


# --------------------------------------------------------------------------- runtime
class Simulator:
    def __init__(self, args):
        self.args = args
        self.base = args.base_url.rstrip("/")
        self.headers = {"X-Device-Key": args.device_key} if args.device_key else {}
        self.http = httpx.AsyncClient(base_url=self.base, headers=self.headers, timeout=10.0)
        self.world = World(args.fire_probability)
        self.cameras, self.sensors = [], []
        self.stop = asyncio.Event()

    @property
    def ws_url(self):
        parsed = urlparse(self.base)
        scheme = "wss" if parsed.scheme == "https" else "ws"
        token = f"?token={quote(self.args.device_key)}" if self.args.device_key else ""
        return f"{scheme}://{parsed.netloc}/ws/live-stream/{token}"

    async def bootstrap(self):
        cameras = (await self.http.get("/api/cameras/")).raise_for_status().json()
        self.sensors = (await self.http.get("/api/sensors/")).raise_for_status().json()
        self.cameras = [CameraSim.create(c) for c in cameras]
        if not self.cameras and not self.sensors:
            raise SystemExit("No cameras or sensors registered. Run: python manage.py seed_demo")
        log.info("Simulating %d camera(s) and %d sensor(s)", len(self.cameras), len(self.sensors))
        for zone in self.args.ignite or []:
            self.world.ignite(zone)

    def zones(self):
        return {c.zone for c in self.cameras} | {s["location_zone"] for s in self.sensors}

    async def world_loop(self):
        dt = 0.5
        while not self.stop.is_set():
            self.world.step(dt, self.zones())
            await asyncio.sleep(dt)

    async def sprinkler_loop(self):
        while not self.stop.is_set():
            try:
                sprinklers = (await self.http.get("/api/sprinklers/")).raise_for_status().json()
                self.world.active_sprinkler_zones = {s["zone"] for s in sprinklers if s["status"] == "Active"}
            except httpx.HTTPError as exc:
                log.warning("Sprinkler poll failed: %s", exc)
            await asyncio.sleep(3)

    async def telemetry_loop(self):
        while not self.stop.is_set():
            readings = [
                {"sensor_id": s["sensor_id"], "value": sensor_value(s, self.world.fire(s["location_zone"]))}
                for s in self.sensors
            ]
            if readings:
                try:
                    resp = await self.http.post("/api/telemetry/", json={"readings": readings})
                    if resp.status_code >= 400:
                        log.warning("Telemetry rejected (%s): %s", resp.status_code, resp.text[:200])
                except httpx.HTTPError as exc:
                    log.warning("Telemetry post failed: %s", exc)
            await asyncio.sleep(self.args.telemetry_interval)

    async def vision_loop(self):
        interval = 1.0 / self.args.fps
        backoff = 1
        while not self.stop.is_set():
            try:
                async with websockets.connect(self.ws_url, max_size=2 ** 22, open_timeout=10) as ws:
                    log.info("Vision producer connected to %s", self.ws_url.split("?")[0])
                    backoff = 1
                    reader = asyncio.create_task(self._drain(ws))
                    try:
                        while not self.stop.is_set():
                            for cam in self.cameras:
                                frame = build_detection(cam, self.world.fire(cam.zone), interval)
                                await ws.send(json.dumps(frame))
                            await asyncio.sleep(interval)
                    finally:
                        reader.cancel()
            except (OSError, websockets.WebSocketException) as exc:
                log.warning("WebSocket error: %s — reconnecting in %ss", exc, backoff)
                await asyncio.sleep(backoff)
                backoff = min(backoff * 2, 30)

    @staticmethod
    async def _drain(ws):
        async for raw in ws:
            try:
                message = json.loads(raw)
            except ValueError:
                continue
            if message.get("type") == "error":
                log.warning("Server rejected frame: %s", message.get("detail") or message.get("errors"))

    async def run(self):
        await self.bootstrap()
        tasks = [asyncio.create_task(coro) for coro in (
            self.world_loop(), self.sprinkler_loop(), self.telemetry_loop(), self.vision_loop(),
        )]
        await self.stop.wait()
        for t in tasks:
            t.cancel()
        await asyncio.gather(*tasks, return_exceptions=True)
        await self.http.aclose()


def parse_args(argv=None):
    parser = argparse.ArgumentParser(description="Sentinel IoT + vision field simulator")
    parser.add_argument("--base-url", default="http://127.0.0.1:8000")
    parser.add_argument("--device-key", default="dev-device-key", help="SENTINEL_DEVICE_API_KEY on the server")
    parser.add_argument("--fps", type=float, default=5.0, help="Detection frames per second per camera")
    parser.add_argument("--telemetry-interval", type=float, default=2.0, help="Seconds between sensor batches")
    parser.add_argument("--fire-probability", type=float, default=0.004, help="Ignition chance per zone per second")
    parser.add_argument("--ignite", action="append", metavar="ZONE", help="Start a fire in ZONE immediately (repeatable)")
    parser.add_argument("-v", "--verbose", action="store_true")
    return parser.parse_args(argv)


def main():
    args = parse_args()
    logging.basicConfig(level=logging.DEBUG if args.verbose else logging.INFO,
                        format="%(asctime)s %(levelname)-7s %(message)s", datefmt="%H:%M:%S")
    sim = Simulator(args)
    loop = asyncio.new_event_loop()
    asyncio.set_event_loop(loop)
    if sys.platform != "win32":
        for sig in (signal.SIGINT, signal.SIGTERM):
            loop.add_signal_handler(sig, sim.stop.set)
    try:
        loop.run_until_complete(sim.run())
    except KeyboardInterrupt:
        sim.stop.set()
        loop.run_until_complete(asyncio.sleep(0))
    finally:
        loop.close()


if __name__ == "__main__":
    main()
