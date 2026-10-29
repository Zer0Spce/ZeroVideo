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
        var target = slot.LocalDateTime;
        if (target <= DateTime.Now.AddMinutes(20))
            throw new InvalidOperationException("The requested TikTok schedule must be in the future.");

        // Commit the caption and close the hashtags popup before touching scheduling.
        var caption = page.Locator("[contenteditable=true]").First;
        if (await caption.CountAsync() > 0)
            await caption.PressAsync("Tab", new() { Timeout = 3000 });
        await page.Keyboard.PressAsync("Escape");
        await page.GetByText("Schedule", new() { Exact = true }).First.ClickAsync();
        await Task.Delay(600, ct);

        var requestedDate = target.ToString("MMM d, yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        var requestedTime = target.ToString("HH:mm");
        log?.Invoke("TikTok: setting schedule to " + requestedDate + " at " + requestedTime + " (local).");

        // TikTok's gray selectors are not ordinary editable date/time inputs.
        // Read the visible trigger and choose options inside the OPEN popup.
        var dateTrigger = page.Locator("button, [role=button], [role=combobox], [class*=select], [class*=Select]")
            .Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(
                @"[A-Za-z]{3}\s+\d{1,2},?\s+\d{4}") }).First;
        if (await dateTrigger.CountAsync() > 0)
        {
            await dateTrigger.ClickAsync(new() { Timeout = 7000 });
            await Task.Delay(500, ct);
            var iso = target.ToString("yyyy-MM-dd");
            var full = target.ToString("MMMM d, yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
            var dateCell = page.Locator(
                "[data-date='" + iso + "'], [aria-label='" + full + "'], [title='" + full + "'], [datetime='" + iso + "']").First;
            if (await dateCell.CountAsync() > 0 && await dateCell.IsVisibleAsync())
                await dateCell.ClickAsync(new() { Timeout = 7000 });
            else
            {
                // Navigate the calendar by its month/year heading before choosing
                // a day. Never select a generic 29 from some other month.
                for (var attempt = 0; attempt < 18; attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    var heading = page.Locator("[class*=calendar] [class*=header], [class*=Calendar] [class*=header], [class*=calendar] [class*=title], [class*=Calendar] [class*=title]").First;
                    var label = await heading.CountAsync() > 0 ? await heading.InnerTextAsync() : "";
                    var month = target.ToString("MMMM", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
                    if (label.Contains(month, StringComparison.OrdinalIgnoreCase) &&
                        label.Contains(target.Year.ToString(), StringComparison.Ordinal)) break;
                    var match = System.Text.RegularExpressions.Regex.Match(label, @"([A-Za-z]+)\s+(\d{4})");
                    if (!match.Success || !DateTime.TryParseExact(match.Value, "MMMM yyyy",
                        System.Globalization.CultureInfo.GetCultureInfo("en-US"),
                        System.Globalization.DateTimeStyles.None, out var shown)) break;
                    var forward = new DateTime(target.Year, target.Month, 1) > shown;
                    var arrow = page.Locator(forward
                        ? "[aria-label*='next month' i], [class*=calendar] [class*=next], [class*=Calendar] [class*=next]"
                        : "[aria-label*='previous month' i], [class*=calendar] [class*=prev], [class*=Calendar] [class*=prev]").First;
                    if (await arrow.CountAsync() == 0) break;
                    await arrow.ClickAsync(new() { Timeout = 5000 });
                    await Task.Delay(250, ct);
                }
                // Match a day of CURRENT month, excluding disabled/outside cells.
                var day = page.Locator("[role=gridcell]:not([aria-disabled=true]), [class*=calendar] [class*=day]:not([class*=disabled]):not([class*=outside]), [class*=Calendar] [class*=day]:not([class*=disabled]):not([class*=outside])")
                    .Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(
                        @"^\s*" + target.Day + @"\s*$") }).First;
                if (await day.CountAsync() > 0 && await day.IsVisibleAsync())
                    await day.ClickAsync(new() { Timeout = 7000 });
            }
        }
        await page.Keyboard.PressAsync("Escape");
        await Task.Delay(350, ct);

        var timeTrigger = page.Locator("button, [role=button], [role=combobox], [class*=select], [class*=Select]")
            .Filter(new() { HasTextRegex = new System.Text.RegularExpressions.Regex(@"^\s*\d{1,2}:\d{2}\s*$") }).First;
        if (await timeTrigger.CountAsync() > 0)
        {
            await timeTrigger.ClickAsync(new() { Timeout = 7000 });
            await Task.Delay(450, ct);
            // TikTok's time popup can show one full-time option or separate
            // scrollable columns. Click only an option INSIDE the visible popup.
            var pop = page.Locator("[role=listbox]:visible, [role=dialog]:visible, [class*=timePicker]:visible, [class*=TimePicker]:visible, [class*=time-picker]:visible, [class*=dropdown]:visible").Last;
            if (await pop.CountAsync() > 0)
            {
                var exact = pop.GetByText(requestedTime, new() { Exact = true }).First;
                if (await exact.CountAsync() > 0 && await exact.IsVisibleAsync())
                    await exact.ClickAsync(new() { Timeout = 7000 });
                else
                {
                    var hours = pop.GetByText(target.ToString("HH"), new() { Exact = true });
                    var mins = pop.GetByText(target.ToString("mm"), new() { Exact = true });
                    if (await hours.CountAsync() > 0 && await mins.CountAsync() > 0)
                    {
                        await hours.Last.ScrollIntoViewIfNeededAsync();
                        await hours.Last.ClickAsync(new() { Timeout = 7000 });
                        await mins.Last.ScrollIntoViewIfNeededAsync();
                        await mins.Last.ClickAsync(new() { Timeout = 7000 });
                    }
                }
            }
        }
        await page.Keyboard.PressAsync("Escape");
        await Task.Delay(450, ct);

        // Check only the two CLOSED trigger values (not the popup's choices).
        var selectedDate = await dateTrigger.CountAsync() > 0 ? (await dateTrigger.InnerTextAsync()).Trim() : "";
        var selectedTime = await timeTrigger.CountAsync() > 0 ? (await timeTrigger.InnerTextAsync()).Trim() : "";
        var dateMatch = DateTime.TryParse(selectedDate,
            System.Globalization.CultureInfo.GetCultureInfo("en-US"),
            System.Globalization.DateTimeStyles.None, out var readDate) && readDate.Date == target.Date;
        var timeMatch = selectedTime == requestedTime || selectedTime == target.ToString("H:mm");
        if (!dateMatch || !timeMatch)
        {
            var diagnostic = await SaveDiagnosticAsync(page, "TikTok-Schedule-Picker", log);
            log?.Invoke("TikTok schedule NOT set. Requested " + requestedDate + " " + requestedTime +
                "; selected " + selectedDate + " " + selectedTime + ". Screenshot: " + diagnostic);
            return false;
        }
        log?.Invoke("TikTok schedule verified on controls: " + selectedDate + " " + selectedTime);
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
