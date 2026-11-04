"""Offline regression checks for the pinned Kijai MediaPipe Tasks repair."""
import importlib.util
from pathlib import Path
import tempfile
import unittest

ROOT = Path(__file__).resolve().parent.parent
SCRIPT = ROOT / "src" / "ZeroVideo.App" / "Resources" / "liveportrait_mediapipe_compat.py"
spec = importlib.util.spec_from_file_location("zerovideo_lmk_patch", SCRIPT)
compat = importlib.util.module_from_spec(spec)
spec.loader.exec_module(compat)


def sample_source():
    return '''import os
import mediapipe as mp
from mediapipe.tasks import python
from mediapipe.tasks.python import vision
from . import face_landmark

class LMKExtractor:
    def __init__(self):
        self.mode = mp.tasks.vision.FaceDetectorOptions.running_mode.IMAGE
        base_options = python.BaseOptions(model_asset_path="task.model")
        options = vision.FaceLandmarkerOptions(base_options=base_options)
        self.detector = face_landmark.FaceLandmarker.create_from_options(options)

        det_base_options = python.BaseOptions(model_asset_path=os.path.join(CUR_DIR, 'mp_models','blaze_face_short_range.tflite'))
        det_options = vision.FaceDetectorOptions(base_options=det_base_options)
        self.det_detector = vision.FaceDetector.create_from_options(det_options)

    def __call__(self, img):
        image = mp.Image(image_format=mp.ImageFormat.SRGB, data=img)
        try:
            detection_result, _ = self.detector.detect(image)
        except:
            return None

        return detection_result.face_landmarks
'''


class ModernMediaPipeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.vendor = self.root / "ComfyUI-LivePortraitKJ"
        (self.vendor / "media_pipe").mkdir(parents=True)
        self.source = self.vendor / "media_pipe" / "mp_utils.py"
        self.source.write_text(sample_source(), encoding="utf-8")

    def test_repair_is_idempotent_with_backup(self):
        original = self.source.read_text()
        self.assertIn("repaired", compat.repair(self.vendor, skip_import=True))
        new = self.source.read_text()
        self.assertNotIn("from . import face_landmark", new)
        self.assertIn("vision.FaceLandmarker.create_from_options", new)
        self.assertIn("detection_result = self.detector.detect(image)", new)
        self.assertNotIn("self.det_detector =", new)
        self.assertIn("np.ascontiguousarray(img)", new)
        self.assertIn("faces if faces else None", new)
        backup = self.source.with_name(
            "mp_utils.py.zerovideo-before-mediapipe-tasks.bak")
        self.assertEqual(backup.read_text(), original)
        self.assertIn("already repaired", compat.repair(
            self.vendor, verify_only=True, skip_import=True))
        self.assertIn("already repaired", compat.repair(
            self.vendor, skip_import=True))
        self.assertEqual(backup.read_text(), original)

    def test_unpatched_source_fails_verify_only_without_edit(self):
        original = self.source.read_text()
        with self.assertRaisesRegex(compat.RepairError, "Install LivePortrait"):
            compat.repair(self.vendor, verify_only=True, skip_import=True)
        self.assertEqual(self.source.read_text(), original)

    def test_partial_repair_resumes(self):
        partial = self.source.read_text().replace(
            compat.OLD_IMPORT, compat.NEW_IMPORT)
        self.source.write_text(partial, encoding="utf-8")
        self.assertIn("repaired", compat.repair(self.vendor, skip_import=True))
        self.assertEqual(self.source.read_text().count(
            "vision.FaceLandmarker.create_from_options"),1)

    def test_unknown_vendor_format_preserves_source(self):
        unknown = self.source.read_text().replace(
            compat.OLD_DETECT, "some_unknown_interface()")
        self.source.write_text(unknown, encoding="utf-8")
        with self.assertRaisesRegex(compat.RepairError,
                                    "outdated detect return tuple"):
            compat.repair(self.vendor, skip_import=True)
        self.assertEqual(self.source.read_text(), unknown)
        self.assertFalse(self.source.with_name(
            "mp_utils.py.zerovideo-before-mediapipe-tasks.bak").exists())


if __name__ == "__main__":
    unittest.main()
