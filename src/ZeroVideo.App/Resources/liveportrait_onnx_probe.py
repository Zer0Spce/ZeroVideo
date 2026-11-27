"""ZeroVideo CPU-only diagnostic for LivePortrait landmark.onnx.

Runs in the EXISTING ComfyUI embedded Python. No NVIDIA GPU or image
generation required. Identifies missing/corrupt model vs ONNX runtime errors.
"""
import argparse
import os
import sys
import traceback


EXPECTED_BYTES = 114_666_491


def check_landmark(path):
    if not os.path.isfile(path):
        raise FileNotFoundError(
            "LIVEPORTRAIT_LANDMARK_MISSING: landmark.onnx not present at " + path
        )
    actual = os.path.getsize(path)
    if actual != EXPECTED_BYTES:
        raise ValueError(
            "LIVEPORTRAIT_LANDMARK_INCOMPLETE: landmark.onnx has "
            + str(actual) + " bytes; expected " + str(EXPECTED_BYTES)
        )

    try:
        import onnxruntime as ort
    except ImportError as exc:
        raise RuntimeError(
            "LIVEPORTRAIT_ONNX_RUNTIME_MISSING: existing ComfyUI Python "
            "cannot import onnxruntime: " + str(exc)
        ) from exc

    try:
        options = ort.SessionOptions()
        options.intra_op_num_threads = 4
        session = ort.InferenceSession(
            path, providers=["CPUExecutionProvider"], sess_options=options
        )
    except Exception as exc:
        raise RuntimeError(
            "LIVEPORTRAIT_ONNX_LOAD_FAILED: failed to initialize the "
            "landmark.onnx model using CPUExecutionProvider. "
            "Check Python onnxruntime DLL compatibility or model integrity. "
            "Underlying error: " + str(exc)
        ) from exc
    names = [output.name for output in session.get_outputs()]
    if len(names) < 3:
        raise RuntimeError(
            "LIVEPORTRAIT_ONNX_WRONG_MODEL: landmark.onnx has "
            + str(len(names)) + " outputs; expected at least 3."
        )
    print("LIVEPORTRAIT_LANDMARK_READY: onnxruntime=" + ort.__version__
          + ", provider=CPUExecutionProvider, outputs="
          + ",".join(names[:3]), flush=True)


def main():
    p = argparse.ArgumentParser()
    p.add_argument("--model", required=True)
    args = p.parse_args()
    try:
        check_landmark(args.model)
    except Exception as exc:
        print("LivePortrait landmark preflight: " + str(exc),
              file=sys.stderr, flush=True)
        traceback.print_exc()
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
