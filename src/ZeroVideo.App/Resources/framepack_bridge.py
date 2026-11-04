"""ZeroVideo optional FramePack adapter for OFFLINE/LOCAL image-to-video.

Requires an already running OFFICIAL FramePack demo_gradio.py on loopback.
Uses FramePack's own Python environment with gradio_client installed.
Does not download models, start an external service or upload to the cloud.
"""
import argparse
import json
import os
import shutil
import sys
from pathlib import Path
from urllib.error import URLError
from urllib.parse import urlsplit
from urllib.request import urlopen


def configure_utf8_output():
    """Windows redirected stdout often defaults to cp1252, but Gradio prints Unicode."""
    for stream in (sys.stdout, sys.stderr):
        if hasattr(stream, "reconfigure"):
            stream.reconfigure(encoding="utf-8", errors="backslashreplace")



def require_loopback(value):
    uri = urlsplit(value)
    if uri.scheme != "http" or uri.hostname not in ("127.0.0.1", "localhost", "::1"):
        raise ValueError("FramePack endpoint must be a local HTTP loopback server.")
    if uri.username or uri.password or not (1 <= (uri.port or 7860) <= 65535):
        raise ValueError("FramePack endpoint must have a valid local port.")
    if uri.path not in ("", "/") or uri.query or uri.fragment:
        raise ValueError("Use a plain local FramePack server URL such as http://127.0.0.1:7860.")
    return value.rstrip("/")


def find_framepack_api(server):
    """Fail before GPU work when no LOCAL FramePack UI is listening.

    The official FramePack demo has a named Gradio /process endpoint.
    Checking the /config dependency list prevents submitting to an unrelated
    local application that happens to be listening on the same port.
    """
    try:
        with urlopen(server + "/config", timeout=8) as response:
            config = json.load(response)
    except (URLError, OSError, ValueError, TimeoutError) as exc:
        raise ConnectionError(
            "FramePack server is not running or did not respond at " + server
            + ". Start FramePack's official demo_gradio.py on this port and "
              "leave its console open. Check that the URL shown in FramePack "
              "matches ZeroVideo Settings."
        ) from exc

    dependencies = config.get("dependencies") or []
    names = {
        "/" + str(dep["api_name"]).lstrip("/")
        for dep in dependencies
        if isinstance(dep, dict)
        and isinstance(dep.get("api_name"), str)
        and dep["api_name"] not in ("", "unnamed")
    }
    if "/process" not in names:
        raise RuntimeError(
            "Connected to a Gradio server, but its /process endpoint is "
            "missing. This is not the expected official FramePack demo, "
            "or the installed FramePack interface has changed."
        )
    return "/process"


def motion_prompt(shot_direction):
    direction = (shot_direction or "").lower()
    surroundings = (
        "light rain or fog drifting only if naturally present in the picture, "
        "tiny realistic light flickers if lamps are already present"
    )
    camera = "locked camera and stable illustration"
    if "pan" in direction:
        camera = "almost imperceptible camera drift, stable composition"
    if "push" in direction or "zoom" in direction:
        camera = "very slow gentle camera push-in, stable composition"
    return (
        "Subtle continuous movement in the existing illustrated scene. "
        "If a person is present, gentle breathing, a natural occasional blink, "
        "very slight hair or clothing movement. If outdoors, " + surroundings + ". "
        + camera + ". Maintain the exact original illustrated character design, "
        "identity, expression, clothing, framing, setting and visual style. "
        "Do not introduce new objects, characters, text, camera cuts, walking, "
        "dramatic action, explosions, fast head turns, or sudden camera movement."
    )


def find_video(value):
    """Gradio Video outputs can be strings or nested FileData dictionaries."""
    if isinstance(value, (str, os.PathLike)):
        path = Path(value)
        if path.suffix.lower() == ".mp4" and path.is_file():
            return path
    if isinstance(value, dict):
        for key in ("video", "path", "name", "value", "data"):
            if key in value:
                result = find_video(value[key])
                if result is not None:
                    return result
    if isinstance(value, (tuple, list)):
        for item in value:
            result = find_video(item)
            if result is not None:
                return result
    return None


