# ZeroVideo architecture

ZeroVideo is a Windows-only WPF desktop application.

## Pipeline

1. **Topic + duration** — user chooses 5, 10, 15, 20, 30, 45, 60 minutes or edits the project JSON for a custom target.
2. **ChatGPT browser planner** — Playwright drives a dedicated, persistent Edge profile. It sends the structured ZeroVideo planning prompt and captures the returned JSON.
3. **Project state** — `project.json` is the source of truth. Chapters, scenes, narration, shot queries and assigned media survive restarts.
4. **Clipchamp TTS** — narration is generated a chapter at a time to keep Clipchamp jobs small. Login/verification is never bypassed. UI selectors live in `profiles/automation.default.json` so they can be repaired without code changes.
5. **Media** — scene files can be assigned in project JSON or dropped into the project's `media` folder using names like `c01-s001.jpg`, `c01-s002.mp4`, etc. `shot-list.csv` contains visual search queries and original-image prompts.
6. **FFmpeg render** — every scene is normalized to 1080p landscape or portrait, stills receive gentle motion, scenes are concatenated chapter-by-chapter, narration is muxed, optional background music is mixed, and an SRT is generated.
7. **Export** — finished files are written to `exports/`.

## Reliability decisions

- Long videos are never edited as one giant Clipchamp project; Clipchamp is only the TTS stage.
- Every chapter audio file and project update is checkpointed, so a failure does not restart the whole video.
- Browser selectors are data, not hard-coded assumptions.
- Authentication, CAPTCHA and account verification remain human-controlled.
- The FFmpeg renderer works even when some media is missing by inserting a neutral placeholder, making end-to-end testing possible.

## Project folder

```
<project>/
  project.json
  shot-list.csv
  audio/
  media/
  render/
  exports/
  diagnostics/
```
