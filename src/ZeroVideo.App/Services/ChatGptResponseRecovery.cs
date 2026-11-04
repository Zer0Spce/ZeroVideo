using System.Text.Json;

namespace ZeroVideo.App.Services;

/// <summary>
/// Pure, browser-independent recovery checks. Never reuse an earlier assistant JSON
/// when a new turn is still streaming or has returned invalid content.
/// </summary>
public static class ChatGptResponseRecovery
{
    public static string? FindFreshJson(
        IReadOnlyList<string> assistantCandidates,
        int baselineCount,
        string baselineAssistant,
        string baselineJson)
    {
        if (assistantCandidates.Count == 0) return null;

        // New DOM blocks belong to the current response. If ChatGPT reuses/replaces
        // a block, only inspect the newest changed block, never an older reply.
        var first = assistantCandidates.Count > baselineCount
            ? Math.Max(0, baselineCount)
            : assistantCandidates.Count - 1;

        if (assistantCandidates.Count <= baselineCount &&
            string.Equals(assistantCandidates[^1], baselineAssistant, StringComparison.Ordinal))
            return null;

        for (var i = assistantCandidates.Count - 1; i >= first; i--)
        {
            var json = ExtractLastValidJsonObject(assistantCandidates[i]);
            if (json is null) continue;
            if (assistantCandidates.Count <= baselineCount &&
                string.Equals(json, baselineJson, StringComparison.Ordinal))
                continue;
            return json;
        }

        // Some UIs split long JSON between adjacent markdown blocks.
        if (first < assistantCandidates.Count - 1)
        {
            var joined = string.Join("\n", assistantCandidates.Skip(first));
            var combined = ExtractLastValidJsonObject(joined);
            if (combined is not null &&
                (!string.Equals(combined, baselineJson, StringComparison.Ordinal) ||
                 assistantCandidates.Count > baselineCount))
                return combined;
        }
        return null;
    }

    /// <summary>
    /// Fallback for the new ChatGPT UI, whose messages sometimes have no
    /// assistant role, conversation-turn test ID or markdown wrapper. In that
    /// case the page's main.innerText still contains the completed JSON.
    /// Exclude all JSON already visible before send and JSON in the user's
    /// prompt, otherwise old chapters and schema examples become false hits.
    /// </summary>
    public static string? FindFreshJsonInMainText(
        string? currentMainText,
        string? baselineMainText,
        string? sentPrompt)
    {
        if (string.IsNullOrWhiteSpace(currentMainText) ||
            string.Equals(currentMainText, baselineMainText, StringComparison.Ordinal))
            return null;

        var json = ExtractLastValidJsonObject(currentMainText);
        if (json is null) return null;

        if ((!string.IsNullOrEmpty(baselineMainText) &&
             baselineMainText.Contains(json, StringComparison.Ordinal)) ||
            (!string.IsNullOrEmpty(sentPrompt) &&
             sentPrompt.Contains(json, StringComparison.Ordinal)))
            return null;

        // In a mixed conversation the last object must have the type of the
        // requested output. A partial new answer must not fall back to an
        // earlier idea/metadata/outline JSON schema or old chapter.
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (sentPrompt?.Contains("zerovideo_ideas_v1", StringComparison.OrdinalIgnoreCase) == true)
                return HasNonEmptyArray(root, "ideas") &&
                    IsStringValue(root, "response_type", "zerovideo_ideas_v1") ? json : null;

            if (sentPrompt?.Contains("zerovideo_publish_v2", StringComparison.OrdinalIgnoreCase) == true)
                return IsStringValue(root, "response_type", "zerovideo_publish_v2") &&
                    HasNonEmptyString(root, "youtube_title") &&
                    HasNonEmptyString(root, "tiktok_caption") ? json : null;

            if (sentPrompt?.Contains("ONE chapter", StringComparison.OrdinalIgnoreCase) == true &&
                sentPrompt.Contains("scenes", StringComparison.OrdinalIgnoreCase))
                return HasNonEmptyArray(root, "scenes") ? json : null;

            if (sentPrompt?.Contains("master outline", StringComparison.OrdinalIgnoreCase) == true ||
                sentPrompt?.Contains("master story outline", StringComparison.OrdinalIgnoreCase) == true)
                return HasNonEmptyArray(root, "chapters") ? json : null;
        }
        catch (JsonException) { return null; }

        return json;
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static bool HasNonEmptyArray(JsonElement root, string name)
        => TryGetProperty(root, name, out var value) &&
           value.ValueKind == JsonValueKind.Array &&
           value.GetArrayLength() > 0;

    private static bool HasNonEmptyString(JsonElement root, string name)
        => TryGetProperty(root, name, out var value) &&
           value.ValueKind == JsonValueKind.String &&
           !string.IsNullOrWhiteSpace(value.GetString());

    private static bool IsStringValue(JsonElement root, string name, string expected)
        => TryGetProperty(root, name, out var value) &&
           value.ValueKind == JsonValueKind.String &&
           string.Equals(value.GetString(), expected, StringComparison.OrdinalIgnoreCase);

    public static string? ExtractLastValidJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        string? lastValid = null;
        var depth = 0;
        var start = -1;
        var inString = false;
        var escaped = false;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (inString)
            {
                if (escaped) { escaped = false; continue; }
                if (ch == '\\') { escaped = true; continue; }
                if (ch == '"') inString = false;
                continue;
            }

            if (ch == '"') { if (depth > 0) inString = true; continue; }
            if (ch == '{') { if (depth == 0) start = i; depth++; continue; }
            if (ch != '}' || depth <= 0) continue;

            depth--;
            if (depth != 0 || start < 0) continue;
            var candidate = text[start..(i + 1)].Trim();
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind == JsonValueKind.Object) lastValid = candidate;
            }
            catch (JsonException) { }
            start = -1;
        }
        return lastValid;
    }
}
