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
        // underlying generated image asset. That produced soft visuals and could include
        // ChatGPT controls such as Edit/download/Ask ChatGPT. Projects without the current
        // asset version are migrated once by discarding only ZeroVideo's generated scene
        // image files; narration, TTS, project state, and user-supplied non-generated media
        // remain untouched.
        if (project.VisualAssetVersion.GetValueOrDefault() < ProjectMedia.CurrentGeneratedAssetVersion)
        {
            var legacyCount = 0;
            foreach (var (chapter, scene) in all)
            {
                var generatedPath = ProjectMedia.GeneratedImagePath(project, chapter, scene);
                var stem = Path.Combine(Path.GetDirectoryName(generatedPath)!, Path.GetFileNameWithoutExtension(generatedPath));
                var hasLegacy = new[] { ".png", ".jpg", ".jpeg", ".webp", ".avif" }.Any(ext => File.Exists(stem + ext));
                if (!hasLegacy) continue;
                ProjectMedia.DeleteGeneratedImageVariants(project, chapter, scene);
                legacyCount++;
            }

            project.VisualAssetVersion = ProjectMedia.CurrentGeneratedAssetVersion;
            await store.SaveAsync(project, settings.ProjectsRoot);
            if (legacyCount > 0)
                log?.Invoke($"Visual quality upgrade: removed {legacyCount} legacy screenshot-based scene visual(s). They will be regenerated from the original ChatGPT image assets at full resolution.");
            else
                log?.Invoke("Visual quality upgrade enabled: new generated visuals will use full-resolution ChatGPT assets, not webpage screenshots.");
        }

        var missing = all.Where(x => ProjectMedia.Find(project, x.Chapter, x.Scene) is null)
            .Take(Math.Max(0, project.Options.MaxGeneratedVisuals))
            .ToList();

        if (missing.Count == 0)
        {
            log?.Invoke("Visual pass: all scene media already exists.");
            return;
        }

        log?.Invoke($"Visual pass: generating {missing.Count} missing scene image(s) with ChatGPT.");
        log?.Invoke("Visual pass is resumable: every successful full-resolution image is checkpointed immediately.");

        var failedScenes = new List<string>();
        var consecutiveFailures = 0;
        var processed = 0;

        for (var i = 0; i < missing.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (chapter, scene) = missing[i];
            var existing = ProjectMedia.Find(project, chapter, scene);
            if (existing is not null)
            {
                scene.MediaPath = existing;
                consecutiveFailures = 0;
                processed++;
                continue;
            }

            var succeeded = await TryGenerateSceneVisualAsync(project, chapter, scene, settings, log, ct);
            processed++;

            if (succeeded)
            {
                consecutiveFailures = 0;
            }
            else
            {
                consecutiveFailures++;
                failedScenes.Add($"{chapter.Number}.{scene.Number}");
                log?.Invoke($"Scene {chapter.Number}.{scene.Number}: left missing after retries. It can be generated on the next Resume/Build run.");
            }

            await store.SaveAsync(project, settings.ProjectsRoot);
            progress?.Report(25 + (processed / (double)missing.Count * 40.0));

            if (consecutiveFailures >= 3)
            {
                log?.Invoke("Visual pass paused after 3 consecutive failed scenes. Completed images are preserved; rerun Build later to resume missing visuals.");
                break;
            }
        }

        var remaining = all.Count(x => ProjectMedia.Find(project, x.Chapter, x.Scene) is null);
        if (failedScenes.Count > 0)
            log?.Invoke($"Visual pass completed with {failedScenes.Count} scene failure(s) this run: {string.Join(", ", failedScenes)}.");

        if (remaining > 0)
            log?.Invoke($"Visual pass: {remaining} scene(s) still have no media. ZeroVideo will preserve the project and can resume them on the next Build run.");
        else
            log?.Invoke("Visual pass: every scene now has full-quality media.");
    }

    private async Task<bool> TryGenerateSceneVisualAsync(
        VideoProject project,
        ProjectChapter chapter,
        ProjectScene scene,
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
                    log?.Invoke($"Scene {chapter.Number}.{scene.Number}: retry {attempt}/{maxAttempts}...");

                await chat.GenerateSceneImageAsync(project, chapter, scene, settings, log, attemptCts.Token);
                return true;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && attemptCts.IsCancellationRequested)
            {
                log?.Invoke($"Scene {chapter.Number}.{scene.Number}: ChatGPT image attempt {attempt}/{maxAttempts} timed out after 3 minutes; retrying without losing project progress.");
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
                log?.Invoke($"Scene {chapter.Number}.{scene.Number}: image attempt {attempt}/{maxAttempts} failed — {ex.Message}");
                await store.SaveAsync(project, settings.ProjectsRoot);

                if (attempt >= maxAttempts) return false;
                await Task.Delay(TimeSpan.FromSeconds(4 * attempt), ct);
            }
        }

        return false;
    }

    private static void AdoptExistingAudio(VideoProject project, ProjectChapter chapter)
    {
        if (!string.IsNullOrWhiteSpace(chapter.AudioPath) && File.Exists(chapter.AudioPath)) return;
        var candidate = Path.Combine(project.ProjectDirectory, "audio", $"chapter-{chapter.Number:00}.m4a");
        if (File.Exists(candidate)) chapter.AudioPath = candidate;
    }
}
