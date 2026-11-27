"""Verify complete PuLID-FLUX node registration in the bundled ComfyUI Python.

ZeroVideo executes this offline in the local portable Python, using the
already installed custom node folder. No weights are loaded and no GPU is used.
"""
import argparse
import importlib.util
import os
import sys
import traceback


REQUIRED_NODES = (
    "PulidFluxModelLoader",
    "PulidFluxEvaClipLoader",
    "PulidFluxFaceNetLoader",
    "ApplyPulidFlux",
)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--comfy-root", required=True)
    parser.add_argument("--pulid-root", required=True)
    args = parser.parse_args()

    main_script = os.path.join(args.comfy_root, "main.py")
    node_script = os.path.join(args.pulid_root, "__init__.py")
    if not os.path.isfile(main_script):
        raise FileNotFoundError("ComfyUI main.py missing: " + main_script)
    if not os.path.isfile(node_script):
        raise FileNotFoundError("PuLID extension missing: " + node_script)

    sys.path.insert(0, args.comfy_root)
    import torch
    import onnx
    import onnxruntime
    import insightface
    import facenet_pytorch
    import facexlib

    name = "zerovideo_pulid_face_node_probe"
    spec = importlib.util.spec_from_file_location(
        name, node_script, submodule_search_locations=[args.pulid_root]
    )
    if spec is None or spec.loader is None:
        raise ImportError("Could not construct PuLID Python import spec")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)

    mappings = getattr(module, "NODE_CLASS_MAPPINGS", {})
    missing = [node for node in REQUIRED_NODES if node not in mappings]
    if missing:
        raise ImportError("PuLID extension is missing the required face nodes: "
                          + ", ".join(missing))
    print("VERIFIED: PuLID-FLUX extension imported, nodes: "
          + ", ".join(REQUIRED_NODES), flush=True)


if __name__ == "__main__":
    try:
        main()
    except Exception:
        traceback.print_exc()
        raise SystemExit(1)
