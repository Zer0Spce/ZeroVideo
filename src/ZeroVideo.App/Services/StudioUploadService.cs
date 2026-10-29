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
    private static async Task<bool> SetTikTokScheduleAsync(IPage page, DateTimeOffset slot,
        Action<string>? log, CancellationToken ct)
    {
        var local = slot.LocalDateTime;
        if (local <= DateTime.Now.AddMinutes(20))
            throw new InvalidOperationException("TikTok requires a future schedule time.");

        // Commit hashtags and dismiss the suggestion popover before touching settings.
        var caption = page.Locator("[contenteditable=true]").First;
        if (await caption.CountAsync() > 0)
        {
            try { await caption.PressAsync("Tab", new() { Timeout = 3000 }); }
            catch (PlaywrightException) { }
        }
        await page.Keyboard.PressAsync("Escape");
        var radio = page.GetByText("Schedule", new() { Exact = true }).First;
        await radio.WaitForAsync(new() { State = WaitForSelectorState.Visible, Timeout = 30000 });
        await radio.ClickAsync();
        await Task.Delay(550, ct);

        var requestedDate = local.ToString("MMM d, yyyy",
            System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        var requestedTime = local.ToString("HH:mm");
        log?.Invoke("TikTok: requested " + requestedDate + " " + requestedTime + " (local timezone).");

        // Date: click the currently displayed date; TikTok opens a calendar.
        // Select the exact day in the calendar, moving months as necessary.
        var dateControl = page.GetByText(
            new System.Text.RegularExpressions.Regex(@"[A-Za-z]{3}\s+\d{1,2},?\s+\d{4}")).First;
        var dateOk = await ScheduleValueVisibleAsync(page, requestedDate);
        if (!dateOk && await dateControl.CountAsync() > 0)
        {
            await dateControl.ClickAsync(new() { Timeout = 8000 });
            await Task.Delay(350, ct);
            // The visible month header is used to navigate a calendar without
            // accidentally clicking a matching day in the wrong month.
            for (var attempt = 0; attempt < 18 && !dateOk; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var monthHeader = page.Locator(
                    "[class*='calendar'] [class*='header'], [class*='Calendar'] [class*='header'], [role='dialog'] [class*='header']");
                string header = "";
                if (await monthHeader.CountAsync() > 0)
                    header = await monthHeader.First.InnerTextAsync();
                var month = System.Globalization.CultureInfo.GetCultureInfo("en-US")
                    .DateTimeFormat.GetMonthName(local.Month);
                var atMonth = header.Contains(month, StringComparison.OrdinalIgnoreCase) &&
                    header.Contains(local.Year.ToString(), StringComparison.Ordinal);
                if (!atMonth && header.Length > 0)
                {
                    // Use month arrows only where a calendar header is present.
                    var currentMonth = DateTime.MinValue;
                    var match = System.Text.RegularExpressions.Regex.Match(header,
                        @"([A-Za-z]+)\s+(\d{4})");
                    if (match.Success)
                        DateTime.TryParseExact(match.Value, "MMMM yyyy",
                            System.Globalization.CultureInfo.GetCultureInfo("en-US"),
                            System.Globalization.DateTimeStyles.None, out currentMonth);
                    if (currentMonth != DateTime.MinValue)
                    {
                        var forward = new DateTime(local.Year, local.Month, 1) > currentMonth;
                        var arrow = page.Locator(forward
                            ? "[class*='calendar'] button[aria-label*='next' i], [class*='Calendar'] button[aria-label*='next' i]"
                            : "[class*='calendar'] button[aria-label*='prev' i], [class*='Calendar'] button[aria-label*='prev' i]").First;
                        if (await arrow.CountAsync() > 0 && await arrow.IsVisibleAsync())
                        {
                            await arrow.ClickAsync(new() { Timeout = 4000 });
                            await Task.Delay(200, ct);
                            continue;
                        }
                    }
                }
                var calendar = page.Locator(
                    "[role='dialog'], [role='grid'], [class*='calendar'], [class*='Calendar'], [class*='datepicker'], [class*='DatePicker']").Last;
                if (await calendar.CountAsync() == 0) break;
                var dayButtons = calendar.Locator(
                    "button, [role='gridcell'], [role='button'], [class*='day'], [class*='Day']")
                    .Filter(new() { HasTextRegex =
                        new System.Text.RegularExpressions.Regex(@"^\s*" + local.Day + @"\s*$") });
                var count = await dayButtons.CountAsync();
                var clicked = false;
                for (var i = count - 1; i >= 0; i--)
                {
                    var choice = dayButtons.Nth(i);
                    if (!await choice.IsVisibleAsync()) continue;
                    var disabled = await choice.GetAttributeAsync("disabled");
                    var ariaDisabled = await choice.GetAttributeAsync("aria-disabled");
                    if (disabled is not null || ariaDisabled == "true") continue;
                    await choice.ClickAsync(new() { Timeout = 5000 });
                    clicked = true;
                    break;
                }
                if (!clicked) break;
                await Task.Delay(300, ct);
                dateOk = await ScheduleValueVisibleAsync(page, requestedDate);
                if (!dateOk) break; // Never select a second, potentially wrong day.
            }
        }

        // Time: open the 24h display and select the requested time from its popup.
        // The UI can render separate hour/minute columns or a searchable list.
        await page.Keyboard.PressAsync("Escape");
        var timeOk = await ScheduleValueVisibleAsync(page, requestedTime);
        if (!timeOk)
        {
            var timeControl = page.GetByText(
                new System.Text.RegularExpressions.Regex(@"^\s*\d{1,2}:\d{2}\s*$")).First;
            if (await timeControl.CountAsync() > 0)
            {
                await timeControl.ClickAsync(new() { Timeout = 8000 });
                await Task.Delay(350, ct);
                var exactTime = page.GetByText(requestedTime, new() { Exact = true });
                if (await exactTime.CountAsync() > 0)
                {
                    // Prefer an item/option, not the closed selector's own text.
                    var option = page.Locator(
                        "[role='option'], [role='listbox'] [role='button'], [class*='time-option'], [class*='timeOption']")
                        .Filter(new() { HasText = requestedTime }).First;
                    if (await option.CountAsync() > 0 && await option.IsVisibleAsync())
                        await option.ClickAsync(new() { Timeout = 6000 });
                }
                // Try fields inside the open time popup (never the caption input).
                var popup = page.Locator(
                    "[role='dialog'], [role='listbox'], [class*='time-picker'], [class*='timePicker'], [class*='TimePicker']").Last;
                if (await popup.CountAsync() > 0 && !await ScheduleValueVisibleAsync(page, requestedTime))
                {
                    var inputs = popup.Locator("input");
                    if (await inputs.CountAsync() == 1)
                    {
                        var input = inputs.First;
                        if (await input.IsVisibleAsync())
                        {
                            await input.FillAsync(requestedTime);
                            await input.PressAsync("Enter");
                        }
                    }
                    else if (await inputs.CountAsync() >= 2)
                    {
                        var hour = inputs.Nth(0);
                        var minute = inputs.Nth(1);
                        if (await hour.IsVisibleAsync() && await minute.IsVisibleAsync())
                        {
                            await hour.FillAsync(local.ToString("HH"));
                            await minute.FillAsync(local.ToString("mm"));
                            await minute.PressAsync("Enter");
                        }
                    }
                    else
                    {
                        // Some dropdowns show two scrollable columns of hours/minutes.
                        var hourChoices = popup.GetByText(local.ToString("HH"), new() { Exact = true });
                        var minuteChoices = popup.GetByText(local.ToString("mm"), new() { Exact = true });
                        if (await hourChoices.CountAsync() == 1 && await minuteChoices.CountAsync() == 1)
                        {
                            await hourChoices.First.ClickAsync(new() { Timeout = 5000 });
                            await minuteChoices.First.ClickAsync(new() { Timeout = 5000 });
                        }
                    }
                }
                await page.Keyboard.PressAsync("Tab");
                await Task.Delay(350, ct);
                timeOk = await ScheduleValueVisibleAsync(page, requestedTime);
            }
        }

        if (!dateOk || !timeOk)
        {
            var screenshot = await SaveDiagnosticAsync(page, "TikTok-Schedule-Picker", log);
            log?.Invoke("TikTok: scheduling mismatch. Requested " +
                local.ToString("yyyy-MM-dd HH:mm") + "; date matches=" + dateOk +
                ", time matches=" + timeOk + ". Check picker manually. Screenshot: " + screenshot);
            return false;
        }
        log?.Invoke("TikTok: schedule display verified " + requestedDate + " at " + requestedTime +
            ". Review before manually confirming.");
        return true;
    }

    private static async Task<bool> ScheduleValueVisibleAsync(IPage page, string expected)
    {
        // Prefer exact visible text of a CLOSED selector, not a value in the open popup.
        var candidate = page.GetByText(expected, new() { Exact = true });
        var count = Math.Min(await candidate.CountAsync(), 10);
        for (var i = 0; i < count; i++)
            if (await candidate.Nth(i).IsVisibleAsync()) return true;
        return false;
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
