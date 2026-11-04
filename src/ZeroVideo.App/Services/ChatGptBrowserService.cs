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

    /// <summary>
    /// Generate ONE isolated, clearly labeled AI-approximate portrait in the
    /// user's existing ChatGPT automation chat. Capture the actual original
    /// PNG via the same proven image saver used for scene images; no webpage
    /// screenshot, silent web photo scraping or fabricated celebrity photo.
    /// </summary>
    public async Task<string> GenerateIdentityReferenceAsync(
        VideoProject project, string personName, int variation,
        ZeroVideoSettings settings, Action<string>? log,
        CancellationToken ct, string? outputDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(personName) || personName.Length > 90 ||
            variation is < 1 or > 3)
            throw new ArgumentOutOfRangeException(nameof(personName));
        var profile = await profiles.LoadAsync(settings.AutomationProfilePath);
        var page = await GetProjectPageAsync(project, settings, log, ct);
        var baseline = await CaptureImageBaselineAsync(
            page, profile.ChatGpt.GeneratedImages);
        var styleRules = VisualStylePresets.GetPrompt(project.Style);
        var angle = variation % 2 == 1
            ? "straight-on frontal head-and-shoulders, neutral expression"
            : "natural three-quarter head-and-shoulders, neutral expression";
        var prompt = $"""
Create EXACTLY ONE brand-new SQUARE 1:1 portrait illustration.
SUBJECT: {personName}
PROJECT VISUAL STYLE: {project.Style}
STYLE RULES: {styleRules}
COMPOSITION: {angle}.

This is a local ZeroVideo AI-generated identity *guide*, NOT a verified
historical photograph or a claim that the face exactly matches the subject.
Depict a respectful, recognizable stylized likeness of the named real adult
public figure using your available visual knowledge. Do not invent props or
historical facts. If likeness is uncertain, preserve the broad defining
features without pretending you verified a real photograph.

IMPORTANT: isolate a SINGLE face, hair and upper shoulders. Center them
with adequate headroom, high-detail eyes, face, hairline and facial hair.
Use a plain mid-gray backdrop with no scenery, no full-body action, no
dramatic colored rim lights, no text, no logos, no captions, no composites.
One portrait only, not two poses in the same frame, not a screenshot.
The portrait must retain the selected comic/digital-illustration style.
This is a REFERENCE ONLY and must not copy a film still or specific
copyrighted photograph.
""";
        await ChatGptComposer.EnterPromptAsync(
            page, profile.ChatGpt.PromptInputs, prompt, ct);
        await ChatGptComposer.SendAsync(
            page, profile.ChatGpt.SendButtons, ct);
        log?.Invoke($"ChatGPT: generating {personName} approximate identity guide "+
            $"{variation} ({project.Style}); waiting for original image...");
        var folder = outputDirectory ??
            RealPersonIdentityService.ReferenceFolder(project);
        Directory.CreateDirectory(folder);
        var slug = System.Text.RegularExpressions.Regex.Replace(
            personName.Trim(), "[^A-Za-z0-9_-]", "_");
        var destination = Path.Combine(folder,
            "AI-APPROXIMATE-" + slug + "-v" + variation + "-" +
            Guid.NewGuid().ToString("N")[..8] + ".png");
        try
        {
            await WaitForGeneratedImageAndSaveAsync(
                page, profile.ChatGpt.GeneratedImages,
                profile.ChatGpt.StopButtons, baseline, destination,
                $"Identity reference {personName} #{variation}", log, ct);
            RememberConversation(project,page);
            log?.Invoke("ChatGPT: saved actual full-resolution generated "+
                "portrait: "+destination);
            return destination;
        }
        catch
        {
            // A failed/partial image must never be added to a profile.
            try { if (File.Exists(destination)) File.Delete(destination); }
            catch (IOException) { }
            throw;
        }
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

        // Record the assistant output BEFORE sending, so old chapter JSON
        // cannot be mistaken for the result of this request.
        var baselineCandidates = await ReadAssistantCandidatesAsync(page);
        var baselineCount = baselineCandidates.Length;
        var baseline = baselineCandidates.Select(ChatGptResponseRecovery.ExtractLastValidJsonObject)
            .LastOrDefault(value => value is not null) ?? "";
        var baselineAssistant = baselineCandidates.LastOrDefault() ?? "";
        await ChatGptComposer.SendAsync(page, profile.ChatGpt.SendButtons, ct);
        log?.Invoke("ChatGPT: prompt sent; waiting for the finished response...");

        var response = await WaitForCompletedResponseAsync(
            page,
            profile.ChatGpt.AssistantMessages,
            profile.ChatGpt.StopButtons,
            baselineCount,
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
        int baselineCount,
        string baseline,
        string baselineAssistant,
        Action<string>? log,
        CancellationToken ct)
    {
        string lastJson = "", lastRaw = "";
        int stableJson = 0, stableRaw = 0;
        var started = DateTime.UtcNow;

        while (!ct.IsCancellationRequested && DateTime.UtcNow - started < TimeSpan.FromMinutes(12))
        {
            ct.ThrowIfCancellationRequested();
            var generating = await AnyVisibleAsync(page, stopSelectors);
            var candidates = await ReadAssistantCandidatesAsync(page);
            var freshJson = ChatGptResponseRecovery.FindFreshJson(
                candidates, baselineCount, baselineAssistant, baseline);

            if (freshJson is not null)
            {
                stableJson = string.Equals(freshJson, lastJson, StringComparison.Ordinal)
                    ? stableJson + 1 : 0;
                lastJson = freshJson;
                if (!generating && stableJson >= 2)
                {
                    log?.Invoke("ChatGPT: newest assistant JSON detected and stable.");
                    return freshJson;
                }
            }
            else { lastJson = ""; stableJson = 0; }

            // If ChatGPT produced non-JSON prose, hand that NEW response to
            // existing validation + retry, not an earlier completed chapter.
            if (!generating && DateTime.UtcNow - started > TimeSpan.FromSeconds(30))
            {
                var freshText = candidates.Length > baselineCount
                    ? string.Join("\n", candidates.Skip(baselineCount))
                    : candidates.Length > 0 &&
                      !string.Equals(candidates[^1], baselineAssistant, StringComparison.Ordinal)
                        ? candidates[^1] : null;

                if (!string.IsNullOrWhiteSpace(freshText))
                {
                    stableRaw = string.Equals(freshText, lastRaw, StringComparison.Ordinal)
                        ? stableRaw + 1 : 0;
                    lastRaw = freshText;
                    if (stableRaw >= 10)
                    {
                        var json = ChatGptResponseRecovery.ExtractLastValidJsonObject(freshText);
                        if (json is not null &&
                            (candidates.Length > baselineCount || json != baseline))
                        {
                            log?.Invoke("ChatGPT: completed answer recovered from fallback text.");
                            return json;
                        }

                        await SaveChatGptResponseDiagnosticAsync(page,
                            "Completed-NonJson", freshText, log);
                        log?.Invoke("ChatGPT: response is not valid JSON; requesting repair.");
                        return freshText;
                    }
                }
            }

            await Task.Delay(1200, ct);
        }

        ct.ThrowIfCancellationRequested();
        var finalCandidates = await ReadAssistantCandidatesAsync(page);
        var recovered = ChatGptResponseRecovery.FindFreshJson(
            finalCandidates, baselineCount, baselineAssistant, baseline);
        if (recovered is not null)
        {
            log?.Invoke("ChatGPT: finished JSON recovered on final check.");
            return recovered;
        }

        await SaveChatGptResponseDiagnosticAsync(page,
            "Json-NotFound", finalCandidates.LastOrDefault() ?? "", log);
        throw new TimeoutException(
            "ZeroVideo could not locate a NEW completed ChatGPT assistant reply. " +
            "Diagnostics were saved in %LOCALAPPDATA%\\ZeroVideo\\diagnostics. " +
            "Your saved conversation and completed video work are preserved; " +
            "retry with Resume Unfinished once the browser is ready.");
    }

    // The ChatGPT page can have new assistant markdown without stable role
    // attributes. Read all visible output blocks in DOM order, excluding
    // user turns. Never favor an older JSON just because it matches a selector.
    private static async Task<string[]> ReadAssistantCandidatesAsync(IPage page)
    {
        const string script = """
            () => {
              const main=document.querySelector('main') ||
                document.querySelector('[role="main"]');
              if(!main)return [];
              const assistant='[data-message-author-role="assistant"],' +
                '[data-testid="conversation-turn-assistant"],' +
                '[data-testid*="assistant-response"],[data-role="assistant"]';
              const blocks='.markdown,[class*="markdown"],pre';
              const user=n=>!!n.closest(
                '[data-message-author-role="user"],[data-role="user"],' +
                '[data-testid*="conversation-turn-user"]');
              const visible=n=>{const st=getComputedStyle(n);
                return st.display!=='none' && st.visibility!=='hidden';};
              const roots=[...main.querySelectorAll(assistant)].filter(n=>!user(n));
              const records=[],seen=new Set();
              const add=n=>{
                if(seen.has(n)||user(n)||!visible(n))return;
                const text=(n.innerText||n.textContent||'').trim();
                if(!text)return; seen.add(n);
                records.push({n,text:text.slice(0,500000)});
              };
              roots.forEach(n=>{
                if(!roots.some(p=>p!==n&&p.contains(n)))add(n);
              });
              for(const n of main.querySelectorAll(blocks)){
                if(roots.some(p=>p.contains(n)))continue;
                if(n.matches('pre')&&n.closest('.markdown,[class*="markdown"]'))continue;
                if(n.matches('.markdown,[class*="markdown"]') &&
                   n.parentElement?.closest('.markdown,[class*="markdown"]'))continue;
                add(n);
              }
              for(const n of main.querySelectorAll('[data-testid^="conversation-turn-"]')){
                if(user(n)||roots.some(p=>p.contains(n)))continue;
                const id=(n.getAttribute('data-testid')||'').toLowerCase();
                if(id.includes('assistant')||n.querySelector(blocks))add(n);
              }
              records.sort((a,b)=>{
                const pos=a.n.compareDocumentPosition(b.n);
                return pos&Node.DOCUMENT_POSITION_FOLLOWING?-1:
                  pos&Node.DOCUMENT_POSITION_PRECEDING?1:0;
              });
              // Deduplicate outer assistant turn + nested content nodes.
              return records.filter((e,i)=>
                !records.some((p,j)=>i!==j&&p.n.contains(e.n))
              ).map(e=>e.text);
            }
            """;
        foreach (var frame in page.Frames)
        {
            try
            {
                if (frame != page.MainFrame &&
                    !frame.Url.Contains("chatgpt.com", StringComparison.OrdinalIgnoreCase))
                    continue;
                var values = await frame.EvaluateAsync<string[]?>(script);
                if (values is { Length: > 0 }) return values;
            }
            catch (PlaywrightException) { /* retry after DOM rerender */ }
        }
        return [];
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
