using Microsoft.Playwright;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

/// <summary>Temporary assisted browser upload. Never clicks final Publish/Schedule or bypasses account checks.</summary>
public sealed class StudioUploadService(BrowserSession browser)
{
    private static async Task<string> SaveDiagnosticAsync(IPage page, string destination, Action<string>? log)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZeroVideo", "PublishingDiagnostics");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, destination + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".png");
            await page.ScreenshotAsync(new() { Path = path, Timeout = 10000 });
            log?.Invoke("Publishing diagnostic screenshot: " + path);
            return path;
        }
        catch (Exception ex)
        {
            log?.Invoke("Publishing diagnostic capture failed: " + ex.Message);
            return "(unavailable)";
        }
    }

    public async Task<string> PrepareAsync(ZeroVideoSettings settings, string destination,
        string videoPath, string title, string description, string caption, string thumbnailPath,
        Action<string>? log = null, CancellationToken ct = default)
    {
        if (!File.Exists(videoPath)) throw new FileNotFoundException("Video is missing.", videoPath);
        if (new FileInfo(videoPath).Length == 0) throw new IOException("Video is empty.");
        await browser.EnsureStartedAsync(settings);
        var youtube = destination == "YouTube";
        var page = await browser.GetOrCreatePageAsync(youtube ? "studio.youtube.com" : "tiktok.com");
        await page.GotoAsync(youtube ? "https://studio.youtube.com/" : "https://www.tiktok.com/tiktokstudio/upload",
            new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 90000 });
        ct.ThrowIfCancellationRequested();
        log?.Invoke(destination + ": Studio opened at " + page.Url);
        if (!youtube)
        {
            for (var nav = 0; nav < 6; nav++)
            {
                var uploadLink = page.GetByRole(AriaRole.Link, new() { Name = "Upload" }).First;
                var uploadButton = page.GetByRole(AriaRole.Button, new() { Name = "Upload" }).First;
                try
                {
                    if (await uploadLink.CountAsync() > 0 && await uploadLink.IsVisibleAsync())
                    { await uploadLink.ClickAsync(new() { Timeout = 4000 }); break; }
                    if (await uploadButton.CountAsync() > 0 && await uploadButton.IsVisibleAsync())
                    { await uploadButton.ClickAsync(new() { Timeout = 4000 }); break; }
                }
                catch (PlaywrightException) { }
                await Task.Delay(1000, ct);
            }
        }
        if (youtube)
        {
            var create = page.GetByRole(AriaRole.Button, new() { Name = "Create" }).First;
            if (await create.IsVisibleAsync(new() { Timeout = 5000 }))
                await create.ClickAsync();
            var upload = page.GetByText("Upload videos", new() { Exact = true }).First;
            if (await upload.IsVisibleAsync(new() { Timeout = 6000 }))
                await upload.ClickAsync();
        }
        var uploaded = false;
        if (!youtube)
        {
            // The actual TikTok Studio uploader shows a pink "Select video" button.
            // Playwright's file chooser event handles inputs created only on click.
            log?.Invoke("TikTok: looking for Select video button...");
            for (var attempt = 0; attempt < 15 && !uploaded; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var frame in page.Frames)
                {
                    try
                    {
                        var select = frame.GetByText("Select video", new() { Exact = true }).First;
                        if (await select.CountAsync() == 0 || !await select.IsVisibleAsync()) continue;
                        log?.Invoke("TikTok: clicking Select video and attaching MP4...");
                        var chooser = await page.RunAndWaitForFileChooserAsync(
                            async () => await select.ClickAsync(new() { Timeout = 5000 }),
                            new() { Timeout = 12000 });
                        await chooser.SetFilesAsync(videoPath);
                        uploaded = true;
                        log?.Invoke("TikTok: selected MP4 through Studio file chooser.");
                        break;
                    }
                    catch (PlaywrightException ex)
                    {
                        log?.Invoke("TikTok file chooser attempt: " + ex.Message.Split((char)10)[0]);
                    }
                }
                if (!uploaded) await Task.Delay(1000, ct);
            }
        }
        if (!uploaded) log?.Invoke(destination + ": trying direct video file input...");

        for (var attempt = 0; attempt < 25 && !uploaded; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var frame in page.Frames)
            {
                try
                {
                    var inputs = frame.Locator("input[type=file]");
                    for (var i = 0; i < await inputs.CountAsync(); i++)
                    {
                        var input = inputs.Nth(i);
                        var accept = (await input.GetAttributeAsync("accept") ?? "").ToLowerInvariant();
                        if (accept.Contains("image") && !accept.Contains("video")) continue;
                        await input.SetInputFilesAsync(videoPath, new() { Timeout = 5000 });
                        uploaded = true;
                        break;
                    }
                }
                catch (PlaywrightException) { }
                if (uploaded) break;
            }
            if (!uploaded) await Task.Delay(1000, ct);
        }
        if (!uploaded)
        {
            var diagnostic = await SaveDiagnosticAsync(page, destination, log);
            throw new InvalidOperationException("Studio video upload control not found. URL: " + page.Url +
                ". Diagnostic screenshot: " + diagnostic);
        }
        log?.Invoke(destination + ": selected video file in Studio.");

        if (youtube)
        {
            await page.Locator("#textbox").First.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30000 });
            var fields = page.Locator("ytcp-social-suggestions-textbox #textbox");
            if (await fields.CountAsync() >= 2)
            {
                await fields.Nth(0).FillAsync(title);
                await fields.Nth(1).FillAsync(description);
            }
            else
            {
                log?.Invoke("YouTube: metadata fields changed; enter the prepared title and description manually.");
            }
            if (File.Exists(thumbnailPath))
            {
                try
                {
                    var thumb = page.Locator("ytcp-thumbnails-compact-editor input[type='file']").First;
                    if (await thumb.CountAsync() > 0)
                        await thumb.SetInputFilesAsync(thumbnailPath, new() { Timeout = 10000 });
                }
                catch (Exception ex) { log?.Invoke("YouTube thumbnail needs manual selection: " + ex.Message); }
            }
        }
        else
        {
            // TikTok Studio is frequently updated; use semantic text inputs and fail visibly
            // rather than silently filling an unrelated control.
            var filled = false;
            for (var attempt = 0; attempt < 30 && !filled; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                foreach (var frame in page.Frames)
                {
                    try
                    {
                        var editor = frame.Locator("[contenteditable=true]").First;
                        if (await editor.CountAsync() > 0 && await editor.IsVisibleAsync())
                        {
                            await editor.FillAsync(caption);
                            filled = true;
                            break;
                        }
                    }
                    catch (PlaywrightException) { }
                }
                if (!filled) await Task.Delay(1000, ct);
            }
            log?.Invoke(filled ? "TikTok: caption entered; review before posting." :
                "TikTok: video selected, but caption input did not appear. Enter the prepared caption manually.");
        }
        return destination + ": video selected in Studio. Review upload completion, metadata, thumbnail and " +
            "schedule/privacy before manually confirming. No post has been submitted.";
    }
}
