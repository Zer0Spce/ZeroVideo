using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public sealed class PromptBuilder
{
    public string BuildOutline(VideoProject p)
    {
        var chapterCount = Math.Clamp((int)Math.Ceiling(p.TargetMinutes / 3.0), 2, 40);
        var totalSeconds = p.TargetMinutes * 60;
        return $$"""
You are the senior story producer for ZeroVideo, a Windows application that creates long-form YouTube videos.

TOPIC: {{p.Topic}}
TARGET LENGTH: {{p.TargetMinutes}} minutes ({{totalSeconds}} seconds)
ASPECT RATIO: {{p.AspectRatio}}
STYLE: {{p.Style}}
LANGUAGE: {{p.Language}}
TARGET CHAPTER COUNT: approximately {{chapterCount}}

Create ONLY the master outline. Do not write the full narration yet.

Requirements:
- Strong hook and clear narrative progression.
- Each chapter introduces genuinely new information; no filler or repeated facts.
- Plan reveals and transitions so later chapters build on earlier ones.
- Keep most chapters around 2-4 minutes so they can be generated and voiced independently.
- Chapter targetSeconds should add up close to {{totalSeconds}} seconds.
- If the topic is current or factual, do not invent claims or sources. When genuinely known, include useful source URLs or source names; otherwise leave sources empty.
- Avoid generic chapter names like "More Information".

Return ONLY valid JSON, no markdown and no commentary, using exactly this shape:
{
  "title": "video title",
  "sources": ["source URL or source name"],
  "chapters": [
    {
      "number": 1,
      "title": "chapter title",
      "purpose": "what new information this chapter contributes",
      "targetSeconds": 150
    }
  ]
}
""";
    }

    public string BuildChapter(VideoProject p, ProjectChapter chapter, int chapterIndex)
    {
        var lowWords = Math.Max(100, (int)Math.Round(chapter.TargetSeconds / 60.0 * 125));
        var highWords = Math.Max(lowWords + 40, (int)Math.Round(chapter.TargetSeconds / 60.0 * 150));
        var previous = chapterIndex > 0 ? p.Chapters[chapterIndex - 1].Title : "none — this is the opening chapter";
        var next = chapterIndex + 1 < p.Chapters.Count ? p.Chapters[chapterIndex + 1].Title : "none — this is the final chapter";
        var outline = string.Join(" | ", p.Chapters.Select(c => $"{c.Number}. {c.Title}"));
        var styleRules = VisualStylePresets.GetPrompt(p.Style);
        return $$"""
You are writing ONE chapter of a long-form ZeroVideo YouTube project. Do not write any other chapter.

VIDEO TOPIC: {{p.Topic}}
VIDEO TITLE: {{p.Title}}
VIDEO STYLE: {{p.Style}}
VISUAL STYLE RULES: {{styleRules}}
LANGUAGE: {{p.Language}}
ASPECT RATIO: {{p.AspectRatio}}
OVERALL OUTLINE: {{outline}}

CHAPTER NUMBER: {{chapter.Number}}
CHAPTER TITLE: {{chapter.Title}}
CHAPTER PURPOSE: {{chapter.Purpose}}
TARGET DURATION: {{chapter.TargetSeconds}} seconds
TARGET SPOKEN WORDS: approximately {{lowWords}}-{{highWords}} words
PREVIOUS CHAPTER: {{previous}}
NEXT CHAPTER: {{next}}

Write an engaging, natural narration for only this chapter, but return it split into visual scenes. The narration must flow continuously when all scene narration fields are joined in order.

Requirements:
- Do not repeat material that belongs to earlier chapters.
- Set up the next chapter naturally without summarizing the whole video.
- Every scene must advance the narration.
- Vary visual changes naturally, usually around 6-12 seconds each; longer holds are allowed when justified.
- visualQuery must be a concise 2-8 word search phrase.
- imagePrompt must explicitly follow the VISUAL STYLE RULES above and describe one original still visual with no captions, readable text, logos, UI, or watermarks.
- Keep the selected visual style consistent across every scene in this project.
- If the selected style is Stickman explainer, every human/character in every imagePrompt must be a stickman and realistic people are forbidden.
- If the selected style is Cartoon, Hand-drawn, or Whiteboard explainer, do not drift into photorealistic human imagery.
- shotDirection must be practical, such as slow push in, pan left, map zoom, document highlight, parallax, or hold.
- Use visualType from: archive, broll, photo, map, document, graphic, generated.
- Use role from: hook, setup, detail, evidence, transition, reveal, ending.
- Do not fabricate specific quotes, documents, statistics, or events.

Return ONLY valid JSON, no markdown and no commentary. Do NOT duplicate the narration in a separate chapter-level narration field. Use exactly:
{
  "number": {{chapter.Number}},
  "title": "{{Escape(chapter.Title)}}",
  "purpose": "{{Escape(chapter.Purpose)}}",
  "targetSeconds": {{chapter.TargetSeconds}},
  "scenes": [
    {
      "number": 1,
      "role": "setup",
      "narration": "spoken narration for this scene",
      "visualType": "broll",
      "visualQuery": "short search phrase",
      "imagePrompt": "original scene visual prompt following the selected style",
      "shotDirection": "slow push in",
      "targetSeconds": 8
    }
  ]
}
""";
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
