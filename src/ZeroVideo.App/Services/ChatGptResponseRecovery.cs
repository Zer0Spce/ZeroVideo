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
