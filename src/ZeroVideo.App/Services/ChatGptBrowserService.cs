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
- One single image only; no multi-panel layout.
- No captions, no readable text, no logos, no watermark, and no UI.
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

    public Task<string> GenerateVideoIdeasAsync(string niche, int count, ZeroVideoSettings settings, Action<string>? log = null, CancellationToken ct = default)
    {
        // Use the same JSON capture pipeline as working outline generation.
        // A marker prevents accidental acceptance of unrelated JSON in the conversation.
        var prompt = $"Generate exactly {count} distinct, factual and compelling video ideas for the niche: {niche}. " +
            "Use curiosity-driven YouTube/TikTok titles and specific opening hooks that deliver on the title. " +
            "Avoid repetition, invented facts, misleading clickbait and guaranteed-view claims. " +
            "IMPORTANT: Return ONLY one complete valid JSON object, without markdown or commentary. " +
            "Required JSON schema: " +
            """ 
            {"response_type":"zerovideo_ideas_v1","niche":"the requested niche","ideas":[{"title":"Specific video title","hook":"Compelling opening hook"}]}
            """ +
            $" The ideas array must contain exactly {count} entries. Every title and hook must be nonempty.";
        return GenerateTextAsync(new VideoProject { Title = $"Ideas: {niche}", Topic = niche }, settings, prompt, log, ct);
    }

    public Task OpenAsync(ZeroVideoSettings settings)
        => browser.OpenManualAsync(settings, settings.ChatGptUrl);

    public async Task<string> GenerateTextAsync(VideoProject project, ZeroVideoSettings settings, string prompt, Action<string>? log, CancellationToken ct)
    {
        var profile = await profiles.LoadAsync(settings.AutomationProfilePath);
        var page = await GetProjectPageAsync(project, settings, log, ct);

        log?.Invoke("ChatGPT: waiting for the live composer...");
        await ChatGptComposer.EnterPromptAsync(page, profile.ChatGpt.PromptInputs, prompt, ct);

        var baseline = await TryReadLatestResponseAsync(page, profile.ChatGpt.AssistantMessages) ?? "";
        var baselineAssistant = await TryReadLatestAssistantTextAsync(page) ?? "";
        await ChatGptComposer.SendAsync(page, profile.ChatGpt.SendButtons, ct);
        log?.Invoke("ChatGPT: prompt sent; waiting for the finished response...");

        var response = await WaitForCompletedResponseAsync(
            page,
            profile.ChatGpt.AssistantMessages,
            profile.ChatGpt.StopButtons,
            baseline,
            baselineAssistant,
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
        string baselineAssistant,
        Action<string>? log,
        CancellationToken ct)
    {
        string last = "";
        string lastAssistant = "";
        var rawStableSamples = 0;
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

                if (!isGenerating && stableSamples >= 1)
                    return current;
            }

            if (sawGeneration && !isGenerating && !string.IsNullOrWhiteSpace(last))
            {
                await Task.Delay(1000, ct);
                var finalRead = await TryReadLatestResponseAsync(page, configuredResponseSelectors);
                if (!string.IsNullOrWhiteSpace(finalRead) &&
                    !string.Equals(finalRead, baseline, StringComparison.Ordinal))
                    return finalRead;
            }

            // If ChatGPT returned normal prose, a structured content block, or
            // code the old selectors cannot parse as JSON, do not spin for 12
            // minutes. Detect a newly completed assistant message and hand it
            // to the existing outline/chapter validation + one-time repair path.
            // Never use the USER's JSON schema prompt as the assistant output.
            if (!isGenerating && DateTime.UtcNow - started > TimeSpan.FromSeconds(30))
            {
                var assistant = await TryReadLatestAssistantTextAsync(page);
                if (!string.IsNullOrWhiteSpace(assistant) &&
                    !string.Equals(assistant, baselineAssistant, StringComparison.Ordinal))
                {
                    if (string.Equals(assistant, lastAssistant, StringComparison.Ordinal))
                        rawStableSamples++;
                    else
                    {
                        lastAssistant = assistant;
                        rawStableSamples = 0;
                    }

                    if (rawStableSamples >= 6)
                    {
                        var recovered = TryExtractLastValidJsonObject(assistant);
                        if (recovered is not null && !string.Equals(recovered, baseline, StringComparison.Ordinal))
                        {
                            log?.Invoke("ChatGPT: recovered JSON from the latest assistant turn.");
                            return recovered;
                        }
                        await SaveChatGptResponseDiagnosticAsync(page,
                            "Completed-NonJson", assistant, log);
                        log?.Invoke("ChatGPT: new assistant reply completed but is not valid JSON. " +
                            "Returning it to the existing validation/retry flow.");
                        return assistant;
                    }
                }
            }
            await Task.Delay(1200, ct);
        }

        if (!string.IsNullOrWhiteSpace(last) &&
            !string.Equals(last, baseline, StringComparison.Ordinal))
        {
            log?.Invoke("ChatGPT: completion controls were inconclusive, but a new valid JSON response was visible; using it.");
            return last;
        }

        var lastVisible = await TryReadLatestAssistantTextAsync(page) ?? "";
        await SaveChatGptResponseDiagnosticAsync(page, "Json-NotFound", lastVisible, log);
        throw new TimeoutException("ZeroVideo could not find a new ChatGPT assistant response in the browser. " +
            "A local diagnostic was saved in %LOCALAPPDATA%\\ZeroVideo\\diagnostics. " +
            "Check the diagnostic TXT and PNG to see whether ChatGPT generated a reply, displayed a sign-in prompt, or changed its page structure.");
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

                    try
                    {
                        if (await item.Locator("[data-message-author-role='user']").CountAsync() > 0 &&
                            await item.Locator("[data-message-author-role='assistant']").CountAsync() == 0)
                            continue;
                    }
                    catch { }

                    var text = (await item.InnerTextAsync()).Trim();
                    var json = TryExtractLastValidJsonObject(text);
                    if (json is not null) return json;
                }
            }
            catch
            {
                // ChatGPT can replace conversation turns while streaming; always re-query.
            }
        }

        try
        {
            var main = page.Locator("main").First;
            if (await main.CountAsync() > 0 && await main.IsVisibleAsync())
            {
                var text = await main.InnerTextAsync();
                var json = TryExtractLastValidJsonObject(text);
                if (json is not null) return json;
            }
        }
        catch { }

        return null;
    }

    // The DOM of ChatGPT can vary across accounts and updates. Collect only
    // assistant-authored turns; never accept a JSON schema example in a user
    // prompt or sidebar as if it were a fresh ChatGPT answer.
    private static async Task<string?> TryReadLatestAssistantTextAsync(IPage page)
    {
        const string script = @"
() => {
  const getText = n => {
    const raw = (n.innerText || n.textContent || '').trim();
    const code = [...n.querySelectorAll('pre code,pre,code,textarea')]
       .map(e => e instanceof HTMLTextAreaElement ? e.value :
         (e.innerText || e.textContent || '')).filter(Boolean).join('\n');
    // Prefer the full assistant response; retain embedded JSON/markup blocks.
    return (raw.length >= code.length ? raw : code).trim();
  };
  const visible = n => {
    const st = getComputedStyle(n);
    return st.visibility !== 'hidden' && st.display !== 'none';
  };
  const selectors = [
    '[data-message-author-role=""assistant""]',
    '[data-testid=""conversation-turn-assistant""]',
    '[data-testid*=""assistant-response""]',
    '[data-role=""assistant""]',
    'article[data-message-author-role=""assistant""]'
  ];
  let found = [];
  for(const selector of selectors) {
    try {
      for(const n of document.querySelectorAll(selector)) {
        if(!visible(n))continue;
        if(n.closest('[data-message-author-role=""user""]'))continue;
        const t=getText(n);
        if(t.length)found.push({n,t});
      }
    }catch{}
  }
  if(found.length) {
    // DOM order over selector order prevents returning an old answer.
    found.sort((a,b) => {
      const v=a.n.compareDocumentPosition(b.n);
      return (v&Node.DOCUMENT_POSITION_FOLLOWING)?-1:
        (v&Node.DOCUMENT_POSITION_PRECEDING)?1:0;
    });
    return found[found.length-1].t.slice(0,200000);
  }
  // Fallback: modern ChatGPT turns may lose the author-role attributes.
  // Only accept a turn with explicit assistant-like markup; reject user turns.
  let turns=[...document.querySelectorAll(
     '[data-testid^=""conversation-turn-""], [data-testid*=""assistant-turn""]')];
  for(let i=turns.length-1;i>=0;i--){
    const n=turns[i],tag=(n.getAttribute('data-testid')||'').toLowerCase();
    const role=n.getAttribute('data-message-author-role')||
      n.querySelector('[data-message-author-role]')?.getAttribute('data-message-author-role')||'';
    if(role.toLowerCase()==='user'||n.querySelector('[data-message-author-role=""user""]'))continue;
    const assistantLike=role.toLowerCase()==='assistant'||tag.includes('assistant')||
      n.querySelector('[data-testid*=""assistant""], [data-role=""assistant""]')!==null;
    if(!assistantLike)continue;
    const t=getText(n);
    if(t.length)return t.slice(0,200000);
  }
  return null;
}";
        foreach (var frame in page.Frames)
        {
            try
            {
                if (frame != page.MainFrame &&
                    !frame.Url.Contains("chatgpt.com", StringComparison.OrdinalIgnoreCase))
                    continue;
                var text = await frame.EvaluateAsync<string?>(script);
                if (!string.IsNullOrWhiteSpace(text)) return text;
            }
            catch (PlaywrightException) { }
        }
        return null;
    }

    private static async Task SaveChatGptResponseDiagnosticAsync(
        IPage page, string reason, string assistantText, Action<string>? log)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DiagnosticsDirectory);
            var stamp=DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var file=Path.Combine(AppPaths.DiagnosticsDirectory,
                "ChatGPT-JSON-"+reason+"-"+stamp+".txt");
            // Local-only evidence for an actual DOM detection failure.
            // Does not upload the browser page or private conversations.
            var summary=await page.EvaluateAsync<string>(@"
() => {
  const selectors=[
    'main','[data-message-author-role=""user""]',
    '[data-message-author-role=""assistant""]',
    '[data-testid^=""conversation-turn-""]',
    '[data-testid*=""assistant""]','main .markdown',
    'main pre','main code','main article',
    '[data-testid=""send-button""]',
    'button[aria-label*=""Stop""]'
  ];
  const lines=['Page title: '+document.title,
    'Document state: '+document.readyState,
    'URL path: '+location.pathname,
    'Visible main text length: '+(document.querySelector('main')?.innerText||'').length,
    'Focused element: '+(document.activeElement?.tagName||'(none)')];
  for(const selector of selectors) {
    try {
      const items=[...document.querySelectorAll(selector)];
      lines.push(selector+': '+items.length);
    }catch(e){lines.push(selector+': selector failed '+e.message)}
  }
  const turns=[...document.querySelectorAll(
    '[data-testid^=""conversation-turn-""], [data-message-author-role]')];
  lines.push('Last conversation nodes (role, testid, length, preview):');
  for(const e of turns.slice(-12)) {
    const txt=(e.innerText||e.textContent||'').trim();
    lines.push('role='+(e.getAttribute('data-message-author-role')||'?')+
      ' testid='+(e.getAttribute('data-testid')||'?')+
      ' length='+txt.length+' preview='+txt.slice(0,200).replace(/\s+/g,' '));
  }
  const main=document.querySelector('main');
  if(main) {
    const end=(main.innerText||'').slice(-1800);
    lines.push('Last 1800 visible main characters (may include prompt): '+end);
  }
  return lines.join('\n');
}");
            var detail="URL: "+page.Url+Environment.NewLine+
                "Assistant response length: "+assistantText.Length+Environment.NewLine+
                "Frames: "+string.Join(" | ",page.Frames.Select(fr=>fr.Url))+Environment.NewLine+
                "DOM snapshot:"+Environment.NewLine+summary+Environment.NewLine+
                "Captured assistant text (first 20000 chars):"+Environment.NewLine+
                assistantText[..Math.Min(assistantText.Length,20000)];
            await File.WriteAllTextAsync(file,detail);
            log?.Invoke("ChatGPT: local DOM diagnostic saved: "+file);
            try
            {
                var screenshot=Path.Combine(AppPaths.DiagnosticsDirectory,
                    "ChatGPT-JSON-"+reason+"-"+stamp+".png");
                await page.ScreenshotAsync(new(){Path=screenshot,FullPage=false,Timeout=10000});
                log?.Invoke("ChatGPT: diagnostic screenshot: "+screenshot);
            }
            catch (PlaywrightException ex)
            {
                log?.Invoke("ChatGPT: screenshot diagnostic unavailable: "+ex.Message);
            }
        }
        catch (Exception ex)
        {
            log?.Invoke("ChatGPT: could not save response diagnostic: "+ex.Message);
        }
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

    private static string ExtractJson(string raw)
    {
        var json = TryExtractLastValidJsonObject(raw);
        if (json is null) throw new InvalidDataException("ChatGPT did not return a valid JSON object.");
        return json;
    }

    private sealed record ImageCandidate(ILocator Locator, string Fingerprint, double Area);
    private sealed record ImageBaseline(HashSet<string> Fingerprints, int CandidateCount);
}
