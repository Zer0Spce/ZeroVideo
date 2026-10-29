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



    // TikTok Studio shows a calendar grid and a scrolling two-column hour/minute
    // wheel. The option elements do NOT necessarily have the picker container's
    // CSS class and only a subset of the wheel may be visible at a time.
    // Move the actual wheel with Playwright mouse input and click a visible
    // numeric option in the correct (left/right) column.
    private static async Task<bool> PickTikTokWheelValueAsync(
        IPage page, ILocator picker, int value, bool hourColumn,
        Action<string>? log, CancellationToken ct)
    {
        const string inspect = @"
(root, args) => {
  const outer = root.getBoundingClientRect();
  const middle = outer.left + outer.width / 2;
  const x = outer.left + (args.hour ? outer.width * .25 : outer.width * .75);
  const y = outer.top + outer.height / 2;
  const options = [];
  for (const el of root.querySelectorAll('span,div,li,button')) {
    // Use the smallest text-bearing element, never an entire column/wrapper.
    if ([...el.children].some(c => /^(0?[0-9]|[1-5][0-9])$/.test((c.textContent || '').trim()))) continue;
    const text = (el.textContent || '').trim();
    if (!/^\d{1,2}$/.test(text)) continue;
    const n = Number(text);
    if (args.hour ? n > 23 : n > 59 || n % 5 !== 0) continue;
    const r = el.getBoundingClientRect();
    const cx = r.left + r.width / 2, cy = r.top + r.height / 2;
    if (r.width < 3 || r.height < 3 || r.width > outer.width * .6) continue;
    if (args.hour ? cx >= middle : cx <= middle) continue;
    options.push({ n, cx, cy, width:r.width, height:r.height,
      inView:cy > outer.top + 5 && cy < outer.bottom - 5 });
  }
  const matching = options.filter(o => o.n === args.value && o.inView);
  matching.sort((a,b) => Math.abs(a.cy-y)-Math.abs(b.cy-y));
  const nums = options.filter(o => o.inView).map(o=>o.n);
  return {
    found: matching.length > 0,
    x: matching.length ? matching[0].cx : x,
    y: matching.length ? matching[0].cy : y,
    min:nums.length ? Math.min(...nums) : -1,
    max:nums.length ? Math.max(...nums) : -1,
    count:nums.length
  };
}";
        for (var attempt = 0; attempt < 35; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var probe = await picker.EvaluateAsync<System.Text.Json.JsonElement>(
                inspect, new { value, hour = hourColumn });
            var x = (float)probe.GetProperty("x").GetDouble();
            var y = (float)probe.GetProperty("y").GetDouble();
            if (probe.GetProperty("found").GetBoolean())
            {
                await page.Mouse.ClickAsync(x, y);
                await Task.Delay(220, ct);
                log?.Invoke("TikTok: clicked visible " +
                    (hourColumn ? "hour " : "minute ") + value);
                return true;
            }
            var min = probe.GetProperty("min").GetInt32();
            var max = probe.GetProperty("max").GetInt32();
            if (attempt == 0)
                log?.Invoke("TikTok: scrolling " + (hourColumn ? "hour" : "minute") +
                    " column; visible " + min + ".." + max + ", requested " + value);
            // Wheel within the real column, not on the popup's surrounding body.
            var direction = min >= 0 && value < min ? -1 : 1;
            if (max >= 0 && value > max) direction = 1;
            if (attempt >= 22) direction = -1; // Search the other direction if needed.
            await page.Mouse.MoveAsync(x, y);
            await page.Mouse.WheelAsync(0, direction * 144);
            await Task.Delay(180, ct);
        }
        log?.Invoke("TikTok: requested " + (hourColumn ? "hour " : "minute ") +
            value + " not found in time wheel.");
        return false;
    }

    private static async Task<bool> SetTikTokScheduleAsync(IPage page,
        DateTimeOffset slot, Action<string>? log, CancellationToken ct)
    {
        var target = slot.LocalDateTime;
        var culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var wantedDate = target.ToString("MMM d, yyyy", culture);
        var wantedTime = target.ToString("HH:mm");
        if (target <= DateTime.Now.AddMinutes(20))
        {
            log?.Invoke("TikTok: schedule must be more than 20 minutes in the future.");
            return false;
        }
        if (target.Minute % 5 != 0)
        {
            log?.Invoke("TikTok: the time picker only supports 5-minute intervals; requested " + wantedTime);
            return false;
        }

        var caption = page.Locator("[contenteditable=true]").First;
        if (await caption.CountAsync() > 0 && await caption.IsVisibleAsync())
            await caption.PressAsync("Tab");
        await page.Keyboard.PressAsync("Escape");
        await page.GetByText("Schedule", new() { Exact = true }).First.ClickAsync();
        await Task.Delay(500, ct);
        log?.Invoke("TikTok: requested " + target.ToString("yyyy-MM-dd HH:mm") + " local.");

        // Prefer the real closed control. Generic GetByText can accidentally
        // find a day in the open calendar or a time in the open wheel.
        var dateTrigger = page.Locator(".date-picker-input").First;
        if (await dateTrigger.CountAsync() == 0)
            dateTrigger = page.GetByText(new System.Text.RegularExpressions.Regex(
                @"[A-Za-z]{3}\s+\d{1,2},?\s+\d{4}")).First;
        if (await dateTrigger.CountAsync() == 0 || !await dateTrigger.IsVisibleAsync())
        {
            log?.Invoke("TikTok: date dropdown trigger missing.");
            return false;
        }
        // The current TikTok date is often already correct for 6-hour presets.
        // Clicking an already-selected day leaves the calendar open, preventing
        // the time wheel from being reached. Skip the popup in that case.
        var initialDate = (await dateTrigger.InnerTextAsync()).Trim();
        var alreadyCorrectDate = DateTime.TryParse(initialDate, culture,
            System.Globalization.DateTimeStyles.None, out var initialParsed) &&
            initialParsed.Date == target.Date;
        log?.Invoke("TikTok: current date " + initialDate + ", desired " + wantedDate +
            ", change needed=" + !alreadyCorrectDate);
        if (!alreadyCorrectDate)
        {
        await dateTrigger.ClickAsync(new() { Timeout = 7000 });
        await Task.Delay(350, ct);

        var calendar = page.Locator(".calendar-wrapper:visible").First;
        if (await calendar.CountAsync() == 0)
            calendar = page.Locator("[role=dialog]:visible:has(.days-wrapper), [class*=calendar]:visible:has(.days-wrapper)").First;
        if (await calendar.CountAsync() == 0)
        {
            log?.Invoke("TikTok: visible calendar grid missing.");
            await SaveDiagnosticAsync(page, "TikTok-Calendar-NotFound", log);
            return false;
        }
        var correctMonth = false;
        for (var attempt = 0; attempt < 18; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            // In the current TikTok UI October and 2026 are separate labels.
            // Read the combined header / entire calendar to find the month YEAR.
            var raw = (await calendar.InnerTextAsync()).Trim();
            var match = System.Text.RegularExpressions.Regex.Match(raw,
                @"\b(January|February|March|April|May|June|July|August|September|October|November|December)\s+(20\d{2})\b",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!match.Success)
            {
                log?.Invoke("TikTok: cannot read calendar month/year; first text: " +
                    raw.Replace("\n", " ").Substring(0, Math.Min(100, raw.Length)));
                break;
            }
            if (!DateTime.TryParseExact(match.Value, "MMMM yyyy", culture,
                System.Globalization.DateTimeStyles.None, out var shown)) break;
            var difference = (target.Year - shown.Year) * 12 + target.Month - shown.Month;
            if (difference == 0)
            {
                correctMonth = true;
                break;
            }
            log?.Invoke("TikTok: calendar shows " + match.Value + "; navigating " +
                (difference > 0 ? "forward" : "backward") + " to " + target.ToString("MMMM yyyy", culture));
            var arrows = calendar.Locator("span[class*='arrow'], button[class*='arrow']");
            ILocator arrow;
            if (await arrows.CountAsync() >= 2)
                arrow = difference > 0 ? arrows.Last : arrows.First;
            else
                arrow = calendar.Locator(difference > 0
                    ? "[aria-label*='next' i], [class*='next-month'], [class*='nextMonth']"
                    : "[aria-label*='prev' i], [class*='prev-month'], [class*='prevMonth']").First;
            if (await arrow.CountAsync() == 0 || !await arrow.IsVisibleAsync())
            {
                log?.Invoke("TikTok: month navigation button missing.");
                break;
            }
            await arrow.ClickAsync(new() { Timeout = 7000 });
            await Task.Delay(250, ct);
        }

        if (!correctMonth)
        {
            await SaveDiagnosticAsync(page, "TikTok-Calendar-Month-Mismatch", log);
            return false;  // Never click day N in the wrong month.
        }
        var isoDate = target.ToString("yyyy-MM-dd");
        var day = calendar.Locator("[data-date='" + isoDate +
            "'], [datetime='" + isoDate + "']").First;
        if (await day.CountAsync() == 0)
        {
            // TikTok's day numbers can appear twice (e.g. Sept 29 and Oct 29).
            // Search all exact text matches inside the verified month popup,
            // inspect ancestor disabled classes, and disambiguate by grid order.
            var matches = calendar.GetByText(target.Day.ToString(), new() { Exact = true });
            var count = await matches.CountAsync();
            var candidates = new List<ILocator>();
            for (var i = 0; i < Math.Min(count, 14); i++)
            {
                var cell = matches.Nth(i);
                if (!await cell.IsVisibleAsync()) continue;
                var disabled = await cell.EvaluateAsync<bool>(@"el => {
                    let node = el;
                    for (let level = 0; level < 4 && node; level++, node = node.parentElement) {
                        const cls = String(node.className || '').toLowerCase();
                        if (node.getAttribute('aria-disabled') === 'true' ||
                            node.hasAttribute('disabled') ||
                            /invalid|disabled|outside|other-month|prev-month|next-month/.test(cls)) return true;
                    }
                    return false;
                }");
                if (!disabled) candidates.Add(cell);
            }
            if (candidates.Count > 0)
            {
                // High day numbers from the preceding month appear BEFORE the
                // actual current-month day; low numbers from next month follow it.
                day = target.Day >= 20 ? candidates[^1] : candidates[0];
                log?.Invoke("TikTok: day " + target.Day + " matched " +
                    candidates.Count + " enabled calendar candidates.");
            }
        }
        if (await day.CountAsync() == 0 || !await day.IsVisibleAsync())
        {
            log?.Invoke("TikTok: day " + isoDate + " not available in target month.");
            await SaveDiagnosticAsync(page, "TikTok-Calendar-Day-Missing", log);
            return false;
        }
        await day.ClickAsync(new() { Timeout = 7000 });
        await Task.Delay(300, ct);
        // A click on today's already-highlighted date can leave the popup open.
        // Close it using the trigger; Escape may not dismiss this React picker.
        if (await calendar.IsVisibleAsync())
        {
            log?.Invoke("TikTok: date popup still open after day click; closing it.");
            await dateTrigger.ClickAsync(new() { Timeout = 7000 });
            await Task.Delay(200, ct);
        }
        } // end date-change branch

        var selectedDate = (await dateTrigger.InnerTextAsync()).Trim();
        var dateOk = DateTime.TryParse(selectedDate, culture,
            System.Globalization.DateTimeStyles.None, out var parsedDate) && parsedDate.Date == target.Date;
        log?.Invoke("TikTok: date after click: " + selectedDate + ", expected " + wantedDate);
        if (!dateOk)
        {
            await SaveDiagnosticAsync(page, "TikTok-Date-Uncommitted", log);
            return false;
        }

        var timeTrigger = page.Locator(".time-picker-input").First;
        if (await timeTrigger.CountAsync() == 0)
            timeTrigger = page.GetByText(new System.Text.RegularExpressions.Regex(
                @"^\s*\d{1,2}:\d{2}\s*$")).First;
        if (await timeTrigger.CountAsync() == 0 || !await timeTrigger.IsVisibleAsync())
        {
            log?.Invoke("TikTok: time dropdown trigger missing.");
            return false;
        }
        await timeTrigger.ClickAsync(new() { Timeout = 7000 });
        await Task.Delay(350, ct);
        var picker = page.Locator(".tiktok-timepicker-time-picker-container:visible").First;
        if (await picker.CountAsync() == 0)
            picker = page.Locator("[role=listbox]:visible, [class*=time-picker]:visible").First;
        if (await picker.CountAsync() == 0)
        {
            log?.Invoke("TikTok: hour/minute wheel container missing.");
            await SaveDiagnosticAsync(page, "TikTok-Time-Wheel-Missing", log);
            return false;
        }

        // Set the hour, then the minute, in the two separate scroll columns.
        var hourSet = await PickTikTokWheelValueAsync(page, picker, target.Hour, true, log, ct);
        var minuteSet = hourSet && await PickTikTokWheelValueAsync(
            page, picker, target.Minute, false, log, ct);
        if (!hourSet || !minuteSet)
        {
            await SaveDiagnosticAsync(page, "TikTok-Time-Option-Missing", log);
            return false;
        }
        // Commit the custom wheel by clicking the closed time trigger, rather
        // than Escape (which can cancel selection in some versions of TikTok).
        await timeTrigger.ClickAsync(new() { Timeout = 7000 });
        await Task.Delay(450, ct);

        var displayedTime = (await timeTrigger.InnerTextAsync()).Trim();
        var timeOk = displayedTime == wantedTime ||
            displayedTime == target.ToString("H:mm");
        log?.Invoke("TikTok: time after selection: " + displayedTime + ", expected " + wantedTime);
        if (!timeOk)
        {
            await SaveDiagnosticAsync(page, "TikTok-Time-Uncommitted", log);
            return false;
        }

        log?.Invoke("TikTok: VERIFIED schedule " + selectedDate + " " + displayedTime +
            ". Review before manually clicking final Schedule.");
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
                try
                {
                    if (!await SetTikTokScheduleAsync(page, scheduledAt.Value, log, ct))
                        return "TikTok: video attached, but requested schedule was NOT set. " +
                            "Select the date and time manually before submitting. No post has been submitted.";
                }
                catch (Exception ex) when (ex is PlaywrightException or TimeoutException or InvalidOperationException)
                {
                    var screenshot = await SaveDiagnosticAsync(page, "TikTok-Schedule", log);
                    log?.Invoke("TikTok: schedule setup failed: " + ex.Message +
                        ". Set it manually before submission. Screenshot: " + screenshot);
                    return "TikTok: video attached, but schedule selection FAILED: " + ex.Message +
                        ". Set the schedule manually. No post has been submitted.";
                }
            }
        }
        return destination + ": video selected in Studio. Review upload completion, metadata, thumbnail and " +
            "schedule/privacy before manually confirming. No post has been submitted.";
    }
}
