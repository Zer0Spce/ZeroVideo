"""ZeroVideo-owned local OmniVoice worker.

Invocation reads a UTF-8 JSON job rather than receiving full scripts on the
command line. Produces a WAV atomically; never deletes an existing voiceover.
Requires authorized reference audio and its matching transcript.
OmniVoice pretrained weights: CC BY-NC 4.0, NON-COMMERCIAL USE ONLY.
"""
import argparse
import json
import os
import re
import sys
from pathlib import Path


def info(message):
    print("[OmniVoice] " + message, flush=True)


def chunks_from(text, max_characters=280):
    text = re.sub(r"\s+", " ", text).strip()
    if not text:
        raise ValueError("Narration text is empty.")
    sentences = re.split(r"(?<=[.!?])\s+", text)
    groups = []
    current = ""
    for sentence in sentences:
        tokens = sentence.split(" ")
        for token in tokens:
            if not token:
                continue
            if len(token) > max_characters:
                raise ValueError("A narration token exceeds the allowed chunk length.")
            if current and len(current) + len(token) + 1 > max_characters:
                groups.append(current)
                current = ""
            current = (current + " " + token).strip()
    if current:
        groups.append(current)
    return groups


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--job")
    ap.add_argument("--download-only", action="store_true")
    args = ap.parse_args()

    if args.download_only:
        from huggingface_hub import snapshot_download
        info("Downloading official OmniVoice checkpoint into the private ZeroVideo model cache.")
        snapshot_download(repo_id="k2-fsa/OmniVoice")
        info("Official OmniVoice checkpoint cached.")
        return 0

    if not args.job:
        raise ValueError("A job file is required.")
    job = json.loads(Path(args.job).read_text(encoding="utf-8"))
    text = job["text"].strip()
    source = Path(job["ref_audio"]).resolve()
    if not source.is_file():
        raise FileNotFoundError("Reference voice WAV not found.")
    transcript = job["ref_text"].strip()
    if not transcript:
        raise ValueError("The reference audio transcript is required.")
    out = Path(job["output"]).resolve()
    speed = float(job.get("speed", 1.0))
    if not 0.7 <= speed <= 1.3:
        raise ValueError("Voice speed must be between 0.7 and 1.3.")
    segments = chunks_from(text)
    if not 1 <= len(segments) <= 2000:
        raise ValueError("The chapter contains too many narration segments.")

    import numpy as np
    import soundfile as sf
    import torch
    from omnivoice import OmniVoice

    device = "cuda:0" if torch.cuda.is_available() else "cpu"
    dtype = torch.float16 if device.startswith("cuda") else torch.float32
    info("Loading model (" + device + "); synthesizing " + str(len(segments)) +
         " short segments with a single voice reference.")
    model = OmniVoice.from_pretrained("k2-fsa/OmniVoice", device_map=device, dtype=dtype)
    rate = int(model.sampling_rate)
    if rate < 16000 or rate > 96000:
        raise ValueError("Unsupported model sample rate.")

    output_audio = []
    pause = np.zeros(int(rate * 0.12), dtype=np.float32)
    for index, segment in enumerate(segments):
        audio = model.generate(
            text=segment, ref_audio=str(source), ref_text=transcript,
            speed=speed, num_step=24
        )
        if not audio:
            raise RuntimeError("OmniVoice returned no speech for segment " + str(index + 1))
        part = np.asarray(audio[0], dtype=np.float32).reshape(-1)
        if part.size == 0 or not np.isfinite(part).all():
            raise RuntimeError("Invalid speech in segment " + str(index + 1))
        output_audio.append(part)
        if index + 1 < len(segments):
            output_audio.append(pause)
        info("Segment " + str(index + 1) + "/" + str(len(segments)) + " complete")

    out.parent.mkdir(parents=True, exist_ok=True)
    pending = out.with_name(out.name + ".partial.wav")
    try:
        sf.write(str(pending), np.concatenate(output_audio), rate, subtype="PCM_16")
        if pending.stat().st_size < 4096:
            raise RuntimeError("Generated WAV is unexpectedly small.")
        os.replace(str(pending), str(out))
    finally:
        if pending.exists():
            pending.unlink()
    info("Completed local voiceover WAV.")
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:
        info("ERROR: " + type(exc).__name__ + ": " + str(exc))
        sys.exit(1)
