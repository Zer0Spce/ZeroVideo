"""Offline smoke tests for ZeroVideo's embedded OmniVoice worker.

Does not require a GPU, install packages, download model weights, or clone voices.
"""
import importlib.util
import json
import subprocess
import sys
import tempfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
WORKER = ROOT / "src" / "ZeroVideo.App" / "Resources" / "omnivoice_worker.py"


def load_worker():
    spec = importlib.util.spec_from_file_location("zerovideo_omnivoice_worker", WORKER)
    assert spec is not None and spec.loader is not None
    worker = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(worker)
    return worker


def assert_rejects(fn, exception_type):
    try:
        fn()
    except exception_type:
        return
    raise AssertionError(f"Expected {exception_type.__name__}")


def main():
    worker = load_worker()
    assert worker.chunks_from("Hello, world!") == ["Hello, world!"]
    assert worker.chunks_from("  First\n second  ") == ["First second"]
    assert_rejects(lambda: worker.chunks_from(" \t  "), ValueError)
    assert_rejects(lambda: worker.chunks_from("x" * 281), ValueError)

    paragraph = " ".join(["Narration grows across episodes."] * 70)
    chunks = worker.chunks_from(paragraph)
    assert len(chunks) > 1
    assert all(0 < len(chunk) <= 280 for chunk in chunks)
    assert " ".join(chunks) == paragraph

    with tempfile.TemporaryDirectory() as temp:
        job = Path(temp) / "invalid-reference.json"
        job.write_text(json.dumps({
            "text": "Test speech for a reference that does not exist.",
            "ref_audio": str(Path(temp) / "missing.wav"),
            "ref_text": "Reference transcript.",
            "output": str(Path(temp) / "output.wav"),
            "speed": 1.0,
        }), encoding="utf-8")
        run = subprocess.run(
            [sys.executable, str(WORKER), "--job", str(job)],
            capture_output=True, text=True, timeout=20, check=False,
        )
        assert run.returncode != 0
        assert "FileNotFoundError" in run.stdout
        assert not (Path(temp) / "output.wav").exists()

    print("OmniVoice worker offline smoke tests passed.")


if __name__ == "__main__":
    main()
