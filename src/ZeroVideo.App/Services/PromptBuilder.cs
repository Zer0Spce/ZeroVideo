using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public sealed class PromptBuilder
{
    public string Build(VideoProject p)
    {
        var targetWordsLow = p.TargetMinutes * 125;
        var targetWordsHigh = p.TargetMinutes * 150;
        var chapterCount = Math.Clamp((int)Math.Round(p.TargetMinutes / 2.5), 3, 16);
        return $$"""
You are the planning engine for ZeroVideo, a long-form YouTube production application.

TOPIC: {{p.Topic}}
TARGET LENGTH: {{p.TargetMinutes}} minutes
ASPECT RATIO: {{p.AspectRatio}}
STYLE: {{p.Style}}
LANGUAGE: {{p.Language}}
TARGET NARRATION: approximately {{targetWordsLow}}-{{targetWordsHigh}} words total
TARGET CHAPTERS: approximately {{chapterCount}}

Create a factual, engaging long-form video plan. Use a strong hook, progressive reveals, varied pacing, and a memorable ending. Do not repeat facts simply to fill time. Keep continuity between chapters. If the topic is current, explicitly distinguish confirmed facts from claims or uncertainty. Never fabricate sources.

Break the narration into chapters suitable for separate text-to-speech jobs. Each chapter should normally be 60-180 seconds. Break each chapter into visual scenes averaging 4-10 seconds, varying timing naturally. Visual queries should be concise search phrases; imagePrompt should describe an original visual when stock/archive material is unavailable. ShotDirection should be practical editing direction such as slow push in, pan left, map zoom, document highlight, or hold.

Return ONLY valid JSON. No markdown fences and no commentary. Use exactly this shape:
{
  "title": "...",
  "sources": ["source names or URLs only when genuinely known; otherwise leave empty"],
  "chapters": [
    {
      "number": 1,
      "title": "...",
      "purpose": "...",
      "narration": "complete chapter narration",
      "targetSeconds": 120,
      "scenes": [
        {
          "number": 1,
          "role": "hook|setup|detail|evidence|transition|reveal|ending",
          "narration": "the exact narration covered by this scene",
          "visualType": "archive|broll|photo|map|document|graphic|generated",
          "visualQuery": "2-8 word search query",
          "imagePrompt": "original visual prompt without text or logos",
          "shotDirection": "editing motion",
          "targetSeconds": 6
        }
      ]
    }
  ]
}

Every chapter narration must equal the content represented by that chapter's scene narrations in the same order. Use double quotes and JSON-safe text.
""";
    }
}
