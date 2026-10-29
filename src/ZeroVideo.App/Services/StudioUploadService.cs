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




    // The October 2026 TikTok picker shows a 7-column grid with adjacent-month
    // dates. Select the intended grid position, NOT an ambiguous "30" text
    // locator. Mouse coordinates are derived from real DOM bounding boxes.
    private static async Task<bool> ClickTikTokCalendarGridDayAsync(IPage page,
        DateTime target, Action<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var grid = page.Locator(".days-wrapper:visible").First;
        if (await grid.CountAsync() == 0)
            grid = page.Locator("[role=grid]:visible").First;
        if (await grid.CountAsync() == 0)
        {
            log?.Invoke("TikTok: numeric day grid not found.");
            return false;
        }
        const string findCell = @"
(root, args) => {
  const gr = root.getBoundingClientRect();
  const candidates = [];
  for (const el of root.querySelectorAll('span,button,div,li,[role=gridcell]')) {
    const t = (el.textContent || '').trim();
    if (!/^(?:[1-9]|[12][0-9]|3[01])$/.test(t)) continue;
    // Only leaf day-number nodes; ignore enclosing rows and grids.
    if ([...el.children].some(c => /^(?:[1-9]|[12][0-9]|3[01])$/.test((c.textContent || '').trim()))) continue;
    const rect = el.getBoundingClientRect();
    if (rect.width < 5 || rect.height < 5 || rect.width > 100 || rect.height > 80) continue;
    const x = rect.left + rect.width / 2, y = rect.top + rect.height / 2;
    if (x < gr.left - 4 || x > gr.right + 4 || y < gr.top - 4 || y > gr.bottom + 4) continue;
    if (getComputedStyle(el).visibility === 'hidden') continue;
    candidates.push({n:Number(t),x,y,cl:String(el.className||'')});
  }
  // Multiple nested nodes can share a center; remove duplicates geometrically.
  const unique=[];
  for (const e of candidates) {
    if (!unique.some(q=>Math.abs(e.x-q.x)<7 && Math.abs(e.y-q.y)<7)) unique.push(e);
  }
  unique.sort((a,b)=>Math.abs(a.y-b.y)>10 ? a.y-b.y : a.x-b.x);
  const firstWeekday = new Date(args.year,args.month-1,1).getDay();
  const expectedIndex = firstWeekday + args.day - 1;
  const picked = unique.length >= 28 && unique.length % 7 === 0
      ? unique[expectedIndex] : null;
  return { count:unique.length, expectedIndex, displayed:picked?.n ?? -1,
           x:picked?.x ?? 0,y:picked?.y ?? 0,
           sample:unique.slice(0,8).map(v=>v.n).join(',') };
}";
        var probe = await grid.EvaluateAsync<System.Text.Json.JsonElement>(
            findCell, new { year = target.Year, month = target.Month, day = target.Day });
        var count = probe.GetProperty("count").GetInt32();
        var displayed = probe.GetProperty("displayed").GetInt32();
        log?.Invoke("TikTok: calendar grid has " + count +
            " date cells; index " + probe.GetProperty("expectedIndex").GetInt32() +
            " contains " + displayed + " (wanted " + target.Day + ")" +
            "; first cells: " + probe.GetProperty("sample").GetString());
        if (displayed != target.Day) return false;
        var x = (float)probe.GetProperty("x").GetDouble();
        var y = (float)probe.GetProperty("y").GetDouble();
        await page.Mouse.ClickAsync(x, y);
        await Task.Delay(350, ct);
        return true;
    }

    private static async Task SaveTikTokCalendarDomAsync(IPage page, Action<string>? log)
    {
        try
        {
            var html = await page.EvaluateAsync<string>(@"
() => [...document.querySelectorAll('.calendar-wrapper, .days-wrapper, .month-title, .date-picker-input')]
  .filter(el => el.getBoundingClientRect().width > 0)
  .map(el=>el.outerHTML.slice(0,20000)).join('\n\n---\n\n').slice(0,65000)");
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZeroVideo", "PublishingDiagnostics");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir,"TikTok-Calendar-DOM-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            await File.WriteAllTextAsync(dest, html);
            log?.Invoke("TikTok: calendar DOM diagnostic saved: " + dest);
        }
        catch (Exception e) { log?.Invoke("TikTok: calendar DOM diagnostic failed: " + e.Message); }
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
            calendar = page.Locator("[class*=calendar]:visible:has(.days-wrapper)").First;
        if (await calendar.CountAsync() == 0)
        {
            log?.Invoke("TikTok: visible calendar popup not found.");
            await SaveDiagnosticAsync(page, "TikTok-Calendar-NotFound", log);
            return false;
        }

        // Capture the relevant picker DOM so that any future failure can be
        // diagnosed from real markup, rather than guessing TikTok selectors.
        await SaveTikTokCalendarDomAsync(page, log);
        var calendarOpened = true;
        try
        {
            var monthMatched = false;
            for (var attempt = 0; attempt < 18; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var raw = (await calendar.InnerTextAsync()).Trim();
                var flattened = System.Text.RegularExpressions.Regex.Replace(raw, @"\s+", " ");
                var monthText = System.Text.RegularExpressions.Regex.Match(flattened,
                    @"\b(January|February|March|April|May|June|July|August|September|October|November|December)\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var yearText = System.Text.RegularExpressions.Regex.Match(flattened, @"\b20\d{2}\b");

                // If the component renders header labels outside .calendar-wrapper,
                // read their visible text from the surrounding popup as a fallback.
                if (!monthText.Success || !yearText.Success)
                {
                    var header = await page.Locator(".month-title:visible, [class*=month-title]:visible")
                        .First.AllInnerTextsAsync();
                    var combined = flattened + " " + string.Join(" ", header);
                    monthText = System.Text.RegularExpressions.Regex.Match(combined,
                        @"\b(January|February|March|April|May|June|July|August|September|October|November|December)\b",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    yearText = System.Text.RegularExpressions.Regex.Match(combined, @"\b20\d{2}\b");
                }

                // The trigger displays Oct 29, 2026 while the calendar is
                // visibly on October 2026. If its year label is not inside the
                // queried subtree, infer the displayed year from that trigger.
                if (!yearText.Success && DateTime.TryParse(initialDate, culture,
                    System.Globalization.DateTimeStyles.None, out var initialMonth))
                    yearText = System.Text.RegularExpressions.Regex.Match(
                        initialMonth.Year.ToString(), @"20\d{2}");

                if (!monthText.Success || !yearText.Success ||
                    !DateTime.TryParseExact(monthText.Value + " " + yearText.Value,
                        "MMMM yyyy", culture, System.Globalization.DateTimeStyles.None,
                        out var shown))
                {
                    log?.Invoke("TikTok: cannot read month/year: " +
                        flattened.Substring(0, Math.Min(150, flattened.Length)));
                    break;
                }
                var difference = (target.Year - shown.Year) * 12 + target.Month - shown.Month;
                log?.Invoke("TikTok: calendar shows " + shown.ToString("MMMM yyyy", culture) +
                    "; target " + target.ToString("MMMM yyyy", culture));
                if (difference == 0)
                {
                    monthMatched = true;
                    break;
                }
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
                    log?.Invoke("TikTok: month arrow missing.");
                    break;
                }
                await arrow.ClickAsync(new() { Timeout = 5000 });
                await Task.Delay(300, ct);
            }

            if (!monthMatched)
            {
                log?.Invoke("TikTok: calendar month did not match; will not select arbitrary day.");
                await SaveDiagnosticAsync(page, "TikTok-Calendar-Month-Mismatch", log);
                return false;
            }

            if (!await ClickTikTokCalendarGridDayAsync(page, target, log, ct))
            {
                log?.Invoke("TikTok: grid position for requested day could not be verified.");
                await SaveDiagnosticAsync(page, "TikTok-Calendar-Grid-Mismatch", log);
                return false;
            }
            log?.Invoke("TikTok: attempted calendar day " + target.Day + ".");
        }
        finally
        {
            // Always dismiss the popup, even if calendar parsing or clicking
            // failed. Do not leave TikTok stuck behind an open calendar.
            if (calendarOpened && await calendar.IsVisibleAsync())
            {
                await page.Keyboard.PressAsync("Escape");
                await Task.Delay(150, ct);
                if (await calendar.IsVisibleAsync())
                {
                    // Click outside the popup without reopening the date trigger.
                    var fieldBox = await dateTrigger.BoundingBoxAsync();
                    if (fieldBox is not null)
                        await page.Mouse.ClickAsync(fieldBox.X + fieldBox.Width + 12,
                            fieldBox.Y + fieldBox.Height + 45);
                }
            }
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
