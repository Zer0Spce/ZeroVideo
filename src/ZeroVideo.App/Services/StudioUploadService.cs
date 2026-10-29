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

    // Playwright transfers SetInputFilesAsync bytes through its driver for a CDP-attached
    // browser and rejects files above 50 MB. Edge runs on this same Windows machine:
    // Chrome DevTools Protocol can instead assign the absolute local file path directly.
    private static async Task<bool> AttachLocalVideoViaCdpAsync(
        IPage page, string videoPath, Action<string>? log, CancellationToken ct)
    {
        var cdp = await page.Context.NewCDPSessionAsync(page);
        try
        {
            for (var attempt = 0; attempt < 25; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var doc = await cdp.SendAsync("DOM.getDocument", new Dictionary<string, object> { ["depth"] = -1, ["pierce"] = true });
                    if (doc is null) throw new InvalidOperationException("CDP returned no document.");
                    var rootId = doc.Value.GetProperty("root").GetProperty("nodeId").GetInt32();
                    var found = await cdp.SendAsync("DOM.querySelectorAll",
                        new Dictionary<string, object> { ["nodeId"] = rootId, ["selector"] = "input[type=file]" });
                    if (found is not null)
                    {
                        foreach (var id in found.Value.GetProperty("nodeIds").EnumerateArray())
                        {
                            var nodeId = id.GetInt32();
                            var info = await cdp.SendAsync("DOM.describeNode", new Dictionary<string, object> { ["nodeId"] = nodeId });
                            if (info is null) continue;
                            var attrs = info.Value.GetProperty("node").GetProperty("attributes");
                            var accept = "";
                            for (var i = 0; i + 1 < attrs.GetArrayLength(); i += 2)
                                if (attrs[i].GetString() == "accept")
                                    accept = attrs[i + 1].GetString() ?? "";
                            if (accept.Contains("image", StringComparison.OrdinalIgnoreCase) &&
                                !accept.Contains("video", StringComparison.OrdinalIgnoreCase)) continue;
                            await cdp.SendAsync("DOM.setFileInputFiles",
                                new Dictionary<string, object> { ["files"] = new[] { videoPath }, ["nodeId"] = nodeId });
                            log?.Invoke("TikTok: attached local MP4 through Edge DevTools without 50 MB transfer limit.");
                            return true;
                        }
                    }
                }
                catch (PlaywrightException ex)
                {
                    log?.Invoke("CDP local file attempt: " + ex.Message.Split((char)10)[0]);
                }
                await Task.Delay(800, ct);
            }
            return false;
        }
        finally
        {
            await cdp.DetachAsync();
        }
    }


    private static async Task<bool> SetTikTokScheduleAsync(IPage page, DateTimeOffset slot, Action<string>? log, CancellationToken ct)
    {
        var local = slot.LocalDateTime;
        if (local <= DateTime.Now.AddMinutes(20))
            throw new InvalidOperationException("Scheduled time must be at least 20 minutes in the future.");

        // Blur the caption editor first: its hashtag autocomplete otherwise overlays
        // TikTok Studio's scheduling controls and swallows the next click.
        await page.Locator("body").ClickAsync(new() { Position = new() { X = 10, Y = 10 } });
        await page.Keyboard.PressAsync("Escape");
        var schedule = page.GetByText("Schedule", new() { Exact = true }).First;
        await schedule.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30000 });
        await schedule.ClickAsync();
        await Task.Delay(700, ct);
        log?.Invoke("TikTok: Schedule selected. Requested local slot: " + local.ToString("yyyy-MM-dd HH:mm"));

        // TikTok Studio currently renders gray dropdowns for the date and time.
        // Native input FillAsync alone cannot change their selected values.
        var dateDone = false;
        var timeDone = false;
        foreach (var frame in page.Frames)
        {
            var dateInput = frame.Locator("input[type='date']");
            if (await dateInput.CountAsync() > 0 && await dateInput.First.IsVisibleAsync())
            {
                try { await dateInput.First.FillAsync(local.ToString("yyyy-MM-dd")); await dateInput.First.PressAsync("Tab"); dateDone = true; }
                catch (PlaywrightException) { }
            }
            var timeInput = frame.Locator("input[type='time']");
            if (await timeInput.CountAsync() > 0 && await timeInput.First.IsVisibleAsync())
            {
                try { await timeInput.First.FillAsync(local.ToString("HH:mm")); await timeInput.First.PressAsync("Tab"); timeDone = true; }
                catch (PlaywrightException) { }
            }
        }

        // The visual time selector contains a HH:mm value (e.g. 11:25).
        // Open its dropdown and click the exact requested time option, not just the
        // default value shown on the closed control.
        if (!timeDone)
        {
            var timeControl = page.Locator("button, [role='combobox'], [role='button'], [class*='select'], [class*='Select']")
                .Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(@"^\\s*\\d{1,2}:\\d{2}\\s*$") })
                .First;
            if (await timeControl.CountAsync() > 0 && await timeControl.IsVisibleAsync())
            {
                await timeControl.ClickAsync(new() { Timeout = 8000 });
                await Task.Delay(350, ct);
                var desired = local.ToString("HH:mm");
                var choices = page.Locator("[role='option'], [role='listbox'] *, [class*='option'], [class*='Option'], [class*='item'], [class*='Item']")
                    .GetByText(desired, new() { Exact = true });
                if (await choices.CountAsync() > 0 && await choices.First.IsVisibleAsync())
                {
                    await choices.First.ClickAsync(new() { Timeout = 8000 });
                    timeDone = true;
                }
                else
                {
                    // Some TikTok time pickers expose an editable field after opening.
                    var editable = page.Locator("input:visible").Last;
                    if (await editable.CountAsync() > 0)
                    {
                        try
                        {
                            await editable.FillAsync(desired, new() { Timeout = 3000 });
                            await editable.PressAsync("Enter");
                            timeDone = true;
                        }
                        catch (PlaywrightException) { }
                    }
                }
            }
        }

        // Try the native calendar and then the visible TikTok calendar dropdown.
        if (!dateDone)
        {
            var dateControl = page.Locator("button, [role='combobox'], [role='button'], [class*='select'], [class*='Select']")
                .Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(@"[A-Za-z]{3}\\s+\\d{1,2},?\\s+\\d{4}") })
                .First;
            if (await dateControl.CountAsync() > 0 && await dateControl.IsVisibleAsync())
            {
                await dateControl.ClickAsync(new() { Timeout = 8000 });
                await Task.Delay(350, ct);
                var dateInputs = page.Locator("input[type='date']:visible, input[placeholder*='date' i]:visible");
                if (await dateInputs.CountAsync() > 0)
                {
                    try { await dateInputs.First.FillAsync(local.ToString("yyyy-MM-dd")); await dateInputs.First.PressAsync("Tab"); dateDone = true; }
                    catch (PlaywrightException) { }
                }
                if (!dateDone)
                {
                    var day = page.Locator("[role='gridcell'], [role='option'], button")
                        .GetByText(local.Day.ToString(), new() { Exact = true });
                    if (await day.CountAsync() == 1 && await day.First.IsVisibleAsync())
                    {
                        await day.First.ClickAsync(new() { Timeout = 8000 });
                        dateDone = true;
                    }
                }
            }
        }

        // Read the CLOSED dropdown values again; a successful click alone does not
        // prove that TikTok accepted the requested schedule.
        await page.Keyboard.PressAsync("Escape");
        await Task.Delay(400, ct);
        var visible = await page.Locator("body").InnerTextAsync();
        var requestedTime = local.ToString("HH:mm");
        var requestedDate = local.ToString("MMM d, yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        var timeVerified = visible.Contains(requestedTime, StringComparison.Ordinal);
        var dateVerified = visible.Contains(requestedDate, StringComparison.OrdinalIgnoreCase) ||
            visible.Contains(local.ToString("MMM dd, yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-US")), StringComparison.OrdinalIgnoreCase);
        if (!dateDone || !timeDone || !dateVerified || !timeVerified)
        {
            var screenshot = await SaveDiagnosticAsync(page, "TikTok-Schedule", log);
            log?.Invoke("TikTok: requested " + local.ToString("yyyy-MM-dd HH:mm") +
                "; date selected=" + dateDone + ", time selected=" + timeDone +
                ", date visible=" + dateVerified + ", time visible=" + timeVerified +
                ". Check dropdowns manually. Screenshot: " + screenshot);
            return false;
        }
        log?.Invoke("TikTok: displayed schedule matches " + local.ToString("yyyy-MM-dd HH:mm") +
            ". Review timezone before manually submitting.");
        return true;
    }

    public async Task<string> PrepareAsync(ZeroVideoSettings settings, string destination,
        string videoPath, string title, string description, string caption, string thumbnailPath,
        DateTimeOffset? scheduledAt, Action<string>? log = null, CancellationToken ct = default)
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
        if (youtube)
        {
            var create = page.GetByRole(AriaRole.Button, new() { Name = "Create" }).First;
            if (await create.IsVisibleAsync(new() { Timeout = 5000 }))
                await create.ClickAsync();
            var upload = page.GetByText("Upload videos", new() { Exact = true }).First;
            if (await upload.IsVisibleAsync(new() { Timeout = 6000 }))
                await upload.ClickAsync();
        }
        // TikTok Studio's upload screen already exposes a file input behind
        // the Select video button. Use that input directly FIRST. Clicking the
        // left sidebar Upload dropdown can obscure the upload panel.
        var largeVideo = new FileInfo(videoPath).Length > 45L * 1024 * 1024;
        var uploaded = false;
        if (!youtube)
        {
            log?.Invoke("TikTok: using local Edge DevTools file attachment (supports videos over 50 MB)...");
            uploaded = await AttachLocalVideoViaCdpAsync(page, videoPath, log, ct);
        }
        log?.Invoke(destination + ": locating MP4 upload control...");
        for (var attempt = 0; attempt < (largeVideo ? 0 : 20) && !uploaded; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var frame in page.Frames)
            {
                try
                {
                    var inputs = frame.Locator("input[type=file]");
                    var count = await inputs.CountAsync();
                    for (var i = 0; i < count; i++)
                    {
                        var input = inputs.Nth(i);
                        var accept = (await input.GetAttributeAsync("accept") ?? "").ToLowerInvariant();
                        if (accept.Contains("image") && !accept.Contains("video")) continue;
                        log?.Invoke(destination + ": attaching MP4 to video file input...");
                        await input.SetInputFilesAsync(videoPath, new() { Timeout = 12000 });
                        uploaded = true;
                        break;
                    }
                }
                catch (PlaywrightException ex)
                {
                    log?.Invoke("Video input attempt: " + ex.Message.Split((char)10)[0]);
                }
                if (uploaded) break;
            }
            if (!uploaded) await Task.Delay(1000, ct);
        }
        if (!uploaded && !youtube && !largeVideo)
        {
            log?.Invoke("TikTok: trying visible Select video button...");
            foreach (var frame in page.Frames)
            {
                try
                {
                    var select = frame.GetByText("Select video", new() { Exact = true }).First;
                    if (await select.CountAsync() == 0 || !await select.IsVisibleAsync()) continue;
                    var chooser = await page.RunAndWaitForFileChooserAsync(
                        async () => await select.ClickAsync(new() { Timeout = 8000 }),
                        new() { Timeout = 15000 });
                    await chooser.SetFilesAsync(videoPath);
                    uploaded = true;
                    break;
                }
                catch (PlaywrightException ex)
                {
                    log?.Invoke("Select video button attempt: " + ex.Message.Split((char)10)[0]);
                }
            }
        }
        if (!uploaded)
        {
            var diagnostic = await SaveDiagnosticAsync(page, destination, log);
            throw new InvalidOperationException("Could not attach MP4 using local CDP file path. Current URL: " +
                page.Url + ". Screenshot: " + diagnostic);
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
            if (scheduledAt.HasValue)
            {
                try { await SetTikTokScheduleAsync(page, scheduledAt.Value, log, ct); }
                catch (Exception ex) when (ex is PlaywrightException or TimeoutException or InvalidOperationException)
                {
                    var screenshot = await SaveDiagnosticAsync(page, "TikTok-Schedule", log);
                    log?.Invoke("TikTok: schedule setup failed: " + ex.Message +
                        ". Set it manually before submission. Screenshot: " + screenshot);
                }
            }
        }
        return destination + ": video selected in Studio. Review upload completion, metadata, thumbnail and " +
            "schedule/privacy before manually confirming. No post has been submitted.";
    }
}