def run_job(job):
    server = require_loopback(job["server"])
    original = Path(job["image"]).resolve()
    target = Path(job["output"]).resolve()
    if not original.is_file() or original.suffix.lower() not in (
        ".png", ".jpg", ".jpeg", ".webp", ".bmp"
    ):
        raise FileNotFoundError("The reference must be a local supported image.")
    seconds = float(job.get("seconds", 4.0))
    if not 3.0 <= seconds <= 5.0:
        raise ValueError("AI motion clip duration must be 3–5 seconds.")
    if target.suffix.lower() != ".mp4":
        raise ValueError("AI motion output must be an MP4 file.")

    api = find_framepack_api(server)
    # Gradio is already a dependency of the official FramePack installation.
    # Client() prints a Unicode API-ready marker on some versions of Windows.
    from gradio_client import Client, handle_file
    print("[FramePack] Verified local FramePack /process endpoint at " + server, flush=True)
    client = Client(server)
    # Official demo_gradio.py: process(input_image, prompt, n_prompt, seed,
    # total_second_length, latent_window_size, steps, cfg, gs, rs,
    # gpu_memory_preservation, use_teacache, mp4_crf).
    # Default 25 steps, 9 latent-window, and 6 GB preservation from official UI.
    prompt = motion_prompt(job.get("shot_direction"))
    submission = client.submit(
        handle_file(str(original)), prompt, "", 31337, seconds, 9, 25,
        1.0, 10.0, 0.0, 6.0, True, 16, api_name=api
    )
    print("[FramePack] Local diffusion running; first run loads model weights.", flush=True)
    finished = submission.result()
    video = find_video(finished)
    if video is None:
        for output in reversed(submission.outputs()):
            video = find_video(output)
            if video is not None:
                break
    if video is None:
        raise RuntimeError(
            "FramePack finished without an accessible MP4. "
            "Use the official demo_gradio.py and check its server log."
        )
    if video.stat().st_size < 8192:
        raise RuntimeError("FramePack output MP4 is unexpectedly small.")
    target.parent.mkdir(parents=True, exist_ok=True)
    part = target.with_name(target.name + ".partial")
    try:
        shutil.copyfile(video, part)
        os.replace(part, target)
    finally:
        if part.exists():
            part.unlink()
    print("[FramePack] Saved animated video: " + str(target), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--job")
    parser.add_argument("--server", default="http://127.0.0.1:7860")
    parser.add_argument("--probe", action="store_true")
    parser.add_argument("--self-test", action="store_true")
    args = parser.parse_args()
    if args.self_test:
        assert require_loopback("http://127.0.0.1:7860") == "http://127.0.0.1:7860"
        assert require_loopback("http://localhost:7860/") == "http://localhost:7860"
        for bad in ("https://example.com", "http://192.168.1.5:7860",
                    "file:///tmp/a", "http://localhost:7860/path"):
            try:
                require_loopback(bad)
            except ValueError:
                pass
            else:
                raise AssertionError("Unsafe FramePack endpoint accepted.")
        assert "Subtle continuous movement" in motion_prompt("hold")
        assert "camera push-in" in motion_prompt("slow zoom in")
        assert find_video({"video": None}) is None
        # Fake the Gradio configuration so API discovery is tested offline.
        from io import BytesIO
        from unittest.mock import patch
        with patch.object(sys.modules[__name__], "urlopen",
                          return_value=BytesIO(json.dumps({
                              "dependencies": [{"api_name": "process"}]
                          }).encode("utf-8"))):
            assert find_framepack_api("http://127.0.0.1:7860") == "/process"
        with patch.object(sys.modules[__name__], "urlopen",
                          return_value=BytesIO(json.dumps({
                              "dependencies": [{"api_name": "different_app"}]
                          }).encode("utf-8"))):
            try:
                find_framepack_api("http://127.0.0.1:7860")
            except RuntimeError:
                pass
            else:
                raise AssertionError("Unrelated Gradio UI incorrectly accepted.")
        print("FramePack adapter offline smoke tests passed.")
        return
    if args.probe:
        server = require_loopback(args.server)
        api = find_framepack_api(server)
        print("[FramePack] CONNECTED: " + server + api, flush=True)
        return
    if not args.job:
        raise ValueError("Missing --job JSON file.")
    job = json.loads(Path(args.job).read_text(encoding="utf-8"))
    run_job(job)


if __name__ == "__main__":
    configure_utf8_output()
    try:
        main()
    except Exception as exc:
        print("[FramePack] ERROR: " + type(exc).__name__ + ": " + str(exc), file=sys.stderr, flush=True)
        raise SystemExit(1)
