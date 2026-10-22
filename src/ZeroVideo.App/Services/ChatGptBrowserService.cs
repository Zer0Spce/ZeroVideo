using System.Text.Json;
using Microsoft.Playwright;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public sealed class ChatGptBrowserService(BrowserSession browser, AutomationProfileService profiles)
{
    private bool _newChatRequested;

    /// <summary>
    /// One-shot request. The next ChatGPT operation starts a fresh conversation,
    /// then all later ZeroVideo work continues in that same conversation.
    /// </summary>
    public void RequestNewChat() => _newChatRequested = true;

    public async Task<VideoProject> GenerateOutlineAsync(VideoProject seed, ZeroVideoSettings settings, string prompt, Action<string>? log = null, CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var raw = await GenerateTextAsync(seed, settings, prompt, log, ct);
            try
            {
                var project = ParseOutline(raw, seed);
                log?.Invoke($"Outline received: {project.Chapters.Count} chapters.");
                return project;
            }
            catch (Exception ex) when (attempt == 1)
            {
                log?.Invoke($"Outline JSON was invalid ({ex.Message}). Retrying in the same project chat.");
                prompt += "\nIMPORTANT: Your previous response could not be parsed. Return one complete valid JSON object only. Do not use markdown fences.";
            }
        }
        throw new InvalidDataException("ChatGPT did not return a valid outline after two attempts.");
    }

    public async Task<ProjectChapter> GenerateChapterAsync(VideoProject project, ProjectChapter metadata, ZeroVideoSettings settings, string prompt, Action<string>? log = null, CancellationToken ct = default)
    {
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var raw = await GenerateTextAsync(project, settings, prompt, log, ct);
            try
            {
                var json = ExtractJson(raw);
                var chapter = JsonSerializer.Deserialize<ProjectChapter>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Chapter JSON was empty.");
                if (chapter.Scenes.Count == 0) throw new InvalidDataException("Chapter has no scenes.");
                chapter.Number = metadata.Number;
                chapter.Title = string.IsNullOrWhiteSpace(chapter.Title) ? metadata.Title : chapter.Title;
                chapter.Purpose = string.IsNullOrWhiteSpace(chapter.Purpose) ? metadata.Purpose : chapter.Purpose;
                chapter.TargetSeconds = chapter.TargetSeconds <= 0 ? metadata.TargetSeconds : chapter.TargetSeconds;
                chapter.Narration = string.Join(" ", chapter.Scenes.Select(s => s.Narration.Trim()).Where(s => s.Length > 0));
                if (string.IsNullOrWhiteSpace(chapter.Narration)) throw new InvalidDataException("Chapter narration was empty.");
                log?.Invoke($"Chapter {chapter.Number}: {chapter.Scenes.Count} scenes / {chapter.Narration.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length} words.");
                return chapter;
            }
            catch (Exception ex) when (attempt == 1)
            {
                log?.Invoke($"Chapter {metadata.Number} JSON was invalid ({ex.Message}). Retrying in the same project chat.");
                prompt += "\nIMPORTANT: Your previous response could not be parsed. Return one complete valid JSON object only, with all scenes included and no markdown fences. Escape every double quote that appears inside a JSON string value as \".";
            }
        }
        throw new InvalidDataException($"ChatGPT did not return valid JSON for chapter {metadata.Number} after two attempts.");
    }

    public async Task<string> GenerateSceneImageAsync(VideoProject project, ProjectChapter chapter, ProjectScene scene, ZeroVideoSettings settings, Action<string>? log = null, CancellationToken ct = default)
    {
        var profile = await profiles.LoadAsync(settings.AutomationProfilePath);
        var page = await GetProjectPageAsync(project, settings, log, ct);
        var baseline = await CaptureImageBaselineAsync(page, profile.ChatGpt.GeneratedImages);
        var ratioWords = project.AspectRatio == "9:16" ? "vertical 9:16" : "widescreen 16:9";
        var styleRules = VisualStylePresets.GetPrompt(project.Style);
        var sceneIndex = chapter.Scenes.FindIndex(s => ReferenceEquals(s, scene) || s.Number == scene.Number);
        var previousNarration = sceneIndex > 0 ? chapter.Scenes[sceneIndex - 1].Narration : "none — this is the first scene in the chapter";
        var nextNarration = sceneIndex >= 0 && sceneIndex + 1 < chapter.Scenes.Count ? chapter.Scenes[sceneIndex + 1].Narration : "none — this is the final scene in the chapter";
        var prompt = $"""
Create ONE original {ratioWords} still image for this exact video scene.
VIDEO TITLE: {project.Title}
VIDEO TOPIC: {project.Topic}
CHAPTER: {chapter.Number}. {chapter.Title}
CHAPTER PURPOSE: {chapter.Purpose}
SCENE ROLE: {scene.Role}
CURRENT SCENE NARRATION: {scene.Narration}
PLANNED VISUAL DIRECTION: {scene.ImagePrompt}
SEARCH/REFERENCE IDEA: {scene.VisualQuery}
PREVIOUS SCENE NARRATION: {previousNarration}
NEXT SCENE NARRATION: {nextNarration}
VIDEO STYLE: {project.Style}
STYLE RULES: {styleRules}

VISUAL RELEVANCE RULES:
- The CURRENT SCENE NARRATION is the source of truth. The image must directly illustrate what is being said in this scene, not merely the overall theme of the video.
- Make the concrete subject, action, location, object, clue, document, vehicle, person, institution, or event named in the current narration the primary visual focus whenever visually appropriate.
- Use the previous and next scene only for continuity. Do not replace the current scene with a generic transition image.
- Prefer concrete explanatory imagery over generic symbolism. Do NOT default to random puzzle pieces, question marks, anonymous investigators, generic evidence boards, random mountains, or unrelated silhouettes unless the current narration specifically calls for them.
- If the narration is abstract, build the metaphor from topic-specific objects already established in this video so the frame still unmistakably belongs to this story.
- Preserve recurring subjects, props, clothing, locations, shapes, and era across adjacent scenes unless the narration intentionally changes them.
- If PLANNED VISUAL DIRECTION conflicts with CURRENT SCENE NARRATION, follow the narration and correct the visual.

QUALITY REQUIREMENTS:
- Generate the actual finished artwork at high resolution suitable for a 1080p YouTube video.
- Fill the composition cleanly for {ratioWords}; do not create a screenshot, browser mockup, framed webpage, UI panel, or image-within-an-interface.
- Produce ONE finished image file for this scene. If VIDEO STYLE is Comic Book Explainer, that one image SHOULD use a true comic-page composition when it helps the narration: 2-4 illustrated panels inside the same frame, separated by thick black gutters/borders, angled panel dividers, or inset close-up panels. Keep every panel tied only to THIS scene's narration; do not depict future scenes. Vary the panel layout naturally instead of repeating the same grid every time. For all other styles, use one full-frame composition with no multi-panel layout.
- No captions, no readable text, no logos, no watermark, and no UI. Comic Book Explainer may use empty graphic caption-box shapes only if they contain no readable text, but prefer pure visual panels.
- Keep the subject and era accurate to the scene.
- Do not add unrelated people or objects.
- Follow the STYLE RULES exactly and keep the visual style consistent with the project.
- If this is Stickman explainer, ALL people and characters must be stickmen; realistic humans are forbidden.
- If this is Cartoon, Hand-drawn, or Whiteboard explainer, do not drift into photorealistic human imagery.
""";

        await ChatGptComposer.EnterPromptAsync(page, profile.ChatGpt.PromptInputs, prompt, ct);
        await ChatGptComposer.SendAsync(page, profile.ChatGpt.SendButtons, ct);
        log?.Invoke($"Scene {chapter.Number}.{scene.Number}: waiting for a scene-specific ChatGPT visual...");

        var destination = ProjectMedia.GeneratedImagePath(project, chapter, scene);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        await WaitForGeneratedImageAndSaveAsync(
            page,
            profile.ChatGpt.GeneratedImages,
            profile.ChatGpt.StopButtons,
            baseline,
            destination,
            $"Scene {chapter.Number}.{scene.Number}",
            log,
            ct);

        RememberConversation(project, page);
        scene.MediaPath = destination;
        log?.Invoke($"Scene {chapter.Number}.{scene.Number}: full-resolution visual saved.");
        return destination;
    }

    public Task OpenAsync(ZeroVideoSettings settings)
        => browser.OpenManualAsync(settings, settings.ChatGptUrl);

    private async Task<string> GenerateTextAsync(VideoProject project, ZeroVideoSettings settings, string prompt, Action<string>? log, CancellationToken ct)
    {
        var profile = await profiles.LoadAsync(settings.AutomationProfilePath);
        var page = await GetProjectPageAsync(project, settings, log, ct);

        log?.Invoke("ChatGPT: waiting for the live composer...");
        await ChatGptComposer.EnterPromptAsync(page, profile.ChatGpt.PromptInputs, prompt, ct);

        var baseline = await TryReadLatestResponseAsync(page, profile.ChatGpt.AssistantMessages) ?? "";
        await ChatGptComposer.SendAsync(page, profile.ChatGpt.SendButtons, ct);
        log?.Invoke("ChatGPT: prompt sent; waiting for the finished response...");

        var response = await WaitForCompletedResponseAsync(
            page,
            profile.ChatGpt.AssistantMessages,
            profile.ChatGpt.StopButtons,
            baseline,
            log,
            ct);

        RememberConversation(project, page);
        log?.Invoke("ChatGPT: finished response detected.");
        return response;
    }

    private async Task<IPage> GetProjectPageAsync(VideoProject project, ZeroVideoSettings settings, Action<string>? log, CancellationToken ct)
    {
        await browser.EnsureStartedAsync(settings);
        var page = await browser.GetOrCreatePageAsync("chatgpt.com");

        if (_newChatRequested)
        {
            _newChatRequested = false;
            project.ChatGptConversationUrl = null;
            log?.Invoke("ChatGPT: starting a new project chat. All following ChatGPT work will reuse it.");
            await page.GotoAsync(settings.ChatGptUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120000 });
        }
        else if (!string.IsNullOrWhiteSpace(project.ChatGptConversationUrl))
        {
            if (!UrlsMatch(page.Url, project.ChatGptConversationUrl))
            {
                log?.Invoke("ChatGPT: reopening the saved project chat.");
                await page.GotoAsync(project.ChatGptConversationUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120000 });
            }
            else
            {
                log?.Invoke("ChatGPT: continuing the same project chat.");
            }
        }
        else
        {
            if (page.Url.Contains("/c/", StringComparison.OrdinalIgnoreCase) ||
                !page.Url.Contains("chatgpt.com", StringComparison.OrdinalIgnoreCase))
            {
                await page.GotoAsync(settings.ChatGptUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 120000 });
            }
            log?.Invoke("ChatGPT: creating the single chat for this project.");
        }

        ct.ThrowIfCancellationRequested();
        return page;
    }

    private static bool UrlsMatch(string current, string saved)
    {
        if (string.IsNullOrWhiteSpace(current) || string.IsNullOrWhiteSpace(saved)) return false;
        return string.Equals(current.TrimEnd('/'), saved.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);
    }

    private static void RememberConversation(VideoProject project, IPage page)
    {
        var url = page.Url;
        if (url.Contains("chatgpt.com", StringComparison.OrdinalIgnoreCase) &&
            url.Contains("/c/", StringComparison.OrdinalIgnoreCase))
        {
            project.ChatGptConversationUrl = url;
        }
    }

    private static async Task<ImageBaseline> CaptureImageBaselineAsync(IPage page, IEnumerable<string> configuredSelectors)
    {
        var candidates = await GetLargeImageCandidatesAsync(page, configuredSelectors);
        return new ImageBaseline(
            candidates.Select(x => x.Fingerprint).ToHashSet(StringComparer.Ordinal),
            candidates.Count);
    }

    private static async Task WaitForGeneratedImageAndSaveAsync(
        IPage page,
        IEnumerable<string> configuredImageSelectors,
        IEnumerable<string> stopSelectors,
        ImageBaseline baseline,
        string destination,
        string sceneLabel,
        Action<string>? log,
        CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        var sawGeneration = false;
        string? lastFingerprint = null;
        var stableSamples = 0;
        ImageCandidate? lastFresh = null;

        while (!ct.IsCancellationRequested && DateTime.UtcNow - started < TimeSpan.FromMinutes(10))
        {
            var isGenerating = await AnyVisibleAsync(page, stopSelectors);
            sawGeneration |= isGenerating;
            var candidates = await GetLargeImageCandidatesAsync(page, configuredImageSelectors);

            var fresh = candidates
                .Where(x => !baseline.Fingerprints.Contains(x.Fingerprint))
                .OrderByDescending(x => x.Area)
                .FirstOrDefault();
            if (fresh is null && candidates.Count > baseline.CandidateCount)
                fresh = candidates.OrderByDescending(x => x.Area).FirstOrDefault();

            if (fresh is not null)
            {
                lastFresh = fresh;
                if (string.Equals(lastFingerprint, fresh.Fingerprint, StringComparison.Ordinal))
                    stableSamples++;
                else
                {
                    lastFingerprint = fresh.Fingerprint;
                    stableSamples = 0;
                }

                if (!isGenerating && stableSamples >= 1)
                {
                    if (await TrySaveImageCandidateAsync(fresh, destination, sceneLabel, log))
                    {
                        log?.Invoke($"{sceneLabel}: full-resolution generated image detected and saved.");
                        return;
                    }
                    stableSamples = 0;
                }
            }

            if (sawGeneration && !isGenerating)
            {
                await Task.Delay(900, ct);
                var finalCandidates = await GetLargeImageCandidatesAsync(page, configuredImageSelectors);
                var finalFresh = finalCandidates
                    .Where(x => !baseline.Fingerprints.Contains(x.Fingerprint))
                    .OrderByDescending(x => x.Area)
                    .FirstOrDefault();
                if (finalFresh is null && finalCandidates.Count > baseline.CandidateCount)
                    finalFresh = finalCandidates.OrderByDescending(x => x.Area).FirstOrDefault();

                if (finalFresh is not null && await TrySaveImageCandidateAsync(finalFresh, destination, sceneLabel, log))
                {
                    log?.Invoke($"{sceneLabel}: image generation finished; saved the original full-resolution visual.");
                    return;
                }
            }

            await Task.Delay(1200, ct);
        }

        if (lastFresh is not null && await TrySaveImageCandidateAsync(lastFresh, destination, sceneLabel, log))
        {
            log?.Invoke($"{sceneLabel}: generation controls were inconclusive, but the original full-resolution image asset was available and saved.");
            return;
        }

        throw new TimeoutException("ChatGPT generated the scene prompt, but ZeroVideo could not obtain the original full-resolution image asset. No webpage screenshot was used. The scene can be retried safely.");
    }

    private static async Task<List<ImageCandidate>> GetLargeImageCandidatesAsync(IPage page, IEnumerable<string> configuredSelectors)
    {
        var selectors = new[]
        {
            "main img",
            "main figure img",
            "main article img",
            "main [data-testid^='conversation-turn-'] img",
            "[data-message-author-role='assistant'] img"
        }
        .Concat(configuredSelectors)
        .Where(s => !string.IsNullOrWhiteSpace(s))
        .Distinct()
        .ToList();

        var result = new List<ImageCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var selector in selectors)
        {
            try
            {
                var items = page.Locator(selector);
                var count = await items.CountAsync();
                for (var i = 0; i < count; i++)
                {
                    try
                    {
                        var item = items.Nth(i);
                        if (!await item.IsVisibleAsync()) continue;
                        var box = await item.BoundingBoxAsync();
                        if (box is null || box.Width < 180 || box.Height < 120 || box.Width * box.Height < 30000) continue;

                        var naturalWidth = await item.EvaluateAsync<int>("el => el.naturalWidth || 0");
                        var naturalHeight = await item.EvaluateAsync<int>("el => el.naturalHeight || 0");
                        if (naturalWidth < 256 || naturalHeight < 128) continue;

                        var src = await item.EvaluateAsync<string>("el => el.currentSrc || el.src || ''");
                        var alt = await item.GetAttributeAsync("alt") ?? "";
                        var sourceKey = src.Length > 320
                            ? $"{src.Length}:{src.GetHashCode(StringComparison.Ordinal)}"
                            : src;
                        var fingerprint = !string.IsNullOrWhiteSpace(sourceKey)
                            ? $"src:{sourceKey}|alt:{alt}|native:{naturalWidth}x{naturalHeight}"
                            : $"nosrc:{alt}|{naturalWidth}x{naturalHeight}|{i}";

                        if (!seen.Add(fingerprint)) continue;
                        result.Add(new ImageCandidate(item, fingerprint, (double)naturalWidth * naturalHeight));
                    }
                    catch
                    {
                        // ChatGPT may replace image nodes while generation finishes.
                    }
                }
            }
            catch
            {
                // Continue with the other selectors when ChatGPT changes one DOM path.
            }
        }

        return result;
    }

    private static Task<bool> TrySaveImageCandidateAsync(ImageCandidate candidate, string destination, string sceneLabel, Action<string>? log)
        => ChatGptImageAssetSaver.TrySaveFullResolutionPngAsync(candidate.Locator, destination, sceneLabel, log);

    private static async Task<string> WaitForCompletedResponseAsync(
        IPage page,
        IEnumerable<string> configuredResponseSelectors,
        IEnumerable<string> stopSelectors,
        string baseline,
        Action<string>? log,
        CancellationToken ct)
    {
        string last = "";
        var stableSamples = 0;
        var sawGeneration = false;
        var started = DateTime.UtcNow;

        while (!ct.IsCancellationRequested && DateTime.UtcNow - started < TimeSpan.FromMinutes(12))
        {
            var isGenerating = await AnyVisibleAsync(page, stopSelectors);
            sawGeneration |= isGenerating;

            var current = await TryReadLatestResponseAsync(page, configuredResponseSelectors);
            if (!string.IsNullOrWhiteSpace(current) &&
                !string.Equals(current, baseline, StringComparison.Ordinal))
            {
                if (string.Equals(current, last, StringComparison.Ordinal))
                    stableSamples++;
                else
                {
                    last = current;
                    stableSamples = 0;
                }

                var completeJsonVisible = TryExtractAndRepairTopLevelJsonObject(current) is not null;
                if (completeJsonVisible && stableSamples >= 1)
                {
                    log?.Invoke("ChatGPT: complete assistant JSON is stable; accepting the response.");
                    return current;
                }

                if (!isGenerating && stableSamples >= 1)
                    return current;
            }

            if (sawGeneration && !isGenerating && !string.IsNullOrWhiteSpace(last))
            {
                await Task.Delay(700, ct);
                var finalRead = await TryReadLatestResponseAsync(page, configuredResponseSelectors);
                if (!string.IsNullOrWhiteSpace(finalRead) &&
                    !string.Equals(finalRead, baseline, StringComparison.Ordinal))
                    return finalRead;
            }

            await Task.Delay(1000, ct);
        }

        if (!string.IsNullOrWhiteSpace(last) &&
            !string.Equals(last, baseline, StringComparison.Ordinal))
        {
            log?.Invoke("ChatGPT: completion controls were inconclusive, but a new assistant JSON response was visible; using it.");
            return last;
        }

        throw new TimeoutException("ChatGPT finished in the browser, but ZeroVideo could not find a new assistant JSON response in the visible conversation.");
    }

    private static async Task<string?> TryReadLatestResponseAsync(IPage page, IEnumerable<string> configuredSelectors)
    {
        // Prefer ChatGPT's own author marker directly. Do not require the wrapper itself to
        // report visible: ChatGPT can keep the role node as a layout wrapper while its child
        // response content is visible on screen.
        try
        {
            var directAssistant = page.Locator("[data-message-author-role='assistant']");
            var directCount = await directAssistant.CountAsync();
            for (var i = directCount - 1; i >= 0; i--)
            {
                var item = directAssistant.Nth(i);
                var text = (await item.InnerTextAsync()).Trim();
                if (LooksLikeJsonPayload(text)) return text;
            }
        }
        catch
        {
            // ChatGPT can replace the latest conversation node while streaming.
        }

        // Fallback for alternate DOM layouts. Only accept nodes that can still be tied to
        // assistant-authored content. Never scan the whole page or user prompt.
        var fallbacks = new[]
        {
            "main article[data-testid^='conversation-turn-']",
            "main [data-testid^='conversation-turn-']",
            "[data-message-author-role='assistant'] .markdown",
            "[data-message-author-role='assistant'] [class*='markdown']"
        };

        foreach (var selector in configuredSelectors.Concat(fallbacks).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct())
        {
            try
            {
                var items = page.Locator(selector);
                var count = await items.CountAsync();
                for (var i = count - 1; i >= 0; i--)
                {
                    var item = items.Nth(i);
                    if (!await item.IsVisibleAsync()) continue;

                    string? text = null;
                    try
                    {
                        var directRole = await item.GetAttributeAsync("data-message-author-role");
                        if (string.Equals(directRole, "assistant", StringComparison.OrdinalIgnoreCase))
                        {
                            text = (await item.InnerTextAsync()).Trim();
                        }
                        else
                        {
                            var assistantAncestor = item.Locator("xpath=ancestor-or-self::*[@data-message-author-role='assistant']").First;
                            if (await assistantAncestor.CountAsync() > 0)
                            {
                                text = (await assistantAncestor.InnerTextAsync()).Trim();
                            }
                            else
                            {
                                var assistantChild = item.Locator("[data-message-author-role='assistant']").Last;
                                if (await assistantChild.CountAsync() > 0)
                                    text = (await assistantChild.InnerTextAsync()).Trim();
                            }
                        }
                    }
                    catch
                    {
                        text = null;
                    }

                    if (LooksLikeJsonPayload(text)) return text;
                }
            }
            catch
            {
                // Keep trying alternate assistant-specific selectors.
            }
        }

        return null;
    }

    private static async Task<bool> AnyVisibleAsync(IPage page, IEnumerable<string> selectors)
    {
        foreach (var selector in selectors.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            try
            {
                var items = page.Locator(selector);
                var count = await items.CountAsync();
                for (var i = count - 1; i >= 0; i--)
                {
                    if (await items.Nth(i).IsVisibleAsync()) return true;
                }
            }
            catch { }
        }
        return false;
    }

    private static string? TryExtractLastValidJsonObject(string? text)
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
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (ch == '"') inString = false;
                continue;
            }

            if (ch == '"')
            {
                if (depth > 0) inString = true;
                continue;
            }

            if (ch == '{')
            {
                if (depth == 0) start = i;
                depth++;
                continue;
            }

            if (ch != '}' || depth <= 0) continue;

            depth--;
            if (depth != 0 || start < 0) continue;

            var candidate = text[start..(i + 1)].Trim();
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                if (doc.RootElement.ValueKind == JsonValueKind.Object)
                    lastValid = candidate;
            }
            catch
            {
                // Not valid JSON; continue scanning for a later object.
            }

            start = -1;
            inString = false;
            escaped = false;
        }

        return lastValid;
    }

    private static VideoProject ParseOutline(string raw, VideoProject seed)
    {
        var json = ExtractJson(raw);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        seed.Title = root.TryGetProperty("title", out var title) ? title.GetString() ?? seed.Topic : seed.Topic;
        seed.Sources = root.TryGetProperty("sources", out var sources) ? sources.EnumerateArray().Select(x => x.GetString() ?? "").Where(x => x.Length > 0).ToList() : [];
        seed.Chapters = JsonSerializer.Deserialize<List<ProjectChapter>>(root.GetProperty("chapters").GetRawText(), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [];
        if (seed.Chapters.Count == 0) throw new InvalidDataException("Outline contained no chapters.");
        for (var i = 0; i < seed.Chapters.Count; i++)
        {
            seed.Chapters[i].Number = i + 1;
            seed.Chapters[i].Scenes = [];
            seed.Chapters[i].Narration = "";
        }
        return seed;
    }

    private static bool LooksLikeJsonPayload(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var first = text.IndexOf('{');
        var last = text.LastIndexOf('}');
        return first >= 0 && last > first;
    }

    private static string? TryExtractAndRepairTopLevelJsonObject(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        // Assistant turns are expected to contain one chapter/outline object. Prefer the
        // widest object first so malformed quoting cannot make us accidentally accept a
        // smaller nested scene object.
        var first = text.IndexOf('{');
        var last = text.LastIndexOf('}');
        if (first < 0 || last <= first) return null;

        var candidate = text[first..(last + 1)].Trim();
        if (IsValidJsonObject(candidate)) return candidate;

        var repaired = RepairUnescapedJsonStringQuotes(candidate);
        return IsValidJsonObject(repaired) ? repaired : null;
    }

    private static bool IsValidJsonObject(string candidate)
    {
        try
        {
            using var doc = JsonDocument.Parse(candidate);
            return doc.RootElement.ValueKind == JsonValueKind.Object;
        }
        catch
        {
            return false;
        }
    }

    private static string RepairUnescapedJsonStringQuotes(string candidate)
    {
        var output = new System.Text.StringBuilder(candidate.Length + 32);
        var inString = false;
        var escaped = false;

        for (var i = 0; i < candidate.Length; i++)
        {
            var ch = candidate[i];

            if (!inString)
            {
                output.Append(ch);
                if (ch == '"') inString = true;
                continue;
            }

            if (escaped)
            {
                output.Append(ch);
                escaped = false;
                continue;
            }

            if (ch == '\\')
            {
                output.Append(ch);
                escaped = true;
                continue;
            }

            if (ch != '"')
            {
                output.Append(ch);
                continue;
            }

            if (IsLikelyJsonStringTerminator(candidate, i))
            {
                output.Append(ch);
                inString = false;
            }
            else
            {
                // ChatGPT occasionally writes quoted titles inside narration without JSON
                // escaping, e.g. titled "Some Document". Preserve the quote as content.
                output.Append("\\\"");
            }
        }

        return output.ToString();
    }

    private static bool IsLikelyJsonStringTerminator(string text, int quoteIndex)
    {
        var i = quoteIndex + 1;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        if (i >= text.Length) return true;

        var next = text[i];
        if (next is ':' or '}' or ']') return true;

        if (next == ',')
        {
            i++;
            while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
            if (i >= text.Length) return true;
            // Object properties and string arrays normally begin the next value with a quote.
            // A comma followed by ordinary prose is more likely part of an inner quotation.
            return text[i] is '"' or '}' or ']';
        }

        return false;
    }

    private static string ExtractJson(string raw)
    {
        // First try the complete assistant object, including a narrowly-scoped repair for
        // accidental unescaped quotes inside JSON strings. Only then fall back to the older
        // valid-object scanner for responses that contain surrounding conversation text.
        var json = TryExtractAndRepairTopLevelJsonObject(raw) ?? TryExtractLastValidJsonObject(raw);
        if (json is null) throw new InvalidDataException("ChatGPT did not return a valid JSON object.");
        return json;
    }

    private sealed record ImageCandidate(ILocator Locator, string Fingerprint, double Area);
    private sealed record ImageBaseline(HashSet<string> Fingerprints, int CandidateCount);
}
