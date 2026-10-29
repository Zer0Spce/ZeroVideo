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
            throw new InvalidOperationException("TikTok schedule requires a future date and time.");

        var caption = page.Locator("[contenteditable=true]").First;
        if (await caption.CountAsync() > 0 && await caption.IsVisibleAsync())
            await caption.PressAsync("Tab");
        await page.Keyboard.PressAsync("Escape");
        await page.GetByText("Schedule", new() { Exact = true }).First.ClickAsync();
        await Task.Delay(650, ct);

        var culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var wantedDate = target.ToString("MMM d, yyyy", culture);
        var wantedTime = target.ToString("HH:mm");
        log?.Invoke("TikTok: requested schedule " + target.ToString("yyyy-MM-dd HH:mm"));

        // This version of TikTok Studio uses grey date/time dropdown triggers.
        // Restore the visible-text triggers that worked before the regression.
        var dateTrigger = page.GetByText(new System.Text.RegularExpressions.Regex(
            @"[A-Za-z]{3}\s+\d{1,2},?\s+\d{4}")).First;
        var oldDateTrigger = page.Locator(".date-picker-input").First;
        var dateOpened = false;
        if (await oldDateTrigger.CountAsync() > 0 && await oldDateTrigger.IsVisibleAsync())
        {
            await oldDateTrigger.ClickAsync();
            dateOpened = true;
        }
        else if (await dateTrigger.CountAsync() > 0 && await dateTrigger.IsVisibleAsync())
        {
            await dateTrigger.ClickAsync();
            dateOpened = true;
        }
        log?.Invoke("TikTok: calendar opened=" + dateOpened);
        if (dateOpened)
        {
            var calendar = page.Locator(".calendar-wrapper:visible, [role=dialog]:visible, [role=grid]:visible, [class*=calendar]:visible, [class*=Calendar]:visible").Last;
            await Task.Delay(450, ct);
            if (await calendar.CountAsync() > 0)
            {
                // Navigate to the target month before searching for its day. The
                // old implementation searched only the initially displayed month.
                var isoDate = target.ToString("yyyy-MM-dd");
                var monthLabel = target.ToString("MMMM yyyy", culture);
                var shortMonthLabel = target.ToString("MMM yyyy", culture);
                for (var attempt = 0; attempt < 13; attempt++)
                {
                    var monthText = (await calendar.InnerTextAsync()).Trim();
                    if (monthText.Contains(monthLabel, StringComparison.OrdinalIgnoreCase) ||
                        monthText.Contains(shortMonthLabel, StringComparison.OrdinalIgnoreCase))
                        break;
                    var next = calendar.Locator(
                        "[aria-label*='Next month' i], [aria-label*='next month' i], " +
                        "[class*='next-month'], [class*='nextMonth'], [class*='calendar-next'], " +
                        ".calendar-header button:last-child, .calendar-header span:last-child").Last;
                    if (await next.CountAsync() == 0 || !await next.IsVisibleAsync())
                    {
                        log?.Invoke("TikTok: next-month control not found for " + monthLabel);
                        break;
                    }
                    await next.ClickAsync(new() { Timeout = 3000 });
                    await Task.Delay(200, ct);
                }
                var day = calendar.Locator("[data-date='" + isoDate + "'], [datetime='" + isoDate + "']").First;
                if (await day.CountAsync() == 0)
                {
                    // Use only a current-month, enabled cell if the picker exposes it.
                    var valid = calendar.Locator(".days-wrapper span[class*=valid], [role=gridcell]:not([aria-disabled=true]):not([class*=outside])");
                    day = valid.Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(
                        @"^\s*" + target.Day + @"\s*$") }).First;
                }
                if (await day.CountAsync() > 0 && await day.IsVisibleAsync())
                {
                    await day.ClickAsync(new() { Timeout = 7000 });
                    log?.Invoke("TikTok: calendar day clicked: " + target.Day);
                }
                else
                    log?.Invoke("TikTok: requested date not offered in open calendar; inspect saved diagnostic.");
            }
        }
        await page.Keyboard.PressAsync("Escape");

        // Always attempt the time control even when the date picker could not
        // commit. Previous versions returned before opening it.
        var timeTrigger = page.GetByText(new System.Text.RegularExpressions.Regex(
            @"^\s*\d{1,2}:\d{2}\s*$")).First;
        var oldTimeTrigger = page.Locator(".time-picker-input").First;
        var timeOpened = false;
        if (await oldTimeTrigger.CountAsync() > 0 && await oldTimeTrigger.IsVisibleAsync())
        {
            await oldTimeTrigger.ClickAsync();
            timeOpened = true;
        }
        else if (await timeTrigger.CountAsync() > 0 && await timeTrigger.IsVisibleAsync())
        {
            await timeTrigger.ClickAsync();
            timeOpened = true;
        }
        log?.Invoke("TikTok: time dropdown opened=" + timeOpened);
        if (timeOpened)
        {
            await Task.Delay(450, ct);
            var picker = page.Locator(".tiktok-timepicker-time-picker-container:visible, [role=listbox]:visible, [role=dialog]:visible, [class*=timePicker]:visible, [class*=time-picker]:visible").Last;
            if (await picker.CountAsync() > 0)
            {
                var hourOptions = picker.Locator("span[class*=tiktok-timepicker-left]");
                var minuteOptions = picker.Locator("span[class*=tiktok-timepicker-right]");
                log?.Invoke("TikTok: picker option counts: hours=" + await hourOptions.CountAsync() +
                    ", minutes=" + await minuteOptions.CountAsync());
                if (await hourOptions.CountAsync() >= 24 && await minuteOptions.CountAsync() >= 12 && target.Minute % 5 == 0)
                {
                    // Match the displayed number, never assume DOM order is 0..23 / 0..55.
                    var hour = hourOptions.Filter(new() { HasTextRegex =
                        new System.Text.RegularExpressions.Regex(@"^\s*0?" + target.Hour + @"\s*$") }).First;
                    var minute = minuteOptions.Filter(new() { HasTextRegex =
                        new System.Text.RegularExpressions.Regex(@"^\s*0?" + target.Minute + @"\s*$") }).First;
                    if (await hour.CountAsync() == 0 || await minute.CountAsync() == 0)
                    {
                        log?.Invoke("TikTok: exact numeric hour/minute options not present.");
                        var diag = await SaveDiagnosticAsync(page, "TikTok-Time-Options", log);
                        log?.Invoke("TikTok: time picker diagnostic: " + diag);
                        return false;
                    }
                    await hour.ScrollIntoViewIfNeededAsync();
                    await hour.ClickAsync();
                    await minute.ScrollIntoViewIfNeededAsync();
                    await minute.ClickAsync();
                    log?.Invoke("TikTok: hour and minute options clicked.");
                }
                else
                {
                    var exactTime = picker.GetByText(wantedTime, new() { Exact = true }).First;
                    if (await exactTime.CountAsync() > 0 && await exactTime.IsVisibleAsync())
                    {
                        await exactTime.ClickAsync();
                        log?.Invoke("TikTok: requested time option clicked.");
                    }
                    else
                        log?.Invoke("TikTok: requested time unavailable in open dropdown.");
                }
            }
        }
        await page.Keyboard.PressAsync("Escape");
        await Task.Delay(450, ct);
        var displayedDate = await dateTrigger.CountAsync() > 0 ? (await dateTrigger.InnerTextAsync()).Trim() :
            await oldDateTrigger.CountAsync() > 0 ? (await oldDateTrigger.InnerTextAsync()).Trim() : "(not found)";
        var displayedTime = await timeTrigger.CountAsync() > 0 ? (await timeTrigger.InnerTextAsync()).Trim() :
            await oldTimeTrigger.CountAsync() > 0 ? (await oldTimeTrigger.InnerTextAsync()).Trim() : "(not found)";
        var dateOk = DateTime.TryParse(displayedDate, culture, System.Globalization.DateTimeStyles.None, out var dateRead) &&
            dateRead.Date == target.Date;
        var timeOk = displayedTime == wantedTime || displayedTime == target.ToString("H:mm");
        if (!dateOk || !timeOk)
        {
            var screenshot = await SaveDiagnosticAsync(page, "TikTok-Schedule-Picker", log);
            log?.Invoke("TikTok: schedule mismatch; expected " + wantedDate + " " + wantedTime +
                ", visible " + displayedDate + " " + displayedTime + ". Screenshot: " + screenshot);
            return false;
        }
        log?.Invoke("TikTok: schedule fields confirmed " + displayedDate + " " + displayedTime);
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
