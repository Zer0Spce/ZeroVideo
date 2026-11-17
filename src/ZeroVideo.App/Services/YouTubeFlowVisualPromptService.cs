using System.Text.RegularExpressions;

namespace ZeroVideo.App.Services;

/// <summary>
/// Distinguishes source-specific documentary visuals from generic aesthetic
/// filler. These prompts apply to original AI illustrations, not historical
/// photographs, licensed episode screenshots or purported authentic evidence.
/// </summary>
public static class YouTubeFlowVisualPromptService
{
    public static bool IsCartoonTopic(string title)
        => Regex.IsMatch(title,
            @"\b(peppa pig|spongebob|simpsons|pokemon|pokémon|looney tunes|cartoon|animated series|anime|nickelodeon|disney episode|banned episode)\b",
            RegexOptions.IgnoreCase);

    public static string TopicAnchor(string title, string narration, string query)
    {
        if (Regex.IsMatch(title, @"\bpeppa pig\b", RegexOptions.IgnoreCase))
            return "Topic: the children's animated series Peppa Pig. For a scene ABOUT the cartoon itself, feature a recognizable editorial IDEA of a piglet cartoon character: an ORIGINAL pink piglet illustration, simplified 2D linework, playful round features, reimagined with eerie shadows and a slightly uncanny expression. Do not recreate an official TV frame or trace official character designs, logos or lettering. For scenes about Australian broadcasters, spiders, public safety or a studio, feature THAT concrete subject instead of the piglet.";
        if (Regex.IsMatch(title, @"\b(celebration|disney.*town)\b",
                RegexOptions.IgnoreCase))
            return "Topic: Celebration, Florida, the real planned community associated with Disney. Represent only the specific historical/social subject described in this exact scene: housing standards, Florida town layout, planning documents, education, community meetings, or a tense Florida residential street as appropriate. Do NOT invent a futuristic EPCOT city, theme-park monorail, fictional residents or a random American boardroom unless the narration specifically describes it. It must be an illustrative reconstruction, not asserted as a photo of the actual town.";
        if (IsCartoonTopic(title))
            return "This is commentary on a named animated show. When the scene explicitly discusses its characters, make one distinctive ORIGINAL, editorially reimagined cartoon character connected to the show's subject (eerie when the mood is ominous), not generic suburbia. Avoid copied official character art, logos, frame replicas, official branding and fake screengrabs. If the scene describes real broadcasters, parent protests or TV decisions, illustrate those actual subjects instead.";
        return "Identify the single MOST CONCRETE subject of the exact narration: object, person role, event context or place. The image must visibly show that subject or a truthful contextual diagram. Avoid bland corporate meetings, anonymous houses, futuristic landscapes and unrelated decorative people unless specifically named in the scene.";
    }

    public static string BuildScenePrompt(string title, PixabayFootageScene scene, string ratio)
    {
        var direction = scene.Mood switch
        {
            "ominous" => "Ominous, dark, suspenseful shadows. No cheerful sunlight or cheerful smiling people.",
            "tense" => "Grounded unease, real-world suspense, controlled contrast.",
            "somber" => "Respectful muted documentary tones, restrained emotional weight.",
            "upbeat" => "Natural positive, optimistic color and mood appropriate to the narration.",
            "calm" => "Peaceful and realistic atmosphere without manufactured drama.",
            _ => "Observational, grounded documentary mood."
        };
        return $"""
Create exactly ONE high-quality, ORIGINAL illustration for a documentary video, {ratio}, full bleed.

VIDEO TITLE: {title}
SCENE NUMBER: {scene.SceneNumber}
NARRATION: {scene.NarrationExcerpt}
SEARCH SUBJECT: {scene.SearchQuery}
MOOD: {scene.Mood}
TOPIC RULES: {TopicAnchor(title, scene.NarrationExcerpt, scene.SearchQuery)}
LIGHTING: {direction}

The depicted subject MUST be directly justified by the narration. Build a clear, intentional composition, not a generic beautiful-looking stock scene. For disturbing cartoon/TV episode commentary, use an original stylized character illustration when the narration talks about its cartoon character; otherwise depict the actual episode-related objects (such as an Australian spider, a television broadcaster, a protest or production context).
Never fabricate evidence, official TV screenshots, historical crimes, documents, actual people or purported authentic photos. A dramatization must remain visibly illustrative.
No green or blue screen, chroma key, stock watermark, text labels, extra characters, typography, brand logo, border, embedded UI or unrelated symbols.
ONE image only. Do not explain.
""";
    }

    public static string BuildThumbnailPrompt(string title, string script,
        string style, string headline)
    {
        var hook = script.Length > 1300 ? script[..1300] : script;
        var character = IsCartoonTopic(title)
            ? "For cartoon/TV topics, prioritize a distinctive original editorial reinterpretation of the relevant animated subject, such as an ominous piglet-inspired cartoon for a scary Peppa Pig documentary. Not an official episode image, copied character frame or official logo."
            : "Use ONE striking, topic-specific subject from the script, not generic corporate meetings, stock houses or filler.";
        return $"""
Create exactly ONE finished 16:9 YouTube documentary thumbnail, widescreen 1280x720 composition.

VIDEO: {title}
OPENING CONTEXT: {hook}
STYLE: {style}
THUMBNAIL HEADLINE: {headline}
SUBJECT RULE: {character}

Cinematic, extremely readable at mobile size, strong controlled contrast, one focal subject, deliberate negative space for the short headline. For suspense/horror: dramatic shadows and ominous atmosphere, not gore.
Only include the provided headline if it is short and legible; do not invent extra words, official logos or imply fabricated archive footage. No chroma-key, watermark, fake TV screenshots or unrelated people.
Illustrative editorial artwork, not an authentic leaked image or a claim that an event happened as depicted.
Return one complete original thumbnail image, no explanation.
""";
    }
}
