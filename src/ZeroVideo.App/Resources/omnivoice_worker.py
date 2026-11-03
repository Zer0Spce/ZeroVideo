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
    """Split on sentence boundaries first, without duplicating any words.

    Only a sentence that exceeds the configured limit is broken on words.
    Keep terminal punctuation; losing it can make OmniVoice repeat speech.
    """
    text = re.sub(r"\\s+", " ", text).strip()
    if not text:
        raise ValueError("Narration text is empty.")
    # These Unicode sentence stops are important for multilingual narration.
    sentences = re.split(r"(?<=[.!?。！？])\\s+", text)
    pieces = []
    for sentence in sentences:
        if len(sentence) <= max_characters:
            pieces.append(sentence)
            continue
        # Prefer clauses when a single long sentence does not fit.
        clauses = re.split(r"(?<=[,;:，；：])\\s+", sentence)
        for clause in clauses:
            if len(clause) <= max_characters:
                pieces.append(clause)
                continue
            words = clause.split(" ")
            chunk = ""
            for word in words:
                if len(word) > max_characters:
                    raise ValueError("A narration token exceeds the allowed chunk length.")
                if chunk and len(chunk) + len(word) + 1 > max_characters:
                    pieces.append(chunk)
                    chunk = ""
                chunk = (chunk + " " + word).strip()
            if chunk:
                pieces.append(chunk)
    groups = []
    current = ""
    for piece in pieces:
        if current and len(current) + len(piece) + 1 > max_characters:
            groups.append(current)
            current = ""
        current = (current + " " + piece).strip()
    if current:
        groups.append(current)
    assert " ".join(groups) == text, "Narration chunking changed the spoken text."
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
    from omnivoice import OmniVoice, VoiceClonePrompt

    device = "cuda:0" if torch.cuda.is_available() else "cpu"
    dtype = torch.float16 if device.startswith("cuda") else torch.float32
    info("Loading model (" + device + "); synthesizing " + str(len(segments)) +
         " short segments with a single voice reference.")
    model = OmniVoice.from_pretrained("k2-fsa/OmniVoice", device_map=device, dtype=dtype)
    rate = int(model.sampling_rate)
    if rate < 16000 or rate > 96000:
        raise ValueError("Unsupported model sample rate.")

    cache_path = job.get("voice_prompt")
    if cache_path and Path(cache_path).is_file():
        info("Loading saved voice-clone prompt from local library.")
        prompt = VoiceClonePrompt.load(str(Path(cache_path)))
    else:
        info("Encoding the authorized sample into a reusable voice-clone prompt.")
        prompt = model.create_voice_clone_prompt(
            ref_audio=str(source), ref_text=transcript
        )
        if cache_path:
            cache = Path(cache_path)
            cache.parent.mkdir(parents=True, exist_ok=True)
            pending_prompt = cache.with_name(cache.name + ".partial")
            try:
                prompt.save(str(pending_prompt))
                os.replace(str(pending_prompt), str(cache))
            finally:
                if pending_prompt.exists():
                    pending_prompt.unlink()
            info("Saved reusable cloned-voice prompt.")

    output_audio = []
    pause = np.zeros(int(rate * 0.12), dtype=np.float32)
    for index, segment in enumerate(segments):
        audio = model.generate(
            text=segment, voice_clone_prompt=prompt,
            speed=speed, num_step=32,
            language=job.get("language", "English"), postprocess_output=True
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
