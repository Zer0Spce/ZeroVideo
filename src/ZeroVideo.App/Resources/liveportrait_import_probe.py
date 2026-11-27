"""Offline diagnostic for the exact ComfyUI LivePortrait extensions shipped by ZeroVideo.

Run in the user's *existing* ComfyUI embedded Python, without starting a
second ComfyUI server or modifying the working models.
"""
import argparse
import importlib.util
import os
import sys
import traceback


def import_comfy_extension(label, path, expected):
    if not os.path.isfile(path):
        raise FileNotFoundError(label + ": missing " + path)
    dirname = os.path.dirname(path)
    name = "zerovideo_liveportrait_probe_" + label
    spec = importlib.util.spec_from_file_location(
        name, path, submodule_search_locations=[dirname])
    if spec is None or spec.loader is None:
        raise ImportError(label + ": cannot load Python module specification")
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    nodes = getattr(module, "NODE_CLASS_MAPPINGS", {})
    missing = [node for node in expected if node not in nodes]
    if missing:
        raise ImportError(label + ": expected nodes missing: " + ", ".join(missing))
    print("REGISTERED: " + label + ": " + ", ".join(expected), flush=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--comfy-root", required=True)
    ap.add_argument("--liveportrait-root", required=True)
    ap.add_argument("--driver-root", required=True)
    args = ap.parse_args()
    if not os.path.isfile(os.path.join(args.comfy_root, "main.py")):
        raise FileNotFoundError("The expected ComfyUI/main.py does not exist")
    sys.path.insert(0, args.comfy_root)
    import_comfy_extension(
        "Kijai", os.path.join(args.liveportrait_root, "__init__.py"),
        ("DownloadAndLoadLivePortraitModels", "LivePortraitLoadMediaPipeCropper",
         "LivePortraitCropper", "LivePortraitProcess", "LivePortraitComposite"))
    import_comfy_extension(
        "ZeroVideo", os.path.join(args.driver_root, "__init__.py"),
        ("ZeroVideoIdleDriver",))
    print("LivePortrait extension import probe passed.", flush=True)


if __name__ == "__main__":
    try:
        main()
    except BaseException:
        traceback.print_exc()
        raise SystemExit(1)
