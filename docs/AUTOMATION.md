# Browser automation setup

ZeroVideo intentionally uses a separate Microsoft Edge profile stored under `%LOCALAPPDATA%\ZeroVideo\browser-profile`.

## First run

1. Open **Automation**.
2. Click **Open ChatGPT** and sign in normally.
3. Click **Open Clipchamp** and sign in normally.
4. Run **System checks**.
5. Do not use InPrivate mode.
6. Return to **Create** and generate a project.

ZeroVideo does not solve CAPTCHA, bypass verification, or extract account cookies. If either site requests a human check, complete it directly in the visible Edge window.

## Selector maintenance

ChatGPT and Clipchamp are web apps and their HTML changes. The selector lists are in `profiles/automation.default.json`. Each action tries selectors in order. When Clipchamp cannot find a control, ZeroVideo takes a diagnostic screenshot in `%LOCALAPPDATA%\ZeroVideo\diagnostics` and stops instead of clicking random controls.

## ChatGPT visual pass

When **Generate missing visuals with ChatGPT** is enabled, ZeroVideo sends one scene-specific image request at a time through the signed-in ChatGPT browser session. It waits for a new assistant image, saves a screenshot of the generated image element into `<project>\media\cXX-sYYY.png`, updates the scene's `mediaPath`, and checkpoints `project.json` before continuing.

Already existing scene files are never regenerated. This makes long jobs resumable. The maximum generated visual count per build is configurable to avoid accidentally requesting hundreds of images in one pass.

## Clipchamp TTS behavior

Each chapter is treated as an independent TTS/export job. This reduces the chance of long Clipchamp timelines becoming slow or failing. Exported chapter audio is saved to `<project>\audio\chapter-XX.m4a` and the project file is checkpointed after each chapter.

If that chapter audio file already exists, ZeroVideo adopts it automatically and skips Clipchamp for that chapter. The Settings page also allows optional Clipchamp language and voice labels; if a requested label cannot be selected, ZeroVideo logs it and keeps Clipchamp's current selection rather than clicking an unrelated option.
