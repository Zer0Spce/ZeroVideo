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



    // TikTok's scheduling widgets are custom popup pickers, not standard HTML
    // date/time fields. Read values from their CLOSED controls after each action.
    // TikTok Studio renders its own calendar and two-column time picker.
    // The ordinary input and generic page-text locators do not commit these controls.
    private static async Task<bool> SetTikTokScheduleAsync(IPage page, DateTimeOffset slot,
        Action<string>? log, CancellationToken ct)
    {
        var target = slot.LocalDateTime;
        if (target <= DateTime.Now.AddMinutes(20))
            throw new InvalidOperationException("TikTok schedule must be at least 20 minutes ahead.");
        if (target.Minute % 5 != 0)
            throw new InvalidOperationException("TikTok time picker uses 5-minute intervals. Choose a time ending in 00, 05, 10, etc. Requested: " + target.ToString("HH:mm"));

        // Blur caption and close hashtag completion menu before opening Schedule.
        var editor = page.Locator("[contenteditable=true]").First;
        if (await editor.CountAsync() > 0 && await editor.IsVisibleAsync())
            await editor.PressAsync("Tab", new() { Timeout = 3500 });
        await page.Keyboard.PressAsync("Escape");
        await page.GetByText("Schedule", new() { Exact = true }).First.ClickAsync();
        await Task.Delay(400, ct);
        log?.Invoke("TikTok: selecting " + target.ToString("yyyy-MM-dd HH:mm") + " local time.");

        var datePicker = page.Locator(".date-picker-input").First;
        var dateOk = false;
        if (await datePicker.CountAsync() > 0)
        {
            await datePicker.ClickAsync();
            var calendar = page.Locator(".calendar-wrapper").First;
            await calendar.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
            for (var i = 0; i < 18; i++)
            {
                ct.ThrowIfCancellationRequested();
                var monthTitle = calendar.Locator(".month-title").First;
                var monthLabel = (await monthTitle.InnerTextAsync()).Trim();
                var month = DateTime.MinValue;
                foreach (var fmt in new[] { "MMMM yyyy", "MMMM", "MMM yyyy", "MMM" })
                {
                    if (DateTime.TryParseExact(monthLabel, fmt,
                        System.Globalization.CultureInfo.GetCultureInfo("en-US"),
                        System.Globalization.DateTimeStyles.None, out var parsed))
                    {
                        month = parsed;
                        break;
                    }
                }
                if (month == DateTime.MinValue)
                    throw new InvalidOperationException("TikTok calendar month could not be interpreted: " + monthLabel);
                var currentMonth = month.Month;
                var currentYear = monthLabel.Contains(target.Year.ToString()) ? target.Year :
                    monthLabel.Contains(DateTime.Now.Year.ToString()) ? DateTime.Now.Year : target.Year;
                var delta = (target.Year - currentYear) * 12 + target.Month - currentMonth;
                if (delta == 0) break;
                var arrows = calendar.Locator("span[class*='arrow']");
                if (await arrows.CountAsync() < 2)
                    throw new InvalidOperationException("TikTok calendar month arrows not available.");
                await (delta > 0 ? arrows.Last : arrows.First).ClickAsync();
                await Task.Delay(180, ct);
            }
            // Only valid current-month day cells; duplicate days from previous/next
            // month must never be selected.
            var validDays = calendar.Locator(".days-wrapper span[class*='day'][class*='valid']");
            for (var i = 0; i < await validDays.CountAsync(); i++)
            {
                var cell = validDays.Nth(i);
                if ((await cell.InnerTextAsync()).Trim() != target.Day.ToString()) continue;
                await cell.ClickAsync(new() { Timeout = 7000 });
                dateOk = true;
                break;
            }
        }
        else
        {
            log?.Invoke("TikTok: custom date-picker-input unavailable; no speculative calendar click performed.");
        }
        if (!dateOk)
            throw new InvalidOperationException("TikTok calendar did not select requested day " + target.ToString("yyyy-MM-dd"));

        var timePicker = page.Locator(".time-picker-input").First;
        var timeOk = false;
        if (await timePicker.CountAsync() > 0)
        {
            await timePicker.ClickAsync();
            var container = page.Locator(".tiktok-timepicker-time-picker-container").First;
            await container.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 10000 });
            var hours = container.Locator("span[class*='tiktok-timepicker-left']");
            var minutes = container.Locator("span[class*='tiktok-timepicker-right']");
            // TikTok hour options are 00..23 and minutes are 00,05,...55.
            if (await hours.CountAsync() >= 24 && await minutes.CountAsync() >= 12)
            {
                var hour = hours.Nth(target.Hour);
                var minute = minutes.Nth(target.Minute / 5);
                await hour.ScrollIntoViewIfNeededAsync();
                await hour.ClickAsync();
                await Task.Delay(150, ct);
                await minute.ScrollIntoViewIfNeededAsync();
                await minute.ClickAsync();
                await timePicker.ClickAsync();
            }
            else
            {
                // Same custom picker, alternate DOM structure: select exact values
                // from each separate column rather than clicking arbitrary page text.
                var hour = hours.Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(
                    @"^\s*" + target.ToString("HH") + @"\s*$") }).First;
                var minute = minutes.Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(
                    @"^\s*" + target.ToString("mm") + @"\s*$") }).First;
                if (await hour.CountAsync() > 0 && await minute.CountAsync() > 0)
                {
                    await hour.ScrollIntoViewIfNeededAsync();
                    await hour.ClickAsync();
                    await minute.ScrollIntoViewIfNeededAsync();
                    await minute.ClickAsync();
                    await timePicker.ClickAsync();
                }
            }
            var selected = (await timePicker.InnerTextAsync()).Trim();
            timeOk = selected.Contains(target.ToString("HH:mm"), StringComparison.Ordinal);
            log?.Invoke("TikTok: time picker displays " + selected + ", requested " + target.ToString("HH:mm"));
        }
        if (!timeOk)
            throw new InvalidOperationException("TikTok time picker did not commit requested " + target.ToString("HH:mm"));

        var chosenDate = (await datePicker.InnerTextAsync()).Trim();
        var dateMatches = DateTime.TryParse(chosenDate, System.Globalization.CultureInfo.GetCultureInfo("en-US"),
            System.Globalization.DateTimeStyles.None, out var parsedDate) && parsedDate.Date == target.Date;
        // Original TikTok component also uses yyyy-MM-dd as a closed picker label.
        if (!dateMatches)
            dateMatches = chosenDate.Contains(target.ToString("yyyy-MM-dd"), StringComparison.Ordinal);
        if (!dateMatches)
            throw new InvalidOperationException("TikTok date did not commit. Requested " +
                target.ToString("yyyy-MM-dd") + ", actual " + chosenDate);
        log?.Invoke("TikTok: schedule controls VERIFIED " + chosenDate + " " + target.ToString("HH:mm") +
            ". Manually review before final Schedule submission.");
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
