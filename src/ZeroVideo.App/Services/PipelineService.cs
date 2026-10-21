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

        if (project.Options.VisualQa)
            RunVisualQa(project, log);

        var total = project.Chapters.Count + 1.0;
        for (var i = 0; i < project.Chapters.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var chapter = project.Chapters[i];
            AdoptExistingAudio(project, chapter);

            if (!string.IsNullOrWhiteSpace(chapter.AudioPath) && File.Exists(chapter.AudioPath))
            {
                try
                {
                    await ffmpeg.ValidateNarrationAudioAsync(chapter.AudioPath, settings, $"Chapter {chapter.Number} existing audio", chapter.Narration, log, ct);
                }
                catch (Exception ex)
                {
                    log?.Invoke($"Chapter {chapter.Number}: existing audio is invalid/silent and will be regenerated — {ex.Message}");
                    try { File.Delete(chapter.AudioPath); } catch { }
                    chapter.AudioPath = "";
                    await store.SaveAsync(project, settings.ProjectsRoot);
                }
            }

            if (string.IsNullOrWhiteSpace(chapter.AudioPath) || !File.Exists(chapter.AudioPath))
            {
                log?.Invoke($"Generating TTS chapter {chapter.Number}/{project.Chapters.Count}: {chapter.Title}");
                Exception? lastTtsError = null;
                for (var ttsAttempt = 1; ttsAttempt <= 2; ttsAttempt++)
                {
                    try
                    {
                        await clipchamp.GenerateChapterAsync(chapter, project, settings, log, ct);
                        await ffmpeg.ValidateNarrationAudioAsync(chapter.AudioPath, settings, $"Chapter {chapter.Number} generated TTS", chapter.Narration, log, ct);
                        lastTtsError = null;
                        break;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        lastTtsError = ex;
                        log?.Invoke($"Chapter {chapter.Number}: TTS attempt {ttsAttempt}/2 was invalid — {ex.Message}");
                        if (!string.IsNullOrWhiteSpace(chapter.AudioPath))
                        {
                            try { if (File.Exists(chapter.AudioPath)) File.Delete(chapter.AudioPath); } catch { }
                            chapter.AudioPath = "";
                        }
                        await store.SaveAsync(project, settings.ProjectsRoot);
                        if (ttsAttempt < 2) await Task.Delay(2500, ct);
                    }
                }
                if (lastTtsError is not null) throw new InvalidOperationException($"Chapter {chapter.Number} TTS failed twice and was not usable.", lastTtsError);
                await store.SaveAsync(project, settings.ProjectsRoot);
            }
            else log?.Invoke($"Chapter {chapter.Number}: using validated existing audio.");
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

        var howManyChapters = Math.Clamp(project.Options.HowManyChapters, 1, Math.Max(1, project.Chapters.Count));
        var imagesPerChapter = Math.Clamp(project.Options.ImagesPerChapter, 1, 50);
        var safetyCap = Math.Max(0, project.Options.MaxGeneratedVisuals);
        var selectedChapters = project.Chapters.Take(howManyChapters).ToList();

        if (safetyCap <= 0)
        {
            log?.Invoke("Visual pass: Safety Total Image Cap = 0. No ChatGPT images will be generated.");
            return;
        }

        // Generate chapter by chapter so Images Per Chapter behaves literally in the log and
        // in the visual pass: 1.1, 1.2, 1.3... then 2.1, 2.2, 2.3..., etc.
        // Every scripted scene is one narration beat + one target image.
        var desiredSlots = new List<(ProjectChapter Chapter, ProjectScene Scene)>();
        foreach (var chapter in selectedChapters)
        {
            foreach (var scene in chapter.Scenes.Take(imagesPerChapter))
                desiredSlots.Add((chapter, scene));
        }

        var existingGenerated = ProjectMedia.CountGeneratedImages(project);
        var remainingBudget = Math.Max(0, safetyCap - existingGenerated);
        var missingSlots = desiredSlots
            .Where(x => ProjectMedia.FindGeneratedImages(project, x.Chapter, x.Scene).Count == 0)
            .Take(remainingBudget)
            .ToList();

        log?.Invoke($"Visual settings: How Many Chapters = {howManyChapters}, Images Per Chapter = {imagesPerChapter}, Safety Total Image Cap = {safetyCap}.");
        log?.Invoke($"Script target: {selectedChapters.Count} chapter(s) × {imagesPerChapter} narration/image scene(s) = {selectedChapters.Count * imagesPerChapter} target visual beat(s).");
        log?.Invoke($"Existing generated images: {existingGenerated}. Remaining safety budget: {remainingBudget}.");

        foreach (var chapter in selectedChapters.Where(c => c.Scenes.Count != imagesPerChapter))
            log?.Invoke($"Chapter {chapter.Number}: script currently has {chapter.Scenes.Count} scene(s), but Images Per Chapter is {imagesPerChapter}. Use Resume writing to regenerate this chapter to the requested structure.");

        if (missingSlots.Count == 0)
        {
            if (remainingBudget <= 0)
                log?.Invoke($"Visual pass: Safety Total Image Cap reached ({existingGenerated}/{safetyCap}). Missing scripted scenes will reuse nearby real visuals during rendering.");
            else
                log?.Invoke("Visual pass: all eligible scripted scenes already have generated images.");
            return;
        }

        log?.Invoke($"Visual pass: generating {missingSlots.Count} scene image(s) chapter by chapter. One generated image targets one scripted narration scene.");
        log?.Invoke("Visual pass is resumable: every successful full-resolution image is checkpointed immediately.");

        var consecutiveFailures = 0;
        var failed = new List<string>();
        var processed = 0;

        foreach (var (chapter, scene) in missingSlots)
        {
            ct.ThrowIfCancellationRequested();
            var succeeded = await TryGenerateSceneVisualAsync(project, chapter, scene, settings, log, ct);
            processed++;

            if (succeeded)
            {
                consecutiveFailures = 0;
            }
            else
            {
                consecutiveFailures++;
                failed.Add($"{chapter.Number}.{scene.Number}");
                log?.Invoke($"Scene {chapter.Number}.{scene.Number}: left missing after retries. Resume/Build will retry it later.");
            }

            await store.SaveAsync(project, settings.ProjectsRoot);
            progress?.Report(25 + (processed / (double)Math.Max(1, missingSlots.Count) * 40.0));

            if (consecutiveFailures >= 3)
            {
                log?.Invoke("Visual pass paused after 3 consecutive failed scenes. Completed images are preserved.");
                break;
            }
        }

        if (failed.Count > 0)
            log?.Invoke($"Visual pass completed with {failed.Count} failed scene(s): {string.Join(", ", failed)}.");

        var generatedNow = ProjectMedia.CountGeneratedImages(project);
        log?.Invoke($"Visual pass total: {generatedNow}/{safetyCap} generated image(s). Voiceover remains aligned to the scripted scene order.");
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
                log?.Invoke($"Scene {chapter.Number}.{scene.Number}: ChatGPT image attempt {attempt}/{maxAttempts} timed out after 3 minutes.");
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

    private static void RunVisualQa(VideoProject project, Action<string>? log)
    {
        var allScenes = project.Chapters.SelectMany(c => c.Scenes.Select(s => (Chapter: c, Scene: s))).ToList();
        var missing = 0;
        var invalid = 0;
        var duplicateContent = 0;
        var hashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (chapter, scene) in allScenes)
        {
            var media = ProjectMedia.FindAll(project, chapter, scene).Where(File.Exists).ToList();
            if (media.Count == 0) { missing++; continue; }
            foreach (var path in media)
            {
                try
                {
                    var info = new FileInfo(path);
                    if (info.Length < 2048) { invalid++; continue; }
                    if (!IsImageForQa(path)) continue;
                    using var fs = File.OpenRead(path);
                    var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(fs));
                    if (!hashes.Add(hash)) duplicateContent++;
                }
                catch { invalid++; }
            }
        }

        log?.Invoke($"Visual QA: {allScenes.Count - missing}/{allScenes.Count} scene(s) have direct media; {missing} missing, {invalid} invalid/tiny file(s), {duplicateContent} duplicate-content visual(s).");
        if (missing > 0) log?.Invoke("Visual QA note: missing scenes will still use ZeroVideo's nearest-real-visual fallback during rendering.");
    }

    private static bool IsImageForQa(string path)
        => new[] { ".png", ".jpg", ".jpeg", ".webp", ".bmp" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static void AdoptExistingAudio(VideoProject project, ProjectChapter chapter)
    {
        if (!string.IsNullOrWhiteSpace(chapter.AudioPath) && File.Exists(chapter.AudioPath)) return;
        var candidate = Path.Combine(project.ProjectDirectory, "audio", $"chapter-{chapter.Number:00}.m4a");
        if (File.Exists(candidate)) chapter.AudioPath = candidate;
    }
}
