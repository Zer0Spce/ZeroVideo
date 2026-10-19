# ZeroVideo architecture

ZeroVideo is a Windows-only WPF desktop application.

## Pipeline

1. **Topic + duration** — user chooses a preset or types a custom long-form target.
2. **ChatGPT outline** — Playwright drives a dedicated persistent Edge profile and requests only the master outline. The outline contains chapter metadata and timing, not a giant narration blob.
3. **Chapter writer** — every chapter is generated in a fresh ChatGPT request with the overall outline, previous/next chapter context, target words and scene schema. Each completed chapter is checkpointed immediately. A partially written project can be loaded and resumed.
4. **Project state** — `project.json` is the source of truth. Chapters, scenes, narration, shot queries, generated visuals and assigned media survive restarts.
5. **Optional ChatGPT visuals** — missing scenes can be generated one at a time through the signed-in browser. Every generated still is saved before the next request. Existing media always wins.
6. **Clipchamp TTS** — narration is generated a chapter at a time to keep Clipchamp jobs small. Existing `audio/chapter-XX.m4a` files are adopted automatically. Login/verification is never bypassed.
7. **Media** — scene files can be assigned in project JSON or dropped into the project's `media` folder using names like `c01-s001.jpg`, `c01-s002.mp4`, etc. `shot-list.csv` contains visual search queries and original-image prompts.
8. **FFmpeg render** — every scene is normalized to 1080p landscape or portrait, stills receive gentle motion, scenes are concatenated chapter-by-chapter, narration is muxed, optional background music is mixed, and an SRT is generated.
9. **Export** — finished files are written to `exports/`.

## Long-form reliability

- A 60–120 minute script is never requested in one giant ChatGPT response. The outline and each chapter are independent generation jobs.
- Most chapters target roughly 2–4 minutes, keeping both ChatGPT responses and Clipchamp TTS jobs manageable.
- Invalid ChatGPT JSON is automatically retried once in a clean chat.
- Every completed chapter, generated image and chapter audio file is checkpointed, so a failure does not restart the whole video.
- Long videos are never edited as one giant Clipchamp project; Clipchamp is only the TTS stage.
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
