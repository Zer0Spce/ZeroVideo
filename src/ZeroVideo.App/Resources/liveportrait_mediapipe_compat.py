"""ZeroVideo surgical repair for pinned Kijai LivePortrait MediaPipe Tasks API.

The pinned extension ships a private copied MediaPipe face_landmark.py from
2023 and imports mediapipe.framework.formats; current MediaPipe 0.10.35 on
Windows Python 3.13 no longer exports that legacy module layout. Replace
only the small public-Tasks API call sites in media_pipe/mp_utils.py; do NOT
edit the 96 KB vendor face_landmark.py or downgrade MediaPipe/NumPy/ComfyUI.

Idempotent, vendor-source checked, atomic, original backup retained.
"""
import argparse
import ast
import importlib.util
import os
from pathlib import Path
import shutil
import sys
import tempfile

OLD_IMPORT = "from . import face_landmark"
NEW_IMPORT = "# ZeroVideo: use public MediaPipe Tasks, not private 2023 face_landmark.py"
OLD_MODE = "self.mode = mp.tasks.vision.FaceDetectorOptions.running_mode.IMAGE"
NEW_MODE = "self.mode = vision.RunningMode.IMAGE"
OLD_LOADER = "self.detector = face_landmark.FaceLandmarker.create_from_options(options)"
NEW_LOADER = "self.detector = vision.FaceLandmarker.create_from_options(options)"
OLD_DETECT = "detection_result, _ = self.detector.detect(image)"
NEW_DETECT = "detection_result = self.detector.detect(image)"
OLD_IMAGE = "image = mp.Image(image_format=mp.ImageFormat.SRGB, data=img)"
NEW_IMAGE = ("image = mp.Image(image_format=mp.ImageFormat.SRGB, "
             "data=np.ascontiguousarray(img))")
UNUSED_DETECTOR = """        det_base_options = python.BaseOptions(model_asset_path=os.path.join(CUR_DIR, 'mp_models','blaze_face_short_range.tflite'))
        det_options = vision.FaceDetectorOptions(base_options=det_base_options)
        self.det_detector = vision.FaceDetector.create_from_options(det_options)
"""
UNUSED_DETECTOR_MARKER = ("        # ZeroVideo: removed redundant FaceDetector; "
                          "FaceLandmarker already detects faces.\n")
OLD_RETURN = "        return detection_result.face_landmarks"
NEW_RETURN = ("        return detection_result.face_landmarks "
              "if detection_result.face_landmarks else None")
OLD_NUMPY = "import mediapipe as mp\n"
NEW_NUMPY = "import mediapipe as mp\nimport numpy as np\n"


class RepairError(RuntimeError):
    pass


def replace_once(src, old, new, label):
    old_count = src.count(old)
    new_count = src.count(new)
    if old_count == 1 and new_count == 0:
        return src.replace(old, new, 1), True
    if old_count == 0 and new_count == 1:
        return src, False
    raise RepairError(
        f"{label}: expected one pinned upstream anchor or repaired anchor, "
        f"found old={old_count} repaired={new_count}; refusing to modify "
        "this unrecognized vendor file."
    )


def repair_source(src):
    if "class LMKExtractor" not in src or "FaceLandmarkerOptions" not in src:
        raise RepairError(
            "Not the supported pinned Kijai LMKExtractor source; no changes made."
        )
    changed = False
    for name, old, new in (
        ("legacy copied MediaPipe import", OLD_IMPORT, NEW_IMPORT),
        ("removed MediaPipe running-mode attribute", OLD_MODE, NEW_MODE),
        ("private FaceLandmarker implementation", OLD_LOADER, NEW_LOADER),
        ("outdated detect return tuple", OLD_DETECT, NEW_DETECT),
        ("contiguous MediaPipe image buffer", OLD_IMAGE, NEW_IMAGE),
        ("empty face results", OLD_RETURN, NEW_RETURN),
        ("NumPy buffer preparation", OLD_NUMPY, NEW_NUMPY),
        ("unused secondary face detector", UNUSED_DETECTOR,
         UNUSED_DETECTOR_MARKER),
    ):
        src, did_change = replace_once(src, old, new, name)
        changed |= did_change
    try:
        ast.parse(src)
    except SyntaxError as ex:
        raise RepairError(f"Updated MediaPipe source is not valid Python: {ex}")
    return src, changed


def import_check(path):
    # Import the exact patched module with the LIVE installed MediaPipe
    # package, rather than only running a syntax check.
    spec = importlib.util.spec_from_file_location(
        "zerovideo_liveportrait_mediapipe_tasks_probe", path
    )
    if spec is None or spec.loader is None:
        raise RepairError("Cannot construct MediaPipe Tasks module import")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    if not hasattr(module, "LMKExtractor"):
        raise RepairError("Patched module does not provide LMKExtractor")
    print("ZeroVideo: patched Kijai LMKExtractor imported against "
          "installed MediaPipe public Tasks API.", flush=True)


def repair(folder, verify_only=False, skip_import=False):
    path = Path(folder) / "media_pipe" / "mp_utils.py"
    if not path.is_file():
        raise RepairError(
            f"LivePortrait media_pipe/mp_utils.py does not exist: {path}"
        )
    original = path.read_text(encoding="utf-8")
    updated, changed = repair_source(original)

    if verify_only and changed:
        raise RepairError(
            "LivePortrait uses removed MediaPipe private APIs. Click Install "
            "LivePortrait to repair the Kijai node, then restart ComfyUI."
        )
    if changed and not verify_only:
        backup = path.with_name(
            path.name + ".zerovideo-before-mediapipe-tasks.bak"
        )
        if not backup.exists():
            shutil.copy2(path, backup)
        temp = None
        try:
            with tempfile.NamedTemporaryFile(
                mode="w", encoding="utf-8", newline="",
                prefix=path.name + ".zerovideo-", suffix=".tmp",
                dir=str(path.parent), delete=False
            ) as stream:
                temp = stream.name
                stream.write(updated)
            os.replace(temp, path)
        finally:
            if temp and os.path.isfile(temp):
                os.unlink(temp)

    if not skip_import:
        import_check(path)
    return (
        "LivePortrait MediaPipe Tasks compatibility "
        + ("verified (already repaired)." if not changed else
           "repaired; original mp_utils.py backup preserved.")
        + " Restart ComfyUI if it has already loaded LivePortrait nodes."
    )


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--extension", required=True)
    parser.add_argument("--verify-only", action="store_true")
    parser.add_argument("--skip-import", action="store_true")
    args = parser.parse_args()
    try:
        print(repair(args.extension, args.verify_only, args.skip_import),
              flush=True)
        return 0
    except Exception as ex:
        print("LIVEPORTRAIT_MEDIAPIPE_COMPAT_ERROR: " + str(ex),
              file=sys.stderr, flush=True)
        return 1


if __name__ == "__main__":
    sys.exit(main())
