# Browser automation setup

ZeroVideo intentionally uses a separate Microsoft Edge profile stored under `%LOCALAPPDATA%\ZeroVideo\browser-profile`.

## First run

1. Open **Automation**.
2. Click **Open ChatGPT session** and sign in normally.
3. Click **Open Clipchamp session** and sign in normally.
4. Do not use InPrivate mode.
5. Return to **Create** and generate a project.

ZeroVideo does not solve CAPTCHA, bypass verification, or extract account cookies. If either site requests a human check, complete it directly in the visible Edge window.

## Selector maintenance

ChatGPT and Clipchamp are web apps and their HTML changes. The selector lists are in `profiles/automation.default.json`. Each action tries selectors in order. When Clipchamp cannot find a control, ZeroVideo takes a diagnostic screenshot in `%LOCALAPPDATA%\ZeroVideo\diagnostics` and stops instead of clicking random controls.

## Clipchamp TTS behavior

Each chapter is treated as an independent TTS/export job. This reduces the chance of long Clipchamp timelines becoming slow or failing. Exported chapter audio is saved to `<project>\audio\chapter-XX.m4a` and the project file is checkpointed after each chapter.
