using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

public sealed class PipelineService(ClipchampTtsService clipchamp, FfmpegService ffmpeg, ProjectStore store)
{
    public async Task<string> RunAsync(VideoProject project, ZeroVideoSettings settings, Action<string>? log = null, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (project.Chapters.Count == 0) throw new InvalidOperationException("Generate a project plan first.");
        await store.SaveAsync(project, settings.ProjectsRoot);
        var total = project.Chapters.Count + 1.0;
        for (var i = 0; i < project.Chapters.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var chapter = project.Chapters[i];
            if (string.IsNullOrWhiteSpace(chapter.AudioPath) || !File.Exists(chapter.AudioPath))
            {
                log?.Invoke($"Generating TTS chapter {chapter.Number}/{project.Chapters.Count}: {chapter.Title}");
                await clipchamp.GenerateChapterAsync(chapter, project, settings, log, ct);
                await store.SaveAsync(project, settings.ProjectsRoot);
            }
            progress?.Report((i + 1) / total * 100.0);
        }
        log?.Invoke("Rendering final video with FFmpeg...");
        var output = await ffmpeg.RenderAsync(project, settings, log, ct);
        progress?.Report(100);
        await store.SaveAsync(project, settings.ProjectsRoot);
        return output;
    }
}
