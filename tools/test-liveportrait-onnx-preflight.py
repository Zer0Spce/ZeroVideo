"""Offline unit checks for LivePortrait ONNX diagnosis, no GPU or weights."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent.parent
MODULE = ROOT / "src" / "ZeroVideo.App" / "Resources" / "liveportrait_onnx_probe.py"
spec = importlib.util.spec_from_file_location("zerovideo_onnx_preflight", MODULE)
probe = importlib.util.module_from_spec(spec)
spec.loader.exec_module(probe)


class LandmarkPreflightTests(unittest.TestCase):
    def test_missing_model_is_distinct_from_missing_mediapipe(self):
        with tempfile.TemporaryDirectory() as root:
            missing = str(Path(root) / "landmark.onnx")
            with self.assertRaisesRegex(FileNotFoundError, "LANDMARK_MISSING"):
                probe.check_landmark(missing)

    def test_partial_download_cannot_load_as_onnx(self):
        with tempfile.TemporaryDirectory() as root:
            tiny = Path(root) / "landmark.onnx"
            tiny.write_bytes(b"incomplete model")
            with self.assertRaisesRegex(ValueError, "LANDMARK_INCOMPLETE"):
                probe.check_landmark(str(tiny))

    def test_official_known_length(self):
        self.assertEqual(probe.EXPECTED_BYTES, 114_666_491)


if __name__ == "__main__":
    unittest.main()
