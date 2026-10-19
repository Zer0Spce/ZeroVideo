# ZeroVideo

**ZeroVideo** is a Windows-only desktop application for automating long-form YouTube video production from a topic. It is designed around 5, 10, 15, 20, 30, 45, 60+ minute projects and also accepts custom durations.

The core workflow is local-first:

- **ChatGPT in Microsoft Edge** for script/chapter/scene planning — no OpenAI API key required.
- **Optional ChatGPT visual generation** for missing scene stills through the visible signed-in browser session.
- **Clipchamp Text to Speech** for chapter voiceovers.
- **FFmpeg** for the actual long-form video assembly, motion, audio mixing, concatenation and captions.
- **Persistent `project.json`** checkpoints so failed browser/TTS/visual jobs can resume instead of starting over.

## v0.1 foundation

- Native WPF Windows UI (`net10.0-windows`)
- 5–120 minute presets plus editable custom duration, 16:9 and 9:16
- ChatGPT browser automation using an isolated persistent Edge profile
- Strict structured long-form prompt / JSON parser
- Chapter and scene project model
- Optional resumable ChatGPT scene-image generation for missing media
- Clipchamp chapter TTS automation with configurable language/voice labels, selectors and safe diagnostic screenshots
- Existing `audio/chapter-XX.m4a` files are auto-adopted so TTS can be resumed or replaced manually
- FFmpeg dependency installer
- FFmpeg still/video scene rendering, Ken Burns-style motion, chapter assembly and narration muxing
- Optional background-music mix
- SRT generation
- Project folders + shot-list CSV
- Preflight checks for Edge, FFmpeg, project storage and automation profile
- Portable Windows build script and GitHub Actions build

## First run

### Build from source

```powershell
powershell -ExecutionPolicy Bypass -File .\tools\setup.ps1
powershell -ExecutionPolicy Bypass -File .\tools\build-portable.ps1
```

The portable ZIP is written to `dist\ZeroVideo-win-x64.zip`.

### In the app

1. Open **Automation** and open the ChatGPT session. Sign in normally.
2. Open the Clipchamp session. Sign in normally.
3. Open **Settings** and click **Install FFmpeg automatically**.
4. Run **System checks** from Automation.
5. Go to **Create**, enter a topic, choose or type a duration, and click **Generate project**.
6. Leave **Generate missing visuals with ChatGPT** enabled for a fully generated still-image pass, or turn it off and supply your own images/video. Manual files named `c01-s001.jpg`, `c01-s002.mp4`, etc. are auto-matched.
7. Click **Build video**. ZeroVideo checkpoints each generated visual and each chapter voiceover, then assembles the final MP4 with FFmpeg.

## Important browser automation note

ChatGPT and Clipchamp change their web UI. ZeroVideo keeps browser selectors in `profiles/automation.default.json` so fixes can be made without touching application logic. It never attempts to bypass login, CAPTCHA, verification or other security checks.

ChatGPT visual generation is intentionally resumable: every generated scene is immediately written into the project `media` folder and saved in `project.json`. For very long videos, use **Maximum generated visuals** to control how many missing scenes are generated in one run; any remainder can be continued later or filled manually.

## Media sourcing

ZeroVideo does not silently scrape copyrighted web images or video. The generated `shot-list.csv` contains concise B-roll/archive queries plus original image prompts. You can use your own licensed media, or let the optional ChatGPT image pass create original stills for missing scenes.

## Documentation

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) and [`docs/AUTOMATION.md`](docs/AUTOMATION.md).
