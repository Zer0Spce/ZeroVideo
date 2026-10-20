using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public sealed class PipelineService(ChatGptBrowserService chat, ClipchampTtsService clipchamp, FfmpegService ffmpeg, ProjectStore store)
{
    public async Task<string> RunAsync(VideoProject project, ZeroVideoSettings settings, Action<string>? log = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (project.Chapters.Count == 0) throw new InvalidOperationException("Generate a project plan first.");
        await store.SaveAsync(project, settings.ProjectsRoot);

        if (project.Options.GenerateMissingVisuals)
            await GenerateMissingVisualsAsync(project, settings, log, progress, ct);

        var total = project.Chapters.Count + 1.0;
        for (var i = 0; i < project.Chapters.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var chapter = project.Chapters[i];
            AdoptExistingAudio(project, chapter);
            if (string.IsNullOrWhiteSpace(chapter.AudioPath) || !File.Exists(chapter.AudioPath))
            {
                log?.Invoke($"Generating TTS chapter {chapter.Number}/{project.Chapters.Count}: {chapter.Title}");
                await clipchamp.GenerateChapterAsync(chapter, project, settings, log, ct);
                await store.SaveAsync(project, settings.ProjectsRoot);
            }
            else log?.Invoke($"Chapter {chapter.Number}: using existing audio.");
            progress?.Report(70 + ((i + 1) / total * 20.0));
        }

        log?.Invoke("Rendering final video with FFmpeg...");
        var output = await ffmpeg.RenderAsync(project, settings, log, ct);
        progress?.Report(100);
        await store.SaveAsync(project, settings.ProjectsRoot);
        return output;
    }

    private async Task GenerateMissingVisualsAsync(VideoProject project, ZeroVideoSettings settings, Action<string>? log, IProgress<double>? progress, CancellationToken ct)
    {
        var all = project.Chapters.SelectMany(c => c.Scenes.Select(s => (Chapter: c, Scene: s))).ToList();

        // Older ZeroVideo builds captured the rendered ChatGPT page area instead of the
        // underlying generated image asset. Projects without the current asset version are
        // migrated once by discarding only ZeroVideo-generated stills. Narration, TTS and
        // user-supplied media remain untouched.
        if (project.VisualAssetVersion.GetValueOrDefault() < ProjectMedia.CurrentGeneratedAssetVersion)
        {
            var legacyCount = 0;
            foreach (var (chapter, scene) in all)
            {
                if (ProjectMedia.FindGeneratedImages(project, chapter, scene).Count == 0) continue;
                ProjectMedia.DeleteGeneratedImageVariants(project, chapter, scene);
                legacyCount++;
            }

            project.VisualAssetVersion = ProjectMedia.CurrentGeneratedAssetVersion;
            await store.SaveAsync(project, settings.ProjectsRoot);
            if (legacyCount > 0)
                log?.Invoke($"Visual quality upgrade: removed {legacyCount} legacy screenshot-based scene visual(s). They will be regenerated at full resolution.");
            else
                log?.Invoke("Visual quality upgrade enabled: generated visuals use full-resolution ChatGPT assets, not webpage screenshots.");
        }

        var maxGeneratedScenes = Math.Clamp(project.Options.MaxGeneratedScenes, 0, all.Count);
        var imagesPerScene = Math.Clamp(project.Options.ImagesPerScene, 1, 20);
        var maxTotalImages = Math.Max(0, project.Options.MaxGeneratedVisuals);
        var existingGeneratedImages = ProjectMedia.CountGeneratedImages(project);

        if (maxGeneratedScenes <= 0)
        {
            log?.Invoke("Visual pass: Max scenes with generated visuals is 0. No ChatGPT images will be generated.");
            return;
        }

        if (maxTotalImages <= 0)
        {
            log?.Invoke("Visual pass: Maximum total images is 0. No ChatGPT images will be generated.");
            return;
        }

        var scenesAlreadyGenerated = all
            .Where(x => ProjectMedia.FindGeneratedImages(project, x.Chapter, x.Scene).Count > 0)
            .ToList();

        // Keep already-generated scenes stable across Resume/Build runs. Only the first
        // MaxGeneratedScenes generated scenes are eligible for additional variants when a
        // user lowers the scene limit on an existing project.
        var selectedScenes = new List<(ProjectChapter Chapter, ProjectScene Scene)>();
        foreach (var item in scenesAlreadyGenerated.Take(maxGeneratedScenes))
        {
            if (ProjectMedia.FindAll(project, item.Chapter, item.Scene).Count < imagesPerScene)
                selectedScenes.Add(item);
        }

        var generatedSceneCount = scenesAlreadyGenerated.Count;
        var remainingSceneBudget = Math.Max(0, maxGeneratedScenes - generatedSceneCount);

        if (remainingSceneBudget > 0)
        {
            foreach (var item in all)
            {
                if (remainingSceneBudget <= 0) break;
                if (ProjectMedia.FindGeneratedImages(project, item.Chapter, item.Scene).Count > 0) continue;
                if (ProjectMedia.FindAll(project, item.Chapter, item.Scene).Count >= imagesPerScene) continue;

                selectedScenes.Add(item);
                remainingSceneBudget--;
            }
        }

        var slots = new List<VisualSlot>();
        foreach (var (chapter, scene) in selectedScenes)
        {
            var existingMediaCount = ProjectMedia.FindAll(project, chapter, scene).Count;
            var generatedCount = ProjectMedia.FindGeneratedImages(project, chapter, scene).Count;
            var needed = Math.Max(0, imagesPerScene - existingMediaCount);
            for (var i = 0; i < needed; i++)
                slots.Add(new VisualSlot(chapter, scene, generatedCount + i + 1));
        }

        var totalBudgetRemaining = Math.Max(0, maxTotalImages - existingGeneratedImages);
        if (slots.Count > totalBudgetRemaining)
            slots = slots.Take(totalBudgetRemaining).ToList();

        log?.Invoke($"Visual settings: up to {maxGeneratedScenes} generated scene(s), {imagesPerScene} image(s) per selected scene, {maxTotalImages} maximum total image(s).");
        log?.Invoke($"Visual pass: {generatedSceneCount} scene(s) already contain generated visuals and {existingGeneratedImages} generated image(s) already exist.");

        if (slots.Count == 0)
        {
            if (generatedSceneCount >= maxGeneratedScenes)
                log?.Invoke($"Visual pass: generated-scene limit reached ({generatedSceneCount}/{maxGeneratedScenes}). Remaining scenes will reuse nearby media during rendering instead of showing black placeholders.");
            else if (totalBudgetRemaining <= 0)
                log?.Invoke($"Visual pass: total image cap reached ({existingGeneratedImages}/{maxTotalImages}). Remaining scenes will reuse nearby media during rendering.");
            else
                log?.Invoke("Visual pass: selected scenes already satisfy the requested image count.");
            return;
        }

        log?.Invoke($"Visual pass: generating {slots.Count} image(s) across {slots.Select(x => (x.Chapter.Number, x.Scene.Number)).Distinct().Count()} selected scene(s).");
        log?.Invoke("Visual pass is resumable: every successful full-resolution image is checkpointed immediately.");

        var failedSlots = new List<string>();
        var consecutiveFailures = 0;
        var processed = 0;

        foreach (var slot in slots)
        {
            ct.ThrowIfCancellationRequested();

            var succeeded = await TryGenerateSceneVisualAsync(project, slot.Chapter, slot.Scene, slot.Variant, imagesPerScene, settings, log, ct);
            processed++;

            if (succeeded)
            {
                consecutiveFailures = 0;
            }
            else
            {
                consecutiveFailures++;
                failedSlots.Add($"{slot.Chapter.Number}.{slot.Scene.Number}/v{slot.Variant:00}");
                log?.Invoke($"Scene {slot.Chapter.Number}.{slot.Scene.Number} visual {slot.Variant}: left missing after retries. It can be generated on the next Build run.");
            }

            await store.SaveAsync(project, settings.ProjectsRoot);
            progress?.Report(25 + (processed / (double)Math.Max(1, slots.Count) * 40.0));

            if (consecutiveFailures >= 3)
            {
                log?.Invoke("Visual pass paused after 3 consecutive failed image slots. Completed images are preserved; rerun Build later to resume.");
                break;
            }
        }

        var generatedNow = ProjectMedia.CountGeneratedImages(project);
        var generatedScenesNow = all.Count(x => ProjectMedia.FindGeneratedImages(project, x.Chapter, x.Scene).Count > 0);
        if (failedSlots.Count > 0)
            log?.Invoke($"Visual pass completed with {failedSlots.Count} failed slot(s) this run: {string.Join(", ", failedSlots)}.");

        log?.Invoke($"Visual pass total: {generatedScenesNow}/{maxGeneratedScenes} generated scene(s), {generatedNow}/{maxTotalImages} generated image(s).");
    }

    private async Task<bool> TryGenerateSceneVisualAsync(
        VideoProject project,
        ProjectChapter chapter,
        ProjectScene scene,
        int variant,
        int imagesPerScene,
        ZeroVideoSettings settings,
        Action<string>? log,
        CancellationToken ct)
    {
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attemptCts.CancelAfter(TimeSpan.FromMinutes(3));

            try
            {
                if (attempt > 1)
                    log?.Invoke($"Scene {chapter.Number}.{scene.Number} visual {variant}: retry {attempt}/{maxAttempts}...");

                PrepareGeneratedVariant(project, chapter, scene, variant, imagesPerScene);
                var rawPath = await chat.GenerateSceneImageAsync(project, chapter, scene, settings, log, attemptCts.Token);
                var storedPath = FinalizeGeneratedVariant(project, chapter, scene, rawPath, variant, imagesPerScene);
                log?.Invoke($"Scene {chapter.Number}.{scene.Number}: saved visual {variant}/{imagesPerScene} -> {Path.GetFileName(storedPath)}");
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attemptCts.IsCancellationRequested)
            {
                log?.Invoke($"Scene {chapter.Number}.{scene.Number} visual {variant}: ChatGPT image attempt {attempt}/{maxAttempts} timed out after 3 minutes.");
                await store.SaveAsync(project, settings.ProjectsRoot);
                if (attempt >= maxAttempts) return false;
                await Task.Delay(TimeSpan.FromSeconds(4 * attempt), ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                log?.Invoke($"Scene {chapter.Number}.{scene.Number} visual {variant}: attempt {attempt}/{maxAttempts} failed — {ex.Message}");
                await store.SaveAsync(project, settings.ProjectsRoot);
                if (attempt >= maxAttempts) return false;
                await Task.Delay(TimeSpan.FromSeconds(4 * attempt), ct);
            }
        }

        return false;
    }

    private static void PrepareGeneratedVariant(VideoProject project, ProjectChapter chapter, ProjectScene scene, int variant, int imagesPerScene)
    {
        if (imagesPerScene <= 1 || variant <= 1) return;

        var basePath = ProjectMedia.GeneratedImagePath(project, chapter, scene, 1);
        if (!File.Exists(basePath)) return;

        var firstVariant = Path.Combine(
            Path.GetDirectoryName(basePath)!,
            Path.GetFileNameWithoutExtension(basePath) + "-v01" + Path.GetExtension(basePath));

        if (!File.Exists(firstVariant)) File.Move(basePath, firstVariant, true);
        else File.Delete(basePath);

        if (string.Equals(scene.MediaPath, basePath, StringComparison.OrdinalIgnoreCase)) scene.MediaPath = firstVariant;
        scene.MediaPaths.RemoveAll(p => PathsEqual(p, basePath));
        if (!scene.MediaPaths.Any(p => PathsEqual(p, firstVariant))) scene.MediaPaths.Add(firstVariant);
    }

    private static string FinalizeGeneratedVariant(VideoProject project, ProjectChapter chapter, ProjectScene scene, string rawPath, int variant, int imagesPerScene)
    {
        var finalPath = rawPath;
        if (imagesPerScene > 1)
        {
            finalPath = Path.Combine(
                Path.GetDirectoryName(rawPath)!,
                $"c{chapter.Number:00}-s{scene.Number:000}-v{variant:00}{Path.GetExtension(rawPath)}");
            if (!string.Equals(rawPath, finalPath, StringComparison.OrdinalIgnoreCase))
                File.Move(rawPath, finalPath, true);
        }

        if (string.IsNullOrWhiteSpace(scene.MediaPath) || !File.Exists(scene.MediaPath)) scene.MediaPath = finalPath;
        if (!scene.MediaPaths.Any(p => PathsEqual(p, finalPath))) scene.MediaPaths.Add(finalPath);

        return finalPath;
    }

    private static bool PathsEqual(string left, string right)
    {
        try { return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase); }
        catch { return string.Equals(left, right, StringComparison.OrdinalIgnoreCase); }
    }

    private static void AdoptExistingAudio(VideoProject project, ProjectChapter chapter)
    {
        if (!string.IsNullOrWhiteSpace(chapter.AudioPath) && File.Exists(chapter.AudioPath)) return;
        var candidate = Path.Combine(project.ProjectDirectory, "audio", $"chapter-{chapter.Number:00}.m4a");
        if (File.Exists(candidate)) chapter.AudioPath = candidate;
    }

    private sealed record VisualSlot(ProjectChapter Chapter, ProjectScene Scene, int Variant);
}
