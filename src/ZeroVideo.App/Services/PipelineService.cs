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
        var missing = all.Where(x => ProjectMedia.Find(project, x.Chapter, x.Scene) is null).Take(Math.Max(0, project.Options.MaxGeneratedVisuals)).ToList();
        if (missing.Count == 0) { log?.Invoke("Visual pass: all scene media already exists."); return; }
        log?.Invoke($"Visual pass: generating {missing.Count} missing scene image(s) with ChatGPT.");

        for (var i = 0; i < missing.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (chapter, scene) = missing[i];
            var existing = ProjectMedia.Find(project, chapter, scene);
            if (existing is not null) { scene.MediaPath = existing; continue; }
            await chat.GenerateSceneImageAsync(project, chapter, scene, settings, log, ct);
            await store.SaveAsync(project, settings.ProjectsRoot);
            progress?.Report(25 + ((i + 1) / (double)missing.Count * 40.0));
        }

        var remaining = all.Count(x => ProjectMedia.Find(project, x.Chapter, x.Scene) is null);
        if (remaining > 0) log?.Invoke($"Visual pass reached its configured limit; {remaining} scene(s) remain and will use placeholders unless media is supplied.");
    }

    private static void AdoptExistingAudio(VideoProject project, ProjectChapter chapter)
    {
        if (!string.IsNullOrWhiteSpace(chapter.AudioPath) && File.Exists(chapter.AudioPath)) return;
        var candidate = Path.Combine(project.ProjectDirectory, "audio", $"chapter-{chapter.Number:00}.m4a");
        if (File.Exists(candidate)) chapter.AudioPath = candidate;
    }
}
