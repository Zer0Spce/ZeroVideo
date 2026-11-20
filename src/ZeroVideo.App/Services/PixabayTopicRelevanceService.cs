using System.Text.RegularExpressions;

namespace ZeroVideo.App.Services;

/// <summary>
/// A second relevance gate separate from Pixabay's query-to-tags match.
/// A stock clip must be supported by the *spoken narration*, not merely a
/// broad AI-generated query, genre mood or an unrelated stock keyword.
/// This checks text metadata, NOT actual video-frame semantics.
/// </summary>
public static class PixabayTopicRelevanceService
{
    private static readonly HashSet<string> Generic = new(
        new[] { "the", "with", "some", "this", "that", "they", "from",
            "into", "when", "while", "were", "there", "their", "then",
            "video", "footage", "clip", "recording", "picture", "image",
            "scene", "film", "stock", "background", "close", "view",
            "camera", "shot", "looking", "show", "shows", "about",
            "night", "daytime", "dark", "scary", "creepy", "ominous",
            "dramatic", "cinematic", "somber", "moody", "beautiful",
            "upbeat", "bright", "sunny", "eerie", "atmosphere", "quiet",
            "slow", "motion", "high", "quality", "black", "white",
            "generic", "animated", "cartoon", "documentary", "story" },
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> UnrelatedCartoonFiller = new(
        new[] { "cat", "squirrel", "nutella", "chocolate", "pizza",
            "burger", "dinner", "eating", "cooking", "wedding",
            "party", "vacation", "beach", "festival", "celebration" },
        StringComparer.OrdinalIgnoreCase);

    private static string Canonical(string word)
    {
        word = word.ToLowerInvariant();
        if (word is "tv" or "telly") return "television";
        if (word is "kids") return "child";
        if (word is "children") return "child";
        if (word is "broadcasting" or "broadcaster" or "broadcasters")
            return "broadcast";
        if (word is "spiders") return "spider";
        if (word is "parents") return "parent";
        if (word.EndsWith("ies", StringComparison.Ordinal) && word.Length > 5)
            return word[..^3] + "y";
        if (word.EndsWith("s", StringComparison.Ordinal) && !word.EndsWith("ss") &&
            word.Length > 4)
            return word[..^1];
        return word;
    }

    private static HashSet<string> Tokens(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]+")
            .Select(m => Canonical(m.Value))
            .Where(w => w.Length >= 3 && !Generic.Contains(w))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static bool IsRelevant(PixabayFootageScene scene,
        string videoTitle, string tags, out string rejection)
    {
        rejection = "";
        if (!PixabayVideoService.IsScriptVisualMatch(
                scene.SearchQuery, tags, scene.Mood))
        {
            rejection = "search-query subject or mood mismatch";
            return false;
        }

        var search = Tokens(scene.SearchQuery);
        var narrated = Tokens(scene.NarrationExcerpt);
        var metadata = Tokens(tags);

        // The exact storyboard subject MUST also exist in the spoken scene.
        // The title alone cannot turn a random cat or food clip into a
        // documentary illustration of an unrelated cartoon controversy.
        var overlap = search.Where(narrated.Contains).ToArray();
        if (overlap.Length == 0)
        {
            rejection = "generated Pixabay query is not grounded in narration";
            return false;
        }
        if (!overlap.Any(metadata.Contains))
        {
            rejection = "video metadata lacks the narrated visual subject";
            return false;
        }

        // Especially valuable for cartoon/show documentaries: Pixabay often
        // suggests unrelated cute animals and food that match a generic title.
        var cartoonSubject = Regex.IsMatch(videoTitle,
            @"\b(cartoon|anime|episode|peppa|spongebob|simpsons|pokemon|pokémon|nickelodeon|looney|animated)\b",
            RegexOptions.IgnoreCase);
        if (cartoonSubject &&
            metadata.Any(w => UnrelatedCartoonFiller.Contains(w) &&
                !narrated.Contains(w) && !search.Contains(w)))
        {
            rejection = "unrelated filler subject for cartoon documentary";
            return false;
        }

        return true;
    }

    public static int MatchScore(PixabayFootageScene scene, string tags)
    {
        var narrated = Tokens(scene.NarrationExcerpt);
        var metadata = Tokens(tags);
        var search = Tokens(scene.SearchQuery);
        return search.Count(w => narrated.Contains(w) && metadata.Contains(w));
    }
}
