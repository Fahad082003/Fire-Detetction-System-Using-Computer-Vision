"""MOCK-BASED unit tests: a fake ONNX session returns hand-made YOLOX output. They prove decoding, filtering,
tracking, confirmation and the HTTP contract, NOT model accuracy (see tools/evaluate.py for that).
Run: python -m unittest discover -s tests"""
import json
import tempfile
import threading
import unittest
import urllib.error
import urllib.request
from pathlib import Path

import cv2
import numpy as np

from sentinel_ai.detector import ModelError, YoloxDetector
from sentinel_ai.pipeline import CameraPipeline, fire_severity
from sentinel_ai.service import CameraWorker, Engine, make_handler, mask
from sentinel_ai.tracker import IouTracker

N = 52 * 52 + 26 * 26 + 13 * 13  # YOLOX anchors for a 416x416 input


class FakeSession:
    def __init__(self, raw_frames):
        self.raw_frames, self.calls = list(raw_frames), 0

    def get_inputs(self):
        return [type("I", (), {"name": "images"})()]

    def run(self, _, feeds):
        assert feeds["images"].shape == (1, 3, 416, 416)
        raw = self.raw_frames[min(self.calls, len(self.raw_frames) - 1)]
        self.calls += 1
        return [raw]


def raw(boxes, classes=2):
    """boxes: (cx, cy, w, h, score, cls) in model pixels; placed on the stride-8 grid cell under (cx, cy)."""
    out = np.zeros((1, N, 5 + classes), dtype=np.float32)
    for cx, cy, w, h, score, cls in boxes:
        gx, gy = int(cx // 8), int(cy // 8)
        i = gy * 52 + gx
        out[0, i, :4] = [cx / 8 - gx, cy / 8 - gy, np.log(w / 8), np.log(h / 8)]
        out[0, i, 4], out[0, i, 5 + cls] = 1.0, score
    return out


SPEC_PERSON = {"name": "person", "file": "p.onnx", "input": [416, 416], "categories": {"0": "Human"},
               "labels": {"0": "person"}, "threshold": 0.45, "nms": 0.45}
SPEC_FIRE = {"name": "fire_smoke", "file": "f.onnx", "input": [416, 416], "categories": {"0": "Smoke", "1": "Fire"},
             "labels": {"0": "smoke", "1": "fire"}, "threshold": 0.40, "nms": 0.45}


def detector(spec, *frames, classes=2):
    return YoloxDetector(spec, Path("."), session=FakeSession([raw(f, classes) for f in frames]))


class DetectorTests(unittest.TestCase):
    def test_decodes_box_back_to_original_image_coordinates(self):
        d = detector(SPEC_PERSON, [(100, 200, 40, 80, 0.9, 0)])
        img = np.zeros((832, 832, 3), np.uint8)  # scale 0.5 -> boxes double
        [det] = d.detect(img)
        self.assertEqual(det.category, "Human")
        self.assertEqual(det.label, "person")
        self.assertAlmostEqual(det.confidence, 0.9, places=3)
        np.testing.assert_allclose(det.box, (160, 320, 80, 160), atol=0.2)

    def test_filters_low_confidence_and_unmapped_classes(self):
        d = detector(SPEC_PERSON, [(100, 100, 40, 40, 0.3, 0), (300, 300, 40, 40, 0.95, 1)])
        self.assertEqual(d.detect(np.zeros((416, 416, 3), np.uint8)), [])

    def test_nms_merges_duplicates_per_class(self):
        d = detector(SPEC_FIRE, [(100, 100, 60, 60, 0.9, 1), (104, 100, 60, 60, 0.8, 1), (112, 100, 60, 60, 0.7, 0)])
        dets = d.detect(np.zeros((416, 416, 3), np.uint8))
        self.assertEqual(sorted(x.category for x in dets), ["Fire", "Smoke"])

    def test_malformed_output_raises_model_error(self):
        bad = YoloxDetector(SPEC_PERSON, Path("."), session=FakeSession([np.zeros((1, 10, 6), np.float32)]))
        with self.assertRaises(ModelError):
            bad.detect(np.zeros((416, 416, 3), np.uint8))

    def test_missing_or_tampered_weights_are_refused(self):
        with tempfile.TemporaryDirectory() as tmp:
            with self.assertRaisesRegex(ModelError, "not found"):
                YoloxDetector(SPEC_PERSON, Path(tmp))
            (Path(tmp) / "p.onnx").write_bytes(b"tampered")
            with self.assertRaisesRegex(ModelError, "sha256 mismatch"):
                YoloxDetector({**SPEC_PERSON, "sha256": "0" * 64}, Path(tmp))


class TrackerTests(unittest.TestCase):
    def test_keeps_id_for_overlapping_box_and_creates_new_id_far_away(self):
        from sentinel_ai.detector import Det
        t = IouTracker()
        mk = lambda box: Det("Human", "person", 0.9, box, "person", 0.45)
        a = t.update([mk((10, 10, 50, 100))])[0]
        b, c = t.update([mk((14, 12, 50, 100)), mk((300, 10, 50, 100))])
        self.assertEqual(a.track_id, "P1")
        self.assertEqual(b.track_id, "P1")
        self.assertEqual((b.hits, b.age), (2, 2))
        self.assertEqual(c.track_id, "P2")


class PipelineTests(unittest.TestCase):
    FIRE = [(200, 200, 100, 100, 0.8, 1)]

    def test_fire_is_reported_only_after_confirmation_frames(self):
        pipe = CameraPipeline([detector(SPEC_FIRE, self.FIRE)], "cam-1", simulated=False)
        img = np.zeros((416, 416, 3), np.uint8)
        first, second, third = (pipe.process(img) for _ in range(3))
        self.assertFalse(first["fireDetected"])
        self.assertEqual(first["detections"], [])
        self.assertFalse(second["fireDetected"])
        self.assertTrue(third["fireDetected"])
        self.assertEqual(third["fireSeverity"], "Medium")  # 100x100 of 416x416 = 5.8% of the frame
        self.assertIn("confirmed: track F1 seen 3/3 frames", third["detections"][0]["explanation"])

    def test_single_image_reports_immediately_and_says_so(self):
        pipe = CameraPipeline([detector(SPEC_FIRE, self.FIRE)], "upload-test", simulated=True)
        p = pipe.process(np.zeros((416, 416, 3), np.uint8), temporal=False)
        self.assertTrue(p["fireDetected"])
        self.assertIn("single image", p["detections"][0]["explanation"])
        self.assertTrue(p["simulated"])

    def test_payload_matches_backend_contract(self):
        pipe = CameraPipeline([detector(SPEC_FIRE, self.FIRE)], "cam-1", simulated=False, missing=["person"])
        p = pipe.process(np.zeros((416, 416, 3), np.uint8), temporal=False)
        self.assertEqual(set(p), {"cameraId", "timestamp", "frameWidth", "frameHeight", "normalized", "detections",
                                  "dangerZones", "heatmap", "fireDetected", "fireSeverity", "simulated", "model"})
        self.assertEqual(p["model"], "fire_smoke:f person:missing")
        self.assertEqual(set(p["detections"][0]), {"label", "category", "confidence", "bbox", "trackId", "explanation"})
        self.assertLessEqual(len(p["detections"][0]["explanation"]), 240)
        zone = p["dangerZones"][0]
        self.assertEqual(zone["level"], "Danger")
        self.assertEqual(len(zone["polygon"]), 4)
        hm = p["heatmap"]
        self.assertEqual(len(hm["values"]), hm["rows"] * hm["cols"])
        self.assertTrue(all(0 <= v <= 1 for v in hm["values"]))
        json.dumps(p)  # serialisable

    def test_severity_thresholds(self):
        t = {"Medium": 2.0, "High": 10.0, "Critical": 25.0}
        self.assertEqual([fire_severity(a, t) for a in (0.5, 2, 9.9, 10, 30)], ["Low", "Medium", "Medium", "High", "Critical"])

    def test_partial_nested_settings_keep_the_other_defaults(self):
        pipe = CameraPipeline([], "cam-1", True, {"severity_area_percent": {"High": 8.0}})
        self.assertEqual(pipe.s["severity_area_percent"], {"Medium": 2.0, "High": 8.0, "Critical": 25.0})
        self.assertEqual(pipe.s["confirm_frames"], 3)


class ServiceTests(unittest.TestCase):
    def start(self, engine, cameras=()):
        from http.server import ThreadingHTTPServer
        server = ThreadingHTTPServer(("127.0.0.1", 0), make_handler(engine, list(cameras)))
        threading.Thread(target=server.serve_forever, daemon=True).start()
        self.addCleanup(server.shutdown)
        return f"http://127.0.0.1:{server.server_address[1]}"

    def engine(self, tmp, factory):
        (Path(tmp) / "models.json").write_text(json.dumps({"models": [SPEC_PERSON]}))
        return Engine(Path(tmp), detector_factory=factory)

    def post(self, url, body):
        try:
            with urllib.request.urlopen(urllib.request.Request(url + "/infer", data=body, method="POST")) as r:
                return r.status, json.loads(r.read())
        except urllib.error.HTTPError as e:
            return e.code, json.loads(e.read())

    def test_no_model_gives_503_and_health_says_so(self):
        with tempfile.TemporaryDirectory() as tmp:
            url = self.start(self.engine(tmp, YoloxDetector))  # real factory, no weights in tmp
            health = json.loads(urllib.request.urlopen(url + "/health").read())
            self.assertEqual(health["status"], "no-model")
            self.assertFalse(health["models"][0]["loaded"])
            ok, png = cv2.imencode(".png", np.zeros((20, 20, 3), np.uint8))
            code, body = self.post(url, png.tobytes())
            self.assertEqual(code, 503)
            self.assertIn("no AI model loaded", body["error"])

    def test_infer_rejects_undecodable_image_and_returns_payload_for_valid_one(self):
        with tempfile.TemporaryDirectory() as tmp:
            factory = lambda spec, d: detector(spec, [(100, 100, 40, 80, 0.9, 0)])
            url = self.start(self.engine(tmp, factory))
            self.assertEqual(self.post(url, b"definitely not an image")[0], 400)
            ok, jpg = cv2.imencode(".jpg", np.zeros((416, 416, 3), np.uint8))
            code, body = self.post(url, jpg.tobytes())
            self.assertEqual(code, 200)
            self.assertEqual(body["payload"]["detections"][0]["category"], "Human")
            self.assertGreaterEqual(body["inferenceMs"], 0)

    def test_file_source_is_always_simulated_and_credentials_are_masked(self):
        with tempfile.TemporaryDirectory() as tmp:
            video = Path(tmp) / "clip.mp4"
            video.write_bytes(b"x")
            e = self.engine(tmp, lambda spec, d: detector(spec, []))
            w = CameraWorker(e, {"cameraId": "cam-1", "source": str(video), "simulated": False}, "http://api", "k")
            self.assertEqual(w.kind, "file")
            self.assertTrue(w.simulated)
        self.assertEqual(mask("rtsp://admin:secret@10.0.0.5/live"), "rtsp://***@10.0.0.5/live")

    def test_camera_worker_survives_a_failing_frame(self):
        class Flaky:  # first inference raises, the rest find nothing
            name, file, calls = "fire_smoke", "f.onnx", 0

            def detect(self, bgr):
                Flaky.calls += 1
                if Flaky.calls == 1:
                    raise ModelError("bad output")
                return []

        with tempfile.TemporaryDirectory() as tmp:
            video = str(Path(tmp) / "clip.avi")
            out = cv2.VideoWriter(video, cv2.VideoWriter_fourcc(*"MJPG"), 50, (64, 48))
            for _ in range(3):
                out.write(np.zeros((48, 64, 3), np.uint8))
            out.release()
            e = self.engine(tmp, lambda spec, d: Flaky())
            w = CameraWorker(e, {"cameraId": "cam-1", "source": video, "fps": 50}, "http://127.0.0.1:9", "k")
            w.run()  # file source without loop: returns when the clip ends
            self.assertEqual(Flaky.calls, 3)  # kept going after the failing frame
            self.assertEqual(w.state, "finished")


if __name__ == "__main__":
    unittest.main()
