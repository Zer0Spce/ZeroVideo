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

    // Requests involving a protected animated franchise use the real-world
    // subject of the narration instead of requesting a lookalike character.
    // This avoids a misleading official-looking recreation and does not try to
    // work around any refusal from the image provider.
    public static string SafeDocumentaryContext(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var substitutions = new (string Pattern, string Value)[]
        {
            (@"\bpeppa\s+pig\b", "a preschool animated television series"),
            (@"\bspongebob(?:\s+squarepants)?\b", "an underwater animated comedy"),
            (@"\bthe\s+simpsons\b", "an animated family sitcom"),
            (@"\bpok[eé]mon\b", "an animated creature-adventure series"),
            (@"\blooney\s+tunes\b", "classic animated shorts"),
            (@"\bmickey\s+mouse\b", "a classic animated mouse character"),
            (@"\bnickelodeon\b", "a children's television network"),
            (@"\bdisney\b", "an entertainment company"),
            (@"\bpixar\b", "an animation studio")
        };
        var value = text;
        foreach (var (pattern, replacement) in substitutions)
            value = Regex.Replace(value, pattern, replacement, RegexOptions.IgnoreCase);
        return value;
    }

    public static string TopicAnchor(string title, string narration, string query)
    {
        if (IsCartoonTopic(title))
            return "This is factual documentary commentary about children's animation or a broadcast controversy. " +
                   "Depict only a concrete non-proprietary visual directly justified by this scene: " +
                   "for example a real spider, family watching television, broadcasting room, parental concern, " +
                   "cartoon-production workspace, or an unbranded television interface. " +
                   "No franchise characters, character likenesses, lookalike cartoon mascots, brand identity, " +
                   "official episode frames or artwork. Do not attempt to simulate the copyrighted show's visual style. " +
                   "Use grounded original visual journalism, not a generic neighborhood or invented crime scene.";
        if (Regex.IsMatch(title, @"\b(celebration|disney.*town)\b", RegexOptions.IgnoreCase))
            return "For a documentary about Celebration, Florida, illustrate only the exact community subject in the narration. " +
                   "Do not depict theme-park attractions, futuristic cities, made-up historical evidence or unrelated boardrooms.";
        return "Identify and illustrate the single most concrete subject named in the narration: " +
               "person role, object, event context or setting. Avoid generic corporate meetings, suburban homes, " +
               "futuristic landscapes or filler unless they genuinely belong to this segment.";
    }

    public static string BuildScenePrompt(string title, PixabayFootageScene scene, string ratio)
    {
        var protectedTopic = IsCartoonTopic(title);
        var safeTitle = protectedTopic ? "Children's television documentary commentary" : SafeDocumentaryContext(title);
        var excerpt = protectedTopic ? SafeDocumentaryContext(scene.NarrationExcerpt) : scene.NarrationExcerpt;
        var query = protectedTopic ? SafeDocumentaryContext(scene.SearchQuery) : scene.SearchQuery;
        var direction = scene.Mood switch
        {
            "ominous" => "Ominous, dark, suspenseful shadows. Never sunny or cheerful.",
            "tense" => "Grounded unease and controlled high-contrast suspense.",
            "somber" => "Respectful muted documentary tones.",
            "upbeat" => "Natural optimistic lighting appropriate to the narration.",
            "calm" => "Peaceful realistic atmosphere without forced drama.",
            _ => "Grounded observational documentary lighting."
        };
        return $"""
Create exactly ONE original {ratio} full-bleed illustration for a factual YouTube documentary.

DOCUMENTARY CATEGORY: {safeTitle}
NARRATION SCENE NUMBER: {scene.SceneNumber}
SPECIFIC NARRATED SUBJECT: {excerpt}
VISUAL SEARCH SUBJECT: {query}
EMOTIONAL MOOD: {scene.Mood}
EDITORIAL ART DIRECTION: {TopicAnchor(title, excerpt, query)}
LIGHTING: {direction}

Illustrate the particular concrete event context or object described, not random stock photography.
This is a stand-alone original conceptual illustration. Do not use copyrighted character designs, recognizable lookalikes, franchise art styles, logos, official cartoon frames, fake screenshots, fabricated evidence, alleged archival photos, or faces of specific real people.
For television controversy, favor the real-world objects and contexts: viewers, television, studio, public discussion, or the specific animal/object actually named in the narration. Mood should follow the scene.
No green/blue screen, chroma key, unrelated characters, text overlays, labels, watermarks, borders or UI mockups.
Exactly ONE finished original illustration. No explanation.
""";
    }

    public static string BuildThumbnailPrompt(string title, string script,
        string style, string headline)
    {
        var hook = script.Length > 1300 ? script[..1300] : script;
        if (IsCartoonTopic(title)) hook = SafeDocumentaryContext(hook);
        var protectedTopic = IsCartoonTopic(title);
        var character = protectedTopic
            ? "Use one concrete, non-proprietary visual tied to the broadcast controversy (for example a spider, dark television room or anxious audience). Do NOT depict the franchise character, a lookalike animated mascot, an official TV still or a copied animation style."
            : "Use one striking subject from the actual narration; no generic corporate meetings or filler.";
        return $"""
Create exactly ONE finished 16:9 YouTube documentary thumbnail, widescreen 1280x720 composition.

VIDEO TOPIC: {(protectedTopic ? "Children's TV controversy documentary" : SafeDocumentaryContext(title))}
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
