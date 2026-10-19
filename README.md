# ZeroVideo

**ZeroVideo** is a Windows-only desktop application for automating long-form YouTube video production from a topic. It is designed around 5, 10, 15, 20, 30, 45, 60+ minute projects.

The core workflow is local-first:

- **ChatGPT in Microsoft Edge** for script/chapter/scene planning — no OpenAI API key required.
- **Clipchamp Text to Speech** for chapter voiceovers.
- **FFmpeg** for the actual long-form video assembly, motion, audio mixing, concatenation and captions.
- **Persistent `project.json`** checkpoints so failed browser/TTS jobs can resume instead of starting over.

## v0.1 foundation

- Native WPF Windows UI (`net10.0-windows`)
- 5–60 minute presets, 16:9 and 9:16
- ChatGPT browser automation using an isolated persistent Edge profile
- Strict structured long-form prompt / JSON parser
- Chapter and scene project model
- Clipchamp chapter TTS automation with configurable selectors and safe diagnostic screenshots
- FFmpeg dependency installer
- FFmpeg still/video scene rendering, Ken Burns-style motion, chapter assembly and narration muxing
- Optional background-music mix
- SRT generation
- Project folders + shot-list CSV
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
4. Go to **Create**, enter a topic, choose a duration, and click **Generate project**.
5. Review `shot-list.csv` and place media into the project `media` folder. Files named `c01-s001.jpg`, `c01-s002.mp4`, etc. are auto-matched, or set `mediaPath` in `project.json`.
6. Click **Build video**. ZeroVideo generates missing chapter voiceovers in Clipchamp, checkpoints each one, then assembles the final MP4 with FFmpeg.

## Important browser automation note

ChatGPT and Clipchamp change their web UI. ZeroVideo keeps browser selectors in `profiles/automation.default.json` so fixes can be made without touching application logic. It never attempts to bypass login, CAPTCHA, verification or other security checks.

## Media sourcing

v0.1 deliberately does not silently scrape copyrighted web images or videos. The generated `shot-list.csv` contains concise B-roll/archive queries plus original image prompts. Media acquisition can be extended as separate source adapters while the rendering pipeline stays unchanged.

## Documentation

See [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) and [`docs/AUTOMATION.md`](docs/AUTOMATION.md).
