using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public sealed class PromptBuilder
{
    public string BuildOutline(VideoProject p)
    {
        var chapterCount = Math.Clamp(p.Options.HowManyChapters, 1, 40);
        var totalSeconds = p.TargetMinutes * 60;
        return $$"""
You are the senior story producer for ZeroVideo, a Windows application that creates long-form YouTube videos.

TOPIC: {{p.Topic}}
TARGET LENGTH: {{p.TargetMinutes}} minutes ({{totalSeconds}} seconds)
ASPECT RATIO: {{p.AspectRatio}}
STYLE: {{p.Style}}
LANGUAGE: {{p.Language}}
HOW MANY CHAPTERS: EXACTLY {{chapterCount}}
IMAGES / NARRATION SCENES PER CHAPTER: EXACTLY {{Math.Clamp(p.Options.ImagesPerChapter, 1, 50)}}

Create ONLY the master outline. Do not write the full narration yet.

Requirements:
- Return exactly {{chapterCount}} chapters. Not approximately {{chapterCount}} — exactly {{chapterCount}}.
- Strong hook and clear narrative progression.
- Each chapter introduces genuinely new information; no filler or repeated facts.
- Plan reveals and transitions so later chapters build on earlier ones.
- Distribute the total runtime sensibly across exactly {{chapterCount}} chapters.
- Chapter targetSeconds should add up close to {{totalSeconds}} seconds.
- The later chapter-writing step will split EACH chapter into exactly {{Math.Clamp(p.Options.ImagesPerChapter, 1, 50)}} narration/visual scenes, so make every chapter purpose substantial enough to support that structure.
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
        var sceneCount = Math.Clamp(p.Options.ImagesPerChapter, 1, 50);
        var averageSceneSeconds = Math.Max(1, chapter.TargetSeconds / (double)sceneCount);
        var averageSceneWords = Math.Max(8, (int)Math.Round(((lowWords + highWords) / 2.0) / sceneCount));

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
IMAGES PER CHAPTER: EXACTLY {{sceneCount}}
VOICEOVER / VISUAL SCENES: EXACTLY {{sceneCount}}
AVERAGE SCENE LENGTH: about {{averageSceneSeconds:0.0}} seconds
AVERAGE SPOKEN WORDS PER SCENE: about {{averageSceneWords}} words, adjusted naturally as needed
PREVIOUS CHAPTER: {{previous}}
NEXT CHAPTER: {{next}}

Write an engaging, natural narration for only this chapter and split it into EXACTLY {{sceneCount}} scenes.
Each scene is one voiceover beat and one target image. The narration must flow continuously when all {{sceneCount}} scene narration fields are joined in order.

Requirements:
- Return exactly {{sceneCount}} scene objects numbered 1 through {{sceneCount}}. No more and no fewer.
- Every scene must contain spoken narration. Do not create silent visual-only scenes.
- The scene narration fields together are the chapter voiceover. They should total approximately {{lowWords}}-{{highWords}} spoken words and approximately {{chapter.TargetSeconds}} seconds.
- Give each scene enough narration to sustain its image naturally. Avoid one-sentence filler scenes just to reach the requested count.
- Scene targetSeconds should add up close to {{chapter.TargetSeconds}} seconds.
- Do not repeat material that belongs to earlier chapters.
- Set up the next chapter naturally without summarizing the whole video.
- Every scene must advance the narration.
- visualQuery must be a concise 2-8 word search phrase that names the actual subject, event, object, place, person, clue, document, or action in that scene. Avoid generic searches such as "mystery evidence", "dramatic background", "question marks", or "thinking person" when the narration contains something more specific.
- imagePrompt must be directly derived from the SAME scene narration. It must depict the exact subject/action/place/object being discussed, not merely the general theme of the whole video.
- When narration mentions a named person, location, vehicle, document, object, historical event, physical clue, technology, company, or institution, make that concrete item the primary visual subject when visually appropriate.
- Prefer concrete explanatory visuals over generic symbolism. Do not default to puzzle pieces, question marks, silhouettes, anonymous investigators, random landscapes, or generic mystery boards unless the narration specifically calls for them.
- For abstract narration, build the metaphor from topic-specific objects already established in the story so the visual still clearly belongs to this video.
- imagePrompt must explicitly follow the VISUAL STYLE RULES above and describe one original still visual with no captions, readable text, logos, UI, or watermarks.
- Keep the selected visual style consistent across every scene in this project.
- Maintain visual continuity between adjacent scenes: recurring subjects should keep recognizable clothing, shapes, props, locations, and era unless the narration changes them.
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
      "visualQuery": "specific subject search phrase",
      "imagePrompt": "one scene-specific visual that directly illustrates this narration while following the selected style",
      "shotDirection": "slow push in",
      "targetSeconds": {{Math.Max(1, (int)Math.Round(averageSceneSeconds))}}
    }
  ]
}
""";
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
