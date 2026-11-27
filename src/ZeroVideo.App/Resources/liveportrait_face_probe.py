"""ZeroVideo CPU-only face eligibility check before LivePortrait GPU execution.

Exit 0: face clearly large enough for LivePortrait
Exit 3: no reliable facial landmarks (use FFmpeg camera motion)
Exit 1: setup/import/image error (also fall back; print diagnosis)
"""
import argparse
import sys
import traceback


def run(image_path, landmark_path):
    import cv2
    import mediapipe as mp
    import numpy as np
    from mediapipe.tasks import python
    from mediapipe.tasks.python import vision

    image = cv2.imread(image_path, cv2.IMREAD_COLOR)
    if image is None:
        raise FileNotFoundError("Cannot read scene illustration: " + image_path)
    h, w = image.shape[:2]
    if min(h, w) < 64:
        print("FACE_SKIP: source illustration too small", flush=True)
        return 3

    factor = min(1.0, 1024.0 / max(w, h))
    if factor < 1:
        image = cv2.resize(image, None, fx=factor, fy=factor,
                           interpolation=cv2.INTER_AREA)
    rgb = np.ascontiguousarray(cv2.cvtColor(image, cv2.COLOR_BGR2RGB))
    options = vision.FaceLandmarkerOptions(
        base_options=python.BaseOptions(model_asset_path=landmark_path),
        running_mode=vision.RunningMode.IMAGE,
        num_faces=1, min_face_detection_confidence=0.60,
        min_face_presence_confidence=0.60)
    with vision.FaceLandmarker.create_from_options(options) as detector:
        found = detector.detect(mp.Image(image_format=mp.ImageFormat.SRGB,
                                         data=rgb))
    if not found.face_landmarks:
        print("FACE_SKIP: no recognizable face in illustration", flush=True)
        return 3
    points = found.face_landmarks[0]
    span = abs(points[33].x - points[263].x) * w
    if span < 45:
        print("FACE_SKIP: detected face is too small for a stable blink", flush=True)
        return 3
    print("FACE_OK: recognizable face, eye span " + str(round(span)) + " px", flush=True)
    return 0


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--image", required=True)
    parser.add_argument("--landmarks", required=True)
    args = parser.parse_args()
    sys.exit(run(args.image, args.landmarks))


if __name__ == "__main__":
    try:
        main()
    except Exception:
        traceback.print_exc()
        sys.exit(1)
