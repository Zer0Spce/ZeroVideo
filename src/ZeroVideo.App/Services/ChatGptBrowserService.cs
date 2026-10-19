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
                prompt += "\nIMPORTANT: Your previous response could not be parsed. Return one complete valid JSON object only, with all scenes included and no markdown fences.";
            }
        }
        throw new InvalidDataException($"ChatGPT did not return valid JSON for chapter {metadata.Number} after two attempts.");
    }

    public async Task<string> GenerateSceneImageAsync(VideoProject project, ProjectChapter chapter, ProjectScene scene, ZeroVideoSettings settings, Action<string>? log = null, CancellationToken ct = default)
    {
        var profile = await profiles.LoadAsync(settings.AutomationProfilePath);
        if (profile.ChatGpt.GeneratedImages.Count == 0) throw new InvalidOperationException("No ChatGPT generated-image selectors are configured.");

        var page = await GetProjectPageAsync(project, settings, log, ct);
        var imageSelector = string.Join(",", profile.ChatGpt.GeneratedImages);
        var before = await page.Locator(imageSelector).CountAsync();
        var ratioWords = project.AspectRatio == "9:16" ? "vertical 9:16" : "widescreen 16:9";
        var prompt = $"""
Create ONE original {ratioWords} documentary-style still image for this video scene.
TOPIC: {project.Topic}
SCENE NARRATION: {scene.Narration}
VISUAL DIRECTION: {scene.ImagePrompt}
SEARCH/REFERENCE IDEA: {scene.VisualQuery}
VIDEO STYLE: {project.Style}

Requirements: one single image only; no collage; no captions; no readable text; no logos; no watermark; no UI; visually clear at YouTube resolution. Keep the subject and era accurate to the scene. Do not add unrelated people or objects.
""";

        await ChatGptComposer.EnterPromptAsync(page, profile.ChatGpt.PromptInputs, prompt, ct);
        await ChatGptComposer.SendAsync(page, profile.ChatGpt.SendButtons, ct);
        log?.Invoke($"Scene {chapter.Number}.{scene.Number}: waiting for ChatGPT visual in the project chat...");

        var image = await WaitForNewImageAsync(page, imageSelector, before, ct);
        RememberConversation(project, page);
        var destination = ProjectMedia.GeneratedImagePath(project, chapter, scene);
        await image.ScreenshotAsync(new() { Path = destination, Type = ScreenshotType.Png });
        scene.MediaPath = destination;
        log?.Invoke($"Scene {chapter.Number}.{scene.Number}: visual saved.");
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
            // A project without a saved conversation must not accidentally inherit a chat
            // left open by another project. Start from ChatGPT's new-chat page once.
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

    private static async Task<ILocator> WaitForNewImageAsync(IPage page, string selector, int previousCount, CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        while (!ct.IsCancellationRequested && DateTime.UtcNow - started < TimeSpan.FromMinutes(8))
        {
            var images = page.Locator(selector);
            var count = await images.CountAsync();
            if (count > previousCount)
            {
                var latest = images.Last;
                try
                {
                    await latest.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 5000 });
                    var box = await latest.BoundingBoxAsync();
                    if (box is not null && box.Width >= 200 && box.Height >= 200) return latest;
                }
                catch { }
            }
            await Task.Delay(2500, ct);
        }
        throw new TimeoutException("ChatGPT image generation did not produce a detectable image. Check the visible browser and image selectors.");
    }

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
                !string.Equals(current, baseline, StringComparison.Ordinal) &&
                LooksLikeJsonResponse(current))
            {
                if (string.Equals(current, last, StringComparison.Ordinal))
                    stableSamples++;
                else
                {
                    last = current;
                    stableSamples = 0;
                }

                // Two identical samples plus no visible Stop control is enough to treat
                // the answer as complete. This also works if the site's assistant-role
                // attribute changes, because TryReadLatestResponseAsync has turn fallbacks.
                if (!isGenerating && stableSamples >= 1)
                    return current;
            }
            else if (!string.IsNullOrWhiteSpace(current) && current != last)
            {
                // Keep this for diagnostics/fallback, but do not accept a user prompt or
                // partially rendered non-JSON turn as the assistant result.
                last = current;
                stableSamples = 0;
            }

            if (sawGeneration && !isGenerating && !string.IsNullOrWhiteSpace(last) && LooksLikeJsonResponse(last))
            {
                // Give the DOM one extra beat after streaming stops, then re-read it.
                await Task.Delay(1200, ct);
                var finalRead = await TryReadLatestResponseAsync(page, configuredResponseSelectors);
                if (!string.IsNullOrWhiteSpace(finalRead) && LooksLikeJsonResponse(finalRead))
                    return finalRead;
            }

            await Task.Delay(1500, ct);
        }

        if (!string.IsNullOrWhiteSpace(last) && LooksLikeJsonResponse(last))
        {
            log?.Invoke("ChatGPT: response detector timed out, but a complete JSON-looking answer was visible; using it.");
            return last;
        }

        throw new TimeoutException("ChatGPT finished in the browser, but ZeroVideo could not read the response. The ChatGPT response selectors may need updating.");
    }

    private static async Task<string?> TryReadLatestResponseAsync(IPage page, IEnumerable<string> configuredSelectors)
    {
        var fallbacks = new[]
        {
            "[data-message-author-role='assistant']",
            "main article[data-testid^='conversation-turn-']",
            "main [data-testid^='conversation-turn-']",
            "main .markdown",
            "main [class*='markdown']"
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
                    var text = (await item.InnerTextAsync()).Trim();
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
            }
            catch
            {
                // ChatGPT can replace conversation turns while streaming; always re-query.
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

    private static bool LooksLikeJsonResponse(string text)
    {
        var value = text.Trim();
        if (value.StartsWith("```", StringComparison.Ordinal))
        {
            var newline = value.IndexOf('\n');
            if (newline >= 0) value = value[(newline + 1)..].TrimStart();
        }

        return value.StartsWith("{", StringComparison.Ordinal) && value.Contains('}');
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

    private static string ExtractJson(string raw)
    {
        raw = raw.Trim();
        if (raw.StartsWith("```"))
        {
            var firstNewLine = raw.IndexOf('\n');
            var lastFence = raw.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewLine >= 0 && lastFence > firstNewLine) raw = raw[(firstNewLine + 1)..lastFence].Trim();
        }
        var start = raw.IndexOf('{');
        var end = raw.LastIndexOf('}');
        if (start < 0 || end <= start) throw new InvalidDataException("ChatGPT did not return a JSON object.");
        return raw[start..(end + 1)];
    }
}
