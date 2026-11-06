using System.Text.Json;

namespace ZeroVideo.App.Services;

/// <summary>
/// Pure, browser-independent recovery checks. Never reuse an earlier assistant JSON
/// when a new turn is still streaming or has returned invalid content.
/// </summary>
public static class ChatGptResponseRecovery
{
    /// <summary>
    /// Stop controls are not a reliable completion signal in every ChatGPT UI.
    /// Accept a fully parsed new JSON only once it is stable, and if the Stop
    /// button still appears, require the entire visible response to remain
    /// unchanged for 12 seconds as a second independent completion signal.
    /// </summary>
    public static bool CanAcceptStableJson(
        bool stopControlVisible,
        int stableJsonSamples,
        TimeSpan unchangedVisibleTextDuration,
        bool visibleTextContainsCompleteJson)
        => stableJsonSamples >= 2 &&
           (!stopControlVisible ||
            (visibleTextContainsCompleteJson &&
             unchangedVisibleTextDuration >= TimeSpan.FromSeconds(12)));

    /// <summary>
    /// A distinct response contract for browser automation. The caller adds a
    /// unique request key and a final completion key to each JSON response.
    /// The app saves the verified document to a local .json file; asking the
    /// ChatGPT website to produce/download an attachment is not required.
    /// </summary>
    public static string BuildStrictJsonInstruction(string requestId)
    {
        var endValue = "ZV_END_" + requestId;
        var fence = new string((char)96, 3);
        return "\n\nFINAL OUTPUT FORMAT — OVERRIDES ANY EARLIER 'NO MARKDOWN' OR 'RAW JSON' INSTRUCTION:\n" +
            "Render your ENTIRE response as exactly ONE Markdown fenced JSON CODE BLOCK with the visible Copy control. " +
            "This is important: DO NOT display ordinary paragraph text or bare JSON on the page.\n" +
            "The FIRST line must be literally " + fence + "json. " +
            "The LAST line must be literally " + fence + ". No text before or after the block.\n" +
            "Inside the code block write one complete, strictly valid JSON OBJECT (double quotes, escaped control characters, no trailing commas).\n" +
            "The FIRST top-level key must be \"zerovideo_request_id\" with exact string value \"" + requestId + "\".\n" +
            "The LAST top-level key must be \"zerovideo_end_marker\" with exact string value \"" + endValue + "\".\n" +
            "Keep ALL required chapter/outline/scene fields, numbers, arrays and narration. Do not cut or abbreviate JSON.\n" +
            "The output must be a COPYABLE code block, not a file attachment, image, table, text paragraph, or writing block.\n" +
            "This valid JSON object must parse as a single .json file when copied from the code block.\n";
    }

    /// <summary>
    /// Preferred source: the text of a rendered JSON code block. Validate its
    /// exact request ID and completion marker rather than relying on a page
    /// selector or accepting any old JSON in the conversation.
    /// </summary>
    public static string? FindJsonInCodeBlock(string? codeText, string requestId)
    {
        if (string.IsNullOrWhiteSpace(codeText)) return null;
        var trimmed = codeText.Trim();
        var fence = new string((char)96, 3);
        if (trimmed.StartsWith(fence, StringComparison.Ordinal))
        {
            var newline = trimmed.IndexOf('\n');
            if (newline < 0 || !trimmed[..newline].Contains("json", StringComparison.OrdinalIgnoreCase))
                return null;
            trimmed = trimmed[(newline + 1)..].Trim();
            if (!trimmed.EndsWith(fence, StringComparison.Ordinal)) return null;
            trimmed = trimmed[..^3].Trim();
        }

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !IsStringValue(doc.RootElement, "zerovideo_request_id", requestId) ||
                !IsStringValue(doc.RootElement, "zerovideo_end_marker", "ZV_END_" + requestId))
                return null;
            return trimmed;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// If ChatGPT ignores the fence format and writes malformed *bare* JSON,
    /// extract only a newly emitted tagged object. Do not return the user's
    /// own prompt or an earlier response as a repair candidate.
    /// </summary>
    public static string? FindTaggedCandidateForRepair(string? visibleMain, string requestId)
    {
        if (string.IsNullOrWhiteSpace(visibleMain) || string.IsNullOrWhiteSpace(requestId))
            return null;

        var key = "\"zerovideo_request_id\"";
        var keyIndex = visibleMain.LastIndexOf(key, StringComparison.Ordinal);
        if (keyIndex < 0) return null;

        var open = visibleMain.LastIndexOf('{', keyIndex);
        // The request ID is the FIRST property in the response. The appended
        // prompt mentions the key but does not place it after an opening brace.
        if (open < 0 || keyIndex - open > 160 ||
            !string.IsNullOrWhiteSpace(visibleMain[(open + 1)..keyIndex]))
            return null;

        var endKey = "\"zerovideo_end_marker\"";
        var endIndex = visibleMain.IndexOf(endKey, keyIndex, StringComparison.Ordinal);
        if (endIndex < 0) return null;
        var expectedValue = "\"ZV_END_" + requestId + "\"";
        var valueIndex = visibleMain.IndexOf(expectedValue, endIndex, StringComparison.Ordinal);
        if (valueIndex < 0) return null;
        var close = visibleMain.IndexOf('}', valueIndex + expectedValue.Length);
        if (close < 0) return null;

        var candidate = visibleMain[open..(close + 1)];
        if (!candidate.Contains("\"" + requestId + "\"", StringComparison.Ordinal))
            return null;
        return candidate;
    }

    public static bool HasEndMarker(string? content, string requestId)
        => !string.IsNullOrWhiteSpace(content) &&
           content.Contains("\"zerovideo_end_marker\"", StringComparison.Ordinal) &&
           content.Contains("ZV_END_" + requestId, StringComparison.Ordinal);

    public static string? FindJsonForRequest(string? conversationText, string requestId)
    {
        if (string.IsNullOrWhiteSpace(conversationText) || string.IsNullOrWhiteSpace(requestId))
            return null;

        string? lastMatch = null;
        var depth = 0;
        var start = -1;
        var quoted = false;
        var escaped = false;
        for (var i = 0; i < conversationText.Length; i++)
        {
            var ch = conversationText[i];
            if (quoted)
            {
                if (escaped) { escaped = false; continue; }
                if (ch == '\\') { escaped = true; continue; }
                if (ch == '"') quoted = false;
                continue;
            }
            if (ch == '"') { if (depth > 0) quoted = true; continue; }
            if (ch == '{') { if (depth == 0) start = i; depth++; continue; }
            if (ch != '}' || depth == 0) continue;
            depth--;
            if (depth != 0 || start < 0) continue;
            var candidate = conversationText[start..(i + 1)];
            start = -1;
            if (!candidate.Contains(requestId, StringComparison.Ordinal)) continue;
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !IsStringValue(root, "zerovideo_request_id", requestId) ||
                    !IsStringValue(root, "zerovideo_end_marker", "ZV_END_" + requestId))
                    continue;
                lastMatch = candidate;
            }
            catch (JsonException) { /* May be a prompt example or streaming JSON. */ }
        }
        return lastMatch;
    }

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
