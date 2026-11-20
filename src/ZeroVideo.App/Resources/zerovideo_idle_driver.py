"""ZeroVideo original, small-motion LivePortrait driving-image generator.

A LOCAL ComfyUI custom node. Only the *driving images* are synthesized:
the final appearance is reconstructed by LivePortrait from the original
illustration. No full-scene diffusion, no external calls, no stock videos.
"""
import math
import cv2
import numpy as np
import torch


class ZeroVideoIdleDriver:
    @classmethod
    def INPUT_TYPES(cls):
        return {"required": {
            "source_image": ("IMAGE",),
            "seconds": ("FLOAT", {"default": 4.0, "min": 3.0, "max": 5.0}),
            "fps": ("INT", {"default": 12, "min": 8, "max": 16}),
            "blink": ("BOOLEAN", {"default": True}),
            "strength": (["Low", "Medium"], {"default": "Low"})
        }}

    RETURN_TYPES = ("IMAGE",)
    RETURN_NAMES = ("driving_images",)
    FUNCTION = "animate"
    CATEGORY = "ZeroVideo/LivePortrait"

    def animate(self, source_image, seconds=4.0, fps=12,
                blink=True, strength="Low"):
        # Modern MediaPipe Tasks API works with both old and current
        # MediaPipe. The removed legacy mp.solutions.face_mesh API does not
        # exist in the current Python 3.13-compatible wheels.
        import os
        import mediapipe as mp
        from mediapipe.tasks import python
        from mediapipe.tasks.python import vision

        frame = (np.clip(source_image[0].cpu().numpy(), 0, 1) *
                 255).astype(np.uint8)
        h, w = frame.shape[:2]
        if h < 64 or w < 64:
            raise ValueError("Source illustration is too small for face animation.")

        # Reuse the model bundled by the pinned official Kijai extension,
        # without downloading another landmark checkpoint.
        model = os.path.normpath(os.path.join(
            os.path.dirname(__file__), "..", "ComfyUI-LivePortraitKJ",
            "media_pipe", "mp_models",
            "face_landmarker_v2_with_blendshapes.task"))
        if not os.path.isfile(model):
            raise FileNotFoundError(
                "LivePortrait face landmark model is missing. "
                "Repair/reinstall the official LivePortrait extension.")
        factor = min(1.0, 1024.0 / max(w, h))
        detecting = cv2.resize(frame, None, fx=factor, fy=factor,
                               interpolation=cv2.INTER_AREA) if factor < 1 else frame

        base = python.BaseOptions(model_asset_path=model)
        options = vision.FaceLandmarkerOptions(
            base_options=base,
            running_mode=vision.RunningMode.IMAGE,
            num_faces=1,
            min_face_detection_confidence=0.60,
            min_face_presence_confidence=0.60)
        with vision.FaceLandmarker.create_from_options(options) as detector:
            result = detector.detect(
                mp.Image(image_format=mp.ImageFormat.SRGB,
                         data=np.ascontiguousarray(detecting)))
        if not result.face_landmarks:
            raise ValueError(
                "No recognizable frontal face in illustration; "
                "use camera-motion fallback for this scene.")
        pts = result.face_landmarks[0]
        p = lambda i: (int(pts[i].x * w), int(pts[i].y * h))
        # Use eye-corner distance to reject tiny faces too small for a
        # credible blink; do not distort scenes dominated by background.
        eye_span = abs(p(33)[0] - p(263)[0])
        if eye_span < 45:
            raise ValueError("Face is too small for a reliable LivePortrait blink.")
        center = ((p(33)[0] + p(263)[0]) // 2,
                  (p(33)[1] + p(263)[1]) // 2)

        def closed_eye(base, corner_a, corner_b, upper, lower):
            a, b, u, d = map(p, (corner_a, corner_b, upper, lower))
            cx = int((a[0] + b[0]) / 2)
            cy = int((u[1] + d[1]) / 2)
            rx = max(3, abs(b[0] - a[0]) // 2)
            ry = max(2, int(abs(d[1] - u[1]) * 1.6))
            mask = np.zeros((h, w), np.uint8)
            cv2.ellipse(mask, (cx, cy), (rx + 2, ry + 2),
                        0, 0, 360, 255, -1)
            # This is only an ephemeral driving representation and is not
            # copied to the final frame; LivePortrait retargets the face.
            repaired = cv2.inpaint(base, mask, 3, cv2.INPAINT_TELEA)
            shade = tuple(int(v) for v in np.median(
                base[max(0,cy-ry-5):min(h,cy+ry+5),
                     max(0,cx-4):min(w,cx+4)], axis=(0,1)))
            cv2.line(repaired, (cx-rx,cy), (cx+rx,cy), shade, 1,
                     lineType=cv2.LINE_AA)
            return repaired, mask

        blink_targets = []
        if blink:
            blink_targets = [
                closed_eye(frame, 33, 133, 159, 145),
                closed_eye(frame, 362, 263, 386, 374)
            ]
        frames = []
        count = max(24, min(80, round(seconds * fps)))
        move = 0.35 if strength == "Low" else 0.65
        for index in range(count):
            t = index / float(fps)
            image = frame.copy()
            if blink:
                # One soft blink in each short loop, not repeated blinking.
                peak = min(1.0, max(0.0, 1.0 - abs(t - seconds*0.55)/0.16))
                peak = peak*peak*(3-2*peak)
                if peak > 0:
                    for closed, mask in blink_targets:
                        alpha = cv2.GaussianBlur(mask.astype(np.float32)/255.0,
                                                 (0,0), 1.5)*peak
                        image = np.clip(
                            image.astype(np.float32)*(1-alpha[...,None]) +
                            closed.astype(np.float32)*alpha[...,None],
                            0,255).astype(np.uint8)
            # Fractional-degree rotation and tiny periodic translation give
            # a genuinely subtle pose cue while preserving eye landmarks.
            angle = move * math.sin(2*math.pi*t/seconds)
            tx = move * 1.5 * math.sin(2*math.pi*t/seconds)
            ty = move * 1.0 * (1-math.cos(2*math.pi*t/seconds))
            matrix = cv2.getRotationMatrix2D(center, angle, 1.0)
            matrix[0,2] += tx
            matrix[1,2] += ty
            image = cv2.warpAffine(image, matrix, (w,h),
                     flags=cv2.INTER_LINEAR, borderMode=cv2.BORDER_REFLECT_101)
            frames.append(torch.from_numpy(np.ascontiguousarray(image))
                          .float().div(255.0))
        return (torch.stack(frames, dim=0),)


NODE_CLASS_MAPPINGS = {"ZeroVideoIdleDriver": ZeroVideoIdleDriver}
NODE_DISPLAY_NAME_MAPPINGS = {
    "ZeroVideoIdleDriver": "ZeroVideo Original Subtle Idle Driver"}
