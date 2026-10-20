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

        var howManyScenes = Math.Clamp(project.Options.MaxGeneratedScenes, 0, all.Count);
        var imagesPerScene = Math.Clamp(project.Options.ImagesPerScene, 1, 20);
        var safetyTotalImageCap = Math.Max(0, project.Options.MaxGeneratedVisuals);

        if (howManyScenes <= 0)
        {
            log?.Invoke("Visual pass: How Many Scenes = 0. No ChatGPT images will be generated.");
            return;
        }

        if (safetyTotalImageCap <= 0)
        {
            log?.Invoke("Visual pass: Safety Total Image Cap = 0. No ChatGPT images will be generated.");
            return;
        }

        // "How Many Scenes" means exactly this many deterministic timeline scenes are eligible.
        // They are spread across the full video so a small scene budget does not spend every
        // image in the opening chapter. The same project/settings always select the same scenes,
        // so Resume/Build continues those scenes instead of drifting into new ones.
        var selectedScenes = SelectEvenly(all, howManyScenes);
        var selectedKeys = selectedScenes
            .Select(x => $"{x.Chapter.Number}.{x.Scene.Number}")
            .ToList();

        var existingGeneratedImages = ProjectMedia.CountGeneratedImages(project);
        var totalBudgetRemaining = Math.Max(0, safetyTotalImageCap - existingGeneratedImages);

        // Build slots variant-first. With 5 scenes, 2 images/scene and a safety cap of 8,
        // ZeroVideo first gives all 5 selected scenes one image, then fills 3 second images.
        // This preserves the meaning of How Many Scenes whenever the safety cap permits it.
        var slots = new List<VisualSlot>();
        for (var variant = 1; variant <= imagesPerScene; variant++)
        {
            foreach (var (chapter, scene) in selectedScenes)
            {
                var generatedCount = ProjectMedia.FindGeneratedImages(project, chapter, scene).Count;
                if (generatedCount >= variant) continue;
                slots.Add(new VisualSlot(chapter, scene, variant));
            }
        }

        if (slots.Count > totalBudgetRemaining)
            slots = slots.Take(totalBudgetRemaining).ToList();

        log?.Invoke($"Visual settings: How Many Scenes = {howManyScenes}, Images Per Scene = {imagesPerScene}, Safety Total Image Cap = {safetyTotalImageCap}.");
        log?.Invoke($"Selected timeline scenes: {string.Join(", ", selectedKeys)}.");
        log?.Invoke($"Existing generated images: {existingGeneratedImages}. Remaining safety budget: {totalBudgetRemaining}.");

        if (slots.Count == 0)
        {
            if (totalBudgetRemaining <= 0)
                log?.Invoke($"Visual pass: Safety Total Image Cap reached ({existingGeneratedImages}/{safetyTotalImageCap}).");
            else
                log?.Invoke("Visual pass: all selected scenes already satisfy Images Per Scene.");
            return;
        }

        log?.Invoke($"Visual pass: generating {slots.Count} image(s) for the selected scene set.");
        log?.Invoke("Visual pass is resumable: successful images are checkpointed immediately and Resume/Build keeps the same selected scene set.");

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
                log?.Invoke($"Scene {slot.Chapter.Number}.{slot.Scene.Number} visual {slot.Variant}: left missing after retries. Resume/Build will retry this selected scene later.");
            }

            await store.SaveAsync(project, settings.ProjectsRoot);
            progress?.Report(25 + (processed / (double)Math.Max(1, slots.Count) * 40.0));

            if (consecutiveFailures >= 3)
            {
                log?.Invoke("Visual pass paused after 3 consecutive failed image slots. Completed images are preserved.");
                break;
            }
        }

        var generatedNow = ProjectMedia.CountGeneratedImages(project);
        if (failedSlots.Count > 0)
            log?.Invoke($"Visual pass completed with {failedSlots.Count} failed slot(s): {string.Join(", ", failedSlots)}.");

        var completedSelectedScenes = selectedScenes.Count(x => ProjectMedia.FindGeneratedImages(project, x.Chapter, x.Scene).Count > 0);
        log?.Invoke($"Visual pass total: {completedSelectedScenes}/{howManyScenes} selected scene(s) have generated visuals; {generatedNow}/{safetyTotalImageCap} generated image(s) total.");
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

    private static List<(ProjectChapter Chapter, ProjectScene Scene)> SelectEvenly(
        List<(ProjectChapter Chapter, ProjectScene Scene)> source,
        int count)
    {
        if (count >= source.Count) return source.ToList();
        if (count <= 0) return [];
        if (count == 1) return [source[0]];

        var selected = new List<(ProjectChapter Chapter, ProjectScene Scene)>(count);
        var used = new HashSet<int>();
        for (var i = 0; i < count; i++)
        {
            var index = (int)Math.Round(i * (source.Count - 1d) / (count - 1d));
            if (used.Add(index)) selected.Add(source[index]);
        }

        for (var i = 0; selected.Count < count && i < source.Count; i++)
            if (used.Add(i)) selected.Add(source[i]);

        return selected;
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
