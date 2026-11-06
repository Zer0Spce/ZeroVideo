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





    // Strictly scope all interaction to schedule controls. Avoid arbitrary
    // coordinates: the user's previous test had toggled Music copyright check.
    private static async Task SaveTikTokPickerEvidenceAsync(
        IPage page, string kind, Action<string>? log)
    {
        try
        {
            var html=await page.EvaluateAsync<string>(@"
()=>['.DatePicker__panel','.Picker__panel.schedule-picker-panel',
 '.tiktok-timepicker-time-picker-container','.TimePicker__panel',
 '.date-picker-input','.time-picker-input']
.map(sel=>{const e=[...document.querySelectorAll(sel)].find(n=>{
const r=n.getBoundingClientRect();return r.width>0&&r.height>0});
return sel+'\n'+(e?e.outerHTML.slice(0,45000):'not visible')})
.join('\n------\n').slice(0,100000)");
            var dir=Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),"ZeroVideo","PublishingDiagnostics");
            Directory.CreateDirectory(dir);
            var path=Path.Combine(dir,"TikTok-Picker-"+kind+"-"+
                DateTime.Now.ToString("yyyyMMdd-HHmmss")+".txt");
            await File.WriteAllTextAsync(path,html);
            log?.Invoke("TikTok: scoped picker evidence: "+path);
        }
        catch(Exception ex){ log?.Invoke("TikTok: picker evidence error "+ex.Message); }
    }

    private static async Task<bool> WaitForTikTokUploadReadyAsync(
        IPage page, Action<string>? log, CancellationToken ct)
    {
        var uploading = page.GetByText(new System.Text.RegularExpressions.Regex(
            @"^\s*Uploading(?:\.{0,3})?\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase)).First;
        // Give TikTok a chance to mount the upload progress card.
        await Task.Delay(1200, ct);
        var seenUploading = false;
        for (var i = 0; i < 150; i++)
        {
            ct.ThrowIfCancellationRequested();
            var isUploading = await uploading.CountAsync() > 0 &&
                await uploading.IsVisibleAsync();
            if (isUploading)
            {
                seenUploading = true;
                if (i == 0 || i % 10 == 0)
                    log?.Invoke("TikTok: video still uploading; delaying schedule selection.");
            }
            else if (!seenUploading || i > 0)
            {
                log?.Invoke("TikTok: upload indicator cleared; scheduling controls may be edited.");
                // Let React finish replacing the upload card.
                await Task.Delay(1000, ct);
                return true;
            }
            await Task.Delay(2000, ct);
        }
        log?.Invoke("TikTok: upload still in progress after five minutes. Schedule not attempted.");
        await SaveDiagnosticAsync(page, "TikTok-Upload-NotReady", log);
        return false;
    }



    private static async Task<bool> SetTikTokDateStrictAsync(
        IPage page, ILocator dateTrigger, DateTime target,
        Action<string>? log, CancellationToken ct)
    {
        var culture=System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var shown=(await dateTrigger.InnerTextAsync()).Trim();
        if(DateTime.TryParse(shown,culture,System.Globalization.DateTimeStyles.None,out var existing)
            && existing.Date==target.Date) return true;
        var calendar=page.Locator(".DatePicker__panel:visible").First;
        // TikTok mounts the calendar asynchronously in React. The previous
        // immediate CountAsync check raced this render: at 08:21:40 it opened
        // successfully, but repeated later attempts reported Panel-Missing.
        // Never click twice if the popup is already open (that toggles it shut).
        if(await calendar.CountAsync()==0)
            await dateTrigger.ClickAsync(new(){Timeout=7000});
        try
        {
            await calendar.WaitForAsync(new()
            {
                State=WaitForSelectorState.Visible, Timeout=7000
            });
            log?.Invoke("TikTok: date calendar rendered after opening the trigger.");
        }
        catch(PlaywrightException ex)
        {
            log?.Invoke("TikTok: date calendar did not render within 7 seconds " +
                "after the date-trigger click: " + ex.Message.Split((char)10)[0]);
        }
        if(await calendar.CountAsync()==0 || !await calendar.IsVisibleAsync())
        {
            log?.Invoke("TikTok: DatePicker__panel still unavailable; not selecting a day.");
            await SaveTikTokPickerEvidenceAsync(page,"Date-Panel-Missing",log);
            await SaveTikTokLaterDateContextAsync(page,
                "Initial-Date-Panel-Missing",log);
            await page.Keyboard.PressAsync("Escape");
            return false;
        }
        await SaveTikTokPickerEvidenceAsync(page,"Date-Open",log);
        var monthTitle=calendar.Locator(".DatePicker__titleMonth").First;
        var yearTitle=calendar.Locator(".DatePicker__titleYear").First;
        if(await monthTitle.CountAsync()!=1||await yearTitle.CountAsync()!=1)
        {
            log?.Invoke("TikTok: month/year titles absent.");
            await page.Keyboard.PressAsync("Escape");
            return false;
        }
        var matched=false;
        // TikTok's calendar can re-render asynchronously. Never click the
        // same month arrow again until its header confirms the last click
        // advanced exactly one month. This also detects dates blocked by
        // TikTok's native scheduling window.
        for(var i=0;i<24;i++)
        {
            ct.ThrowIfCancellationRequested();
            var monthText=(await monthTitle.InnerTextAsync()).Trim();
            var yearText=(await yearTitle.InnerTextAsync()).Trim();
            if(!int.TryParse(yearText,out var y)||
                !DateTime.TryParseExact(monthText,new[]{"MMMM","MMM"},culture,
                    System.Globalization.DateTimeStyles.None,out var m))
            {
                log?.Invoke("TikTok: unrecognized month header "+monthText+" "+yearText);
                break;
            }
            var shownMonth=new DateTime(y,m.Month,1);
            var targetMonth=new DateTime(target.Year,target.Month,1);
            var difference=(target.Year-y)*12+target.Month-m.Month;
            log?.Invoke("TikTok: calendar shows "+shownMonth.ToString("yyyy-MM")+
                "; requested "+targetMonth.ToString("yyyy-MM")+
                "; remaining month transitions="+Math.Abs(difference));
            if(difference==0){matched=true;break;}

            // Only use the actual button inside the opened DatePicker.
            var arrow=calendar.Locator(difference>0?
                "button[aria-label='Next month']":"button[aria-label='Previous month']").First;
            if(await arrow.CountAsync()!=1 || !await arrow.IsVisibleAsync() ||
               !await arrow.IsEnabledAsync() ||
               await arrow.GetAttributeAsync("aria-disabled")=="true")
            {
                log?.Invoke("TikTok: month navigation unavailable. The requested date "+
                    target.ToString("yyyy-MM-dd")+" may exceed TikTok's scheduling window.");
                await SaveTikTokPickerEvidenceAsync(page,"Date-Month-Navigation-Blocked",log);
                break;
            }

            var expectedNext=shownMonth.AddMonths(difference>0?1:-1);
            await arrow.ClickAsync(new(){Timeout=6000});
            var advanced=false;
            // Poll the live header for up to ~4 seconds instead of assuming
            // that TikTok has updated after a fixed 180ms.
            for(var poll=0;poll<20;poll++)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(200,ct);
                var newMonthText=(await monthTitle.InnerTextAsync()).Trim();
                var newYearText=(await yearTitle.InnerTextAsync()).Trim();
                if(!int.TryParse(newYearText,out var newYear)||
                    !DateTime.TryParseExact(newMonthText,new[]{"MMMM","MMM"},culture,
                        System.Globalization.DateTimeStyles.None,out var newMonth))
                    continue;
                var actualMonth=new DateTime(newYear,newMonth.Month,1);
                if(actualMonth==shownMonth)continue;
                if(actualMonth!=expectedNext)
                {
                    log?.Invoke("TikTok: calendar advanced unexpectedly from "+
                        shownMonth.ToString("yyyy-MM")+" to "+
                        actualMonth.ToString("yyyy-MM")+"; stopping without selecting a day.");
                    await SaveTikTokPickerEvidenceAsync(page,"Date-Unexpected-Month",log);
                    return false;
                }
                advanced=true;
                break;
            }
            if(!advanced)
            {
                log?.Invoke("TikTok: month arrow click did not advance the calendar "+
                    "within four seconds. TikTok may have disabled later dates.");
                await SaveTikTokPickerEvidenceAsync(page,"Date-Month-Not-Advanced",log);
                break;
            }
        }
        if(!matched)
        {
            await SaveTikTokPickerEvidenceAsync(page,"Date-Wrong-Month",log);
            await page.Keyboard.PressAsync("Escape");
            return false;
        }
        var suffix=target.Day switch
        {
            11 or 12 or 13 => "th",
            _ when target.Day%10==1 => "st",
            _ when target.Day%10==2 => "nd",
            _ when target.Day%10==3 => "rd",
            _ => "th"
        };
        var labelSuffix=", "+target.ToString("MMMM",culture)+" "+
            target.Day+suffix+", "+target.Year;
        // Actual markup from user's uploaded diagnostic: DatePicker__day
        // aria-label="Friday, October 30th, 2026", data-disabled="false".
        var option=calendar.Locator(
            "button.DatePicker__day[aria-label$='"+labelSuffix+
            "'][data-outside='false'][data-disabled='false']:not([disabled])");
        if(await option.CountAsync()!=1||!await option.IsEnabledAsync())
        {
            log?.Invoke("TikTok: expected enabled calendar button missing: "+labelSuffix);
            await SaveTikTokPickerEvidenceAsync(page,"Date-Day-Missing",log);
            await page.Keyboard.PressAsync("Escape");
            return false;
        }
        await option.ClickAsync(new(){Timeout=7000});
        // No Escape on successful day click; it may revert the selection.
        await Task.Delay(350,ct);
        var selected=(await dateTrigger.InnerTextAsync()).Trim();
        var ok=DateTime.TryParse(selected,culture,
            System.Globalization.DateTimeStyles.None,out var read)&&read.Date==target.Date;
        log?.Invoke("TikTok: date requested "+target.ToString("yyyy-MM-dd")+
            "; displayed "+selected+"; verified="+ok);
        if(!ok) await SaveTikTokPickerEvidenceAsync(page,"Date-Uncommitted",log);
        return ok;
    }

    // The failing diagnostics show the calendar popup and the old
    // .date-picker-input class are BOTH absent on a subsequent upload, while
    // the independent time popup still works. The generic date text locator
    // can click a non-interactive child instead of the actual date control.
    // Identify the closest date-specific interactive wrapper before clicking.
    private static async Task<ILocator?> LocateLaterTikTokDateTriggerAsync(
        IPage page, Action<string>? log)
    {
        const string script = """
            () => {
              document.querySelectorAll('[data-zerovideo-later-date-trigger]')
                .forEach(n => n.removeAttribute('data-zerovideo-later-date-trigger'));
              const isVisible = n => {
                const r=n.getBoundingClientRect(), st=getComputedStyle(n);
                return r.width>10 && r.height>5 &&
                  st.display!=='none' && st.visibility!=='hidden';
              };
              const date = /^(?:[A-Za-z]{3,9}\s+\d{1,2}(?:st|nd|rd|th)?,?\s+\d{4}|\d{4}-\d{2}-\d{2})$/i;
              const found = new Map();
              // This is a *date* detector only. It cannot select privacy,
              // music copyright, the time control, or final submit button.
              for (const leaf of document.querySelectorAll(
                'input,button,span,div,[role="button"],[role="combobox"]')) {
                if (!isVisible(leaf) || leaf.closest('[contenteditable="true"]'))
                  continue;
                const text=(leaf instanceof HTMLInputElement
                  ? leaf.value : (leaf.innerText||'')).trim();
                if (text.length>45 || !date.test(text)) continue;
                let parent=leaf;
                for (let depth=0; parent && depth<=5; depth++,parent=parent.parentElement) {
                  if (!isVisible(parent)) continue;
                  const rect=parent.getBoundingClientRect();
                  if (rect.width<60 || rect.width>620 || rect.height>150) continue;
                  const cls=(typeof parent.className==='string'
                    ? parent.className : '').toLowerCase();
                  const role=(parent.getAttribute('role')||'').toLowerCase();
                  const ownTag=parent.tagName.toLowerCase();
                  const dateClass=/date|calendar/.test(cls);
                  const pickerClass=/picker|select|dropdown/.test(cls);
                  const interactive=['button','input'].includes(ownTag) ||
                    ['button','combobox'].includes(role) ||
                    parent.hasAttribute('tabindex');
                  if (!dateClass && !pickerClass && !interactive) continue;
                  const score=(dateClass?12:0)+(pickerClass?5:0)+
                    (interactive?8:0)+(depth>0?2:0)-depth;
                  if (!found.has(parent) || score>found.get(parent).score)
                    found.set(parent,{node:parent,score,text,depth});
                }
              }
              const candidates=[...found.values()].sort((a,b)=>b.score-a.score);
              if (!candidates.length) return {found:false,reason:'No visible date-specific clickable element'};
              const best=candidates[0];
              if (candidates.length>1 && candidates[1].score===best.score &&
                  !best.node.contains(candidates[1].node) &&
                  !candidates[1].node.contains(best.node))
                return {found:false,reason:'Ambiguous date controls',count:candidates.length};
              best.node.setAttribute('data-zerovideo-later-date-trigger','true');
              return {found:true,label:best.text,
                tag:best.node.tagName,role:best.node.getAttribute('role')||'',
                classes:typeof best.node.className==='string'
                  ? best.node.className.slice(0,180):'',
                count:candidates.length};
            }
            """;
        var selection = await page.EvaluateAsync<System.Text.Json.JsonElement>(script);
        if (!selection.GetProperty("found").GetBoolean())
        {
            log?.Invoke("TikTok: later-video DATE CONTROL NOT FOUND: " +
                selection.GetProperty("reason").GetString());
            await SaveTikTokLaterDateContextAsync(page, "Trigger-Missing", log);
            return null;
        }
        log?.Invoke("TikTok: later-video date control identified: " +
            selection.GetProperty("tag").GetString() + " class='" +
            selection.GetProperty("classes").GetString() + "' value='" +
            selection.GetProperty("label").GetString() + "'.");
        var marked=page.Locator("[data-zerovideo-later-date-trigger='true']");
        if (await marked.CountAsync()!=1 || !await marked.IsVisibleAsync())
        {
            log?.Invoke("TikTok: date control changed during identification.");
            await SaveTikTokLaterDateContextAsync(page,"Trigger-Rerendered",log);
            return null;
        }
        return marked;
    }

    private static async Task SaveTikTokLaterDateContextAsync(
        IPage page, string reason, Action<string>? log)
    {
        try
        {
            const string script = """
                () => {
                  const candidates=[];
                  const date=/[A-Za-z]{3,9}\s+\d{1,2},?\s+\d{4}|\d{4}-\d{2}-\d{2}/;
                  const isVisible=n=>{const r=n.getBoundingClientRect();
                    return r.width>0 && r.height>0;};
                  const names=['.date-picker-input','.DatePicker__panel',
                    '[class*="DatePicker"]','[class*="date-picker"]',
                    '[data-zerovideo-later-date-trigger]'];
                  for(const sel of names)
                    for(const n of [...document.querySelectorAll(sel)].slice(0,12))
                      candidates.push({selector:sel,visible:isVisible(n),
                        html:n.outerHTML.slice(0,1600)});
                  for(const n of document.querySelectorAll('button,span,div,input')) {
                    if(candidates.length>=100)break;
                    const text=(n instanceof HTMLInputElement ? n.value :
                      (n.innerText||'')).trim();
                    if(text.length>50 || !date.test(text) || !isVisible(n))continue;
                    candidates.push({value:text,html:n.outerHTML.slice(0,700),
                      parent:n.parentElement?.outerHTML.slice(0,1200)});
                  }
                  const schedule=[...document.querySelectorAll(
                    'button,[role="radio"],input[type="radio"]')]
                    .filter(n=>/schedule/i.test((n.innerText||'')+' '+
                      (n.getAttribute('aria-label')||'')))
                    .slice(0,8).map(n=>({html:n.outerHTML.slice(0,700),
                      checked:n.getAttribute('aria-checked')||
                        (n instanceof HTMLInputElement ? n.checked : null)}));
                  return JSON.stringify({url:location.pathname,
                    dateCandidates:candidates,scheduleControls:schedule},null,2);
                }
                """;
            var evidence=await page.EvaluateAsync<string>(script);
            var dir=Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
                "ZeroVideo","PublishingDiagnostics");
            Directory.CreateDirectory(dir);
            var filename="TikTok-Later-Date-"+reason+"-"+
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+".txt";
            var path=Path.Combine(dir,filename);
            await File.WriteAllTextAsync(path,evidence);
            log?.Invoke("TikTok: later-video date/trigger evidence saved: "+path);
        }
        catch(Exception ex)
        {
            log?.Invoke("TikTok: date trigger diagnostic failed: "+ex.Message);
        }
    }

    // The normal picker path is preserved for video #1. Later videos
    // occasionally re-render their calendar after file attachment and leave
    // the closed date unchanged. Recover only that failed later-video date:
    // inspect the calendar month and choose exactly one enabled date cell.
    // TikTok's wrapper can contain a stale hidden input from an earlier
    // selected date. What the user sees on the closed picker is authoritative;
    // fall back to input values ONLY if the visible text contains no date.
    private static async Task<string> ReadTikTokClosedDateAsync(ILocator dateTrigger)
    {
        var candidates = await ReadTikTokDateCandidatesAsync(dateTrigger);
        return TikTokSubsequentDatePolicy.PreferVisibleDate(
            candidates[0], candidates[1], candidates[2]);
    }

    private static async Task<string[]> ReadTikTokDateCandidatesAsync(ILocator dateTrigger)
    {
        return await dateTrigger.EvaluateAsync<string[]>(@"element => {
            const text = (element.innerText || '').trim();
            const direct = typeof element.value === 'string' ? element.value.trim() : '';
            const input = element.querySelector('input');
            const child = input && typeof input.value === 'string'
                ? input.value.trim() : '';
            return [text, direct, child];
        }");
    }

    private static async Task LogTikTokDateReadbackAsync(
        ILocator dateTrigger, DateTime target, Action<string>? log)
    {
        var values = await ReadTikTokDateCandidatesAsync(dateTrigger);
        var selected = TikTokSubsequentDatePolicy.PreferVisibleDate(
            values[0], values[1], values[2]);
        log?.Invoke("TikTok: later-video date readback requested=" +
            target.ToString("yyyy-MM-dd") + "; chosen='" + selected +
            "'; visible text='" + values[0] + "'; element value='" +
            values[1] + "'; nested input='" + values[2] + "'.");
    }

    private static async Task<bool> RecoverTikTokDateForLaterVideoAsync(
        IPage page, ILocator dateTrigger, DateTime target,
        Action<string>? log, CancellationToken ct)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var wanted = target.ToString("yyyy-MM-dd");
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var before = await ReadTikTokClosedDateAsync(dateTrigger);
                if (TikTokSubsequentDatePolicy.MatchesDisplayedDate(before, target))
                {
                    log?.Invoke("TikTok: later-video date already committed: " + before);
                    return true;
                }

                log?.Invoke("TikTok: later-video date recovery " + attempt +
                    "/2; requested=" + wanted + "; currently displayed='" + before + "'.");
                await LogTikTokDateReadbackAsync(dateTrigger, target, log);
                await page.Keyboard.PressAsync("Escape");
                await dateTrigger.ClickAsync(new() { Timeout = 7000 });
                var calendar = page.Locator(".DatePicker__panel:visible").First;
                try
                {
                    await calendar.WaitForAsync(new()
                    {
                        State = WaitForSelectorState.Visible, Timeout = 7000
                    });
                }
                catch (PlaywrightException)
                {
                    log?.Invoke("TikTok: later-video date trigger clicked, but no " +
                        "DatePicker__panel appeared in seven seconds.");
                    await SaveTikTokLaterDateContextAsync(page,"Panel-Missing",log);
                    if (attempt < 2) continue;
                    return false;
                }

                var month = calendar.Locator(".DatePicker__titleMonth").First;
                var year = calendar.Locator(".DatePicker__titleYear").First;
                if (await month.CountAsync() == 0 || await year.CountAsync() == 0)
                {
                    log?.Invoke("TikTok: recovery calendar month/year header missing.");
                    await SaveTikTokPickerEvidenceAsync(page, "Later-Date-Header-Missing", log);
                    return false;
                }

                var atTargetMonth = false;
                for (var turn = 0; turn < 5; turn++)
                {
                    ct.ThrowIfCancellationRequested();
                    var monthText = (await month.InnerTextAsync()).Trim();
                    var yearText = (await year.InnerTextAsync()).Trim();
                    if (!int.TryParse(yearText, out var shownYear) ||
                        !DateTime.TryParseExact(monthText, new[] { "MMMM", "MMM" },
                            culture, System.Globalization.DateTimeStyles.None, out var shown))
                    {
                        log?.Invoke("TikTok: recovery cannot parse month header '" +
                            monthText + " " + yearText + "'.");
                        await SaveTikTokPickerEvidenceAsync(page, "Later-Date-Header-Unreadable", log);
                        return false;
                    }

                    var distance = TikTokSubsequentDatePolicy.MonthsToTarget(
                        shownYear, shown.Month, target);
                    log?.Invoke("TikTok: later-video calendar " + shownYear + "-" +
                        shown.Month.ToString("D2") + "; requested=" +
                        target.ToString("yyyy-MM") + "; month difference=" + distance);
                    if (distance == 0)
                    {
                        atTargetMonth = true;
                        break;
                    }
                    // Clicking an unavailable arrow should not select a wrong
                    // month/day or continue into the final Schedule action.
                    var nav = calendar.Locator(distance > 0
                        ? "button[aria-label='Next month']"
                        : "button[aria-label='Previous month']").First;
                    if (await nav.CountAsync() != 1 || !await nav.IsEnabledAsync() ||
                        await nav.GetAttributeAsync("aria-disabled") == "true")
                    {
                        log?.Invoke("TikTok: requested later-video month is inaccessible. " +
                            "Check TikTok's scheduling window; no post will be submitted.");
                        await SaveTikTokPickerEvidenceAsync(page, "Later-Date-Month-Blocked", log);
                        return false;
                    }

                    var expected = new DateTime(shownYear, shown.Month, 1)
                        .AddMonths(distance > 0 ? 1 : -1);
                    await nav.ClickAsync(new() { Timeout = 6000 });
                    var advanced = false;
                    for (var poll = 0; poll < 24; poll++)
                    {
                        ct.ThrowIfCancellationRequested();
                        await Task.Delay(200, ct);
                        var nextMonthText = (await month.InnerTextAsync()).Trim();
                        var nextYearText = (await year.InnerTextAsync()).Trim();
                        if (int.TryParse(nextYearText, out var nextYear) &&
                            DateTime.TryParseExact(nextMonthText,
                                new[] { "MMMM", "MMM" }, culture,
                                System.Globalization.DateTimeStyles.None, out var next) &&
                            nextYear == expected.Year && next.Month == expected.Month)
                        {
                            advanced = true;
                            break;
                        }
                    }
                    if (!advanced)
                    {
                        log?.Invoke("TikTok: later-video month arrow did not reach " +
                            expected.ToString("yyyy-MM") + "; refusing a wrong date.");
                        await SaveTikTokPickerEvidenceAsync(page, "Later-Date-Month-Not-Changed", log);
                        return false;
                    }
                }

                if (!atTargetMonth)
                {
                    log?.Invoke("TikTok: later-video target month could not be reached.");
                    await SaveTikTokPickerEvidenceAsync(page, "Later-Date-Wrong-Month", log);
                    return false;
                }

                var buttons = calendar.Locator("button.DatePicker__day");
                var matches = new List<ILocator>();
                var numericFallback = new List<ILocator>();
                var count = await buttons.CountAsync();
                for (var i = 0; i < count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var day = buttons.Nth(i);
                    if (!await day.IsEnabledAsync() ||
                        await day.GetAttributeAsync("data-disabled") == "true" ||
                        await day.GetAttributeAsync("data-outside") == "true")
                        continue;

                    var label = await day.GetAttributeAsync("aria-label");
                    var iso = await day.GetAttributeAsync("data-date") ??
                        await day.GetAttributeAsync("datetime");
                    if (TikTokSubsequentDatePolicy.MatchesCalendarDay(label, iso, target))
                    {
                        matches.Add(day);
                    }
                    else if (string.IsNullOrWhiteSpace(label) &&
                        string.Equals((await day.InnerTextAsync()).Trim(),
                            target.Day.ToString(), StringComparison.Ordinal) &&
                        await day.GetAttributeAsync("data-outside") == "false")
                    {
                        numericFallback.Add(day);
                    }
                }

                var choices = matches.Count > 0 ? matches : numericFallback;
                if (choices.Count != 1)
                {
                    log?.Invoke("TikTok: later-video day " + wanted +
                        " is missing, disabled or ambiguous; matching day buttons=" +
                        choices.Count + ". No submission will be attempted.");
                    await SaveTikTokPickerEvidenceAsync(page, "Later-Date-Day-Unavailable", log);
                    return false;
                }

                await choices[0].ClickAsync(new() { Timeout = 7000 });
                for (var poll = 0; poll < 24; poll++)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(250, ct);
                    var displayed = await ReadTikTokClosedDateAsync(dateTrigger);
                    if (TikTokSubsequentDatePolicy.MatchesDisplayedDate(displayed, target))
                    {
                        log?.Invoke("TikTok: later-video date committed after recovery: " +
                            displayed + " (requested " + wanted + ").");
                        return true;
                    }
                }

                log?.Invoke("TikTok: later-video date click did not commit " + wanted +
                    "; will retry the calendar once if safe.");
                await SaveTikTokPickerEvidenceAsync(page, "Later-Date-Not-Committed", log);
            }
            catch (PlaywrightException ex)
            {
                log?.Invoke("TikTok: later-video calendar changed during recovery: " +
                    ex.Message.Split((char)10)[0]);
                await SaveTikTokPickerEvidenceAsync(page, "Later-Date-Picker-Changed", log);
            }
            if (attempt < 2) await Task.Delay(600, ct);
        }
        return false;
    }


    private static async Task<ILocator?> GetTikTokTimePopupAsync(
        IPage page, ILocator timeTrigger, Action<string>? log)
    {
        var popup=page.Locator(
            ".tiktok-timepicker-time-picker-container:visible, .TimePicker__panel:visible").First;
        if(await popup.CountAsync()>0)return popup;
        // Current TikTok CSS may differ. A candidate must be a small visible
        // popup containing at least 10 short numeric option texts in TWO
        // distinct horizontal columns, adjacent to the time trigger.
        var marked=await page.EvaluateAsync<bool>(@"
()=>{document.querySelectorAll('[data-zerovideo-time-popup]').forEach(
 e=>e.removeAttribute('data-zerovideo-time-popup'));
 const trigger=[...document.querySelectorAll('.time-picker-input')].find(e=>{
 const r=e.getBoundingClientRect();return r.width>10&&r.height>10});
 if(!trigger)return false;
 const tr=trigger.getBoundingClientRect();
 let matches=[];
 for(const p of document.querySelectorAll('[role=dialog],[role=listbox],.Picker__panel,[class*=TimePicker]')){
  const r=p.getBoundingClientRect(),s=getComputedStyle(p);
  if(s.display==='none'||s.visibility==='hidden'||r.width<180||
     r.width>520||r.height<180||r.height>650)continue;
  if(Math.abs((r.left+r.width/2)-(tr.left+tr.width/2))>310||
     Math.min(Math.abs(r.bottom-tr.top),Math.abs(r.top-tr.bottom))>200)continue;
  const leaves=[...p.querySelectorAll('span,button,div,li')].filter(e=>{
    const t=(e.textContent||'').trim();
    return /^\d{1,2}$/.test(t) && ![...e.children].some(c=>(c.textContent||'').trim()===t);
  }).map(e=>{const q=e.getBoundingClientRect();return {x:q.left+q.width/2,
    y:q.top+q.height/2,n:Number(e.textContent.trim())}});
  const left=leaves.filter(v=>v.x<r.left+r.width*.5 && v.y>r.top && v.y<r.bottom);
  const right=leaves.filter(v=>v.x>=r.left+r.width*.5 && v.y>r.top && v.y<r.bottom);
  if(left.length>=4 && right.length>=4 && right.some(v=>v.n%5===0))
    matches.push({p,area:r.width*r.height});
 }
 matches.sort((a,b)=>a.area-b.area);
 if(!matches.length)return false;
 matches[0].p.setAttribute('data-zerovideo-time-popup','true');return true;
}");
        if(!marked)
        {
            log?.Invoke("TikTok: no verified time popup; unrelated controls untouched.");
            return null;
        }
        return page.Locator("[data-zerovideo-time-popup='true']");
    }

    private static async Task<bool> SelectTikTokTimeColumnAsync(
        IPage page, ILocator popup, int value, bool hour,
        Action<string>? log, CancellationToken ct)
    {
        var direct=popup.Locator(hour?
            "span[class*='tiktok-timepicker-left']":
            "span[class*='tiktok-timepicker-right']")
            .Filter(new(){HasTextRegex=new System.Text.RegularExpressions.Regex(
                @"^\s*0?"+value+@"\s*$")}).First;
        if(await direct.CountAsync()>0)
        {
            await direct.ScrollIntoViewIfNeededAsync(new(){Timeout=5000});
            await direct.ClickAsync(new(){Timeout=5000});
            log?.Invoke("TikTok: selected "+(hour?"hour ":"minute ")+value+" via popup option.");
            return true;
        }
        // For newer markup, mark ONLY a numeric leaf inside the verified
        // time popup, in the correct left/right column.
        const string inspect=@"
(root,args)=>{
 root.querySelectorAll('[data-zerovideo-option]').forEach(
 e=>e.removeAttribute('data-zerovideo-option'));
 const b=root.getBoundingClientRect(),mid=b.left+b.width/2;
 const nums=[];
 for(const e of root.querySelectorAll('span,button,div,li')){
   const t=(e.textContent||'').trim();
   if(!/^\d{1,2}$/.test(t))continue;
   if([...e.children].some(c=>(c.textContent||'').trim()===t))continue;
   const r=e.getBoundingClientRect(),x=r.left+r.width/2,y=r.top+r.height/2;
   if(r.width<3||r.height<3||r.width>130||r.height>75)continue;
   if(args.hour?x>=mid:x<=mid)continue;
   if(y<b.top+5||y>b.bottom-5)continue;
   nums.push({e,n:Number(t),y});
 }
 const candidates=nums.filter(n=>n.n===args.value);
 candidates.sort((a,b)=>Math.abs(a.y-(b.top+b.height/2))-
  Math.abs(b.y-(b.top+b.height/2)));
 if(candidates.length)candidates[0].e.setAttribute('data-zerovideo-option','true');
 return {found:candidates.length>0,
  min:nums.length?Math.min(...nums.map(x=>x.n)):-1,
  max:nums.length?Math.max(...nums.map(x=>x.n)):-1,
  count:nums.length};
}";
        for(var i=0;i<22;i++)
        {
            ct.ThrowIfCancellationRequested();
            var probe=await popup.EvaluateAsync<System.Text.Json.JsonElement>(
                inspect,new {value,hour});
            if(probe.GetProperty("found").GetBoolean())
            {
                var marked=popup.Locator("[data-zerovideo-option='true']");
                if(await marked.CountAsync()!=1)return false;
                await marked.ClickAsync(new(){Timeout=5000});
                log?.Invoke("TikTok: selected "+(hour?"hour ":"minute ")+value+
                    " in isolated popup.");
                return true;
            }
            var count=probe.GetProperty("count").GetInt32();
            if(count==0){log?.Invoke("TikTok: no values in "+(hour?"hour":"minute")+" column.");return false;}
            var min=probe.GetProperty("min").GetInt32();
            var max=probe.GetProperty("max").GetInt32();
            var direction=value<min?-1:1;
            if(value>max)direction=1;
            // Scrolling only inside verified popup. No click outside it.
            var bb=await popup.BoundingBoxAsync();
            if(bb is null)return false;
            await page.Mouse.MoveAsync(bb.X+bb.Width*(hour?.25f:.75f),
                bb.Y+bb.Height*.5f);
            await page.Mouse.WheelAsync(0,direction*130);
            await Task.Delay(180,ct);
        }
        log?.Invoke("TikTok: "+(hour?"hour":"minute")+" "+value+
            " not reachable safely in time popup.");
        return false;
    }

    private static async Task<bool> SetTikTokScheduleAsync(
        IPage page,DateTimeOffset slot,Action<string>? log,CancellationToken ct,
        bool laterVideo = false)
    {
        var target=slot.LocalDateTime;
        var culture=System.Globalization.CultureInfo.GetCultureInfo("en-US");
        log?.Invoke("TikTok: scheduler path=" +
            (laterVideo ? "subsequent-batch-video" : "first-or-standalone-video") +
            "; requested=" + target.ToString("yyyy-MM-dd HH:mm") + ".");
        if (laterVideo)
            log?.Invoke("TikTok: subsequent-video scheduling requested=" +
                target.ToString("yyyy-MM-dd HH:mm") + "; days ahead=" +
                (target - DateTime.Now).TotalDays.ToString("F1",
                    System.Globalization.CultureInfo.InvariantCulture) +
                ". The first-video schedule code is unchanged.");
        if(target<=DateTime.Now.AddMinutes(20)||target.Minute%5!=0)
        {
            log?.Invoke("TikTok: invalid schedule; must be future and 5-minute interval.");
            return false;
        }
        var caption=page.Locator("[contenteditable=true]").First;
        if(await caption.CountAsync()>0&&await caption.IsVisibleAsync())
            await caption.PressAsync("Tab");
        await page.Keyboard.PressAsync("Escape");
        await page.GetByText("Schedule",new(){Exact=true}).First.ClickAsync();
        await Task.Delay(450,ct);
        log?.Invoke("TikTok: strictly setting "+target.ToString("yyyy-MM-dd HH:mm")+
            "; privacy and music controls must remain untouched.");

        var dateTrigger=page.Locator(".date-picker-input:visible").First;
        if (laterVideo)
        {
            // Diagnostics from the failing second video show that the old
            // .date-picker-input selector is not visible. Never click an
            // arbitrary matching text node: it may not open the calendar.
            var matchedTrigger=await LocateLaterTikTokDateTriggerAsync(page,log);
            if (matchedTrigger is null) return false;
            dateTrigger=matchedTrigger;
        }
        else if(await dateTrigger.CountAsync()==0)
        {
            dateTrigger=page.GetByText(new System.Text.RegularExpressions.Regex(
                @"[A-Za-z]{3}\s+\d{1,2},?\s+\d{4}")).First;
        }
        if(await dateTrigger.CountAsync()==0||!await dateTrigger.IsVisibleAsync())
        {
            log?.Invoke("TikTok: date trigger not available.");return false;
        }
        bool dateOk;
        if (laterVideo)
        {
            // Go directly to the scoped recovery for video #2 onward.
            // SetTikTokDateStrictAsync has an immediate panel check that can
            // miss a newly mounted popup. Video #1 stays unchanged.
            dateOk = await RecoverTikTokDateForLaterVideoAsync(
                page, dateTrigger, target, log, ct);
            if(!dateOk)
                await SaveTikTokLaterDateContextAsync(page,"Calendar-Not-Opened",log);
        }
        else
        {
            // Keep the first video's known-working date path untouched.
            dateOk = await SetTikTokDateStrictAsync(page,dateTrigger,target,log,ct);
        }
        if(!dateOk)
        {
            await SaveTikTokPickerEvidenceAsync(page,"Date-Failed",log);
            log?.Invoke("TikTok: date FAILED — time picker left untouched; " +
                "final Schedule/Post will NOT be clicked.");
            return false;
        }

        // The date was verified. Only now touch the dedicated time control.
        var timeTrigger=page.Locator(".time-picker-input:visible").First;
        if(await timeTrigger.CountAsync()==0)
            timeTrigger=page.GetByText(new System.Text.RegularExpressions.Regex(
                @"^\s*\d{1,2}:\d{2}\s*$")).First;
        var timeOk=false;
        if(await timeTrigger.CountAsync()>0&&await timeTrigger.IsVisibleAsync())
        {
            var current=(await timeTrigger.InnerTextAsync()).Trim();
            var expected=target.ToString("HH:mm");
            timeOk=current==expected||current==target.ToString("H:mm");
            if(!timeOk)
            {
                await page.Keyboard.PressAsync("Escape");
                await timeTrigger.ClickAsync(new(){Timeout=6000});
                await Task.Delay(350,ct);
                await SaveTikTokPickerEvidenceAsync(page,"Time-Open",log);
                var popup=await GetTikTokTimePopupAsync(page,timeTrigger,log);
                if(popup is not null)
                {
                    var hour=await SelectTikTokTimeColumnAsync(
                        page,popup,target.Hour,true,log,ct);
                    var minute=await SelectTikTokTimeColumnAsync(
                        page,popup,target.Minute,false,log,ct);
                    log?.Invoke("TikTok: time option results hour="+hour+" minute="+minute);
                    // Toggle ONLY the dedicated time trigger to close popup;
                    // never click anywhere else on the upload form.
                    await timeTrigger.ClickAsync(new(){Timeout=6000});
                    await Task.Delay(350,ct);
                }
                current=(await timeTrigger.InnerTextAsync()).Trim();
                timeOk=current==expected||current==target.ToString("H:mm");
                log?.Invoke("TikTok: time requested="+expected+" displayed="+current+
                    " verified="+timeOk);
                if(!timeOk)await SaveTikTokPickerEvidenceAsync(page,"Time-Failed",log);
            }
        }
        if(!dateOk||!timeOk)
        {
            log?.Invoke("TikTok: schedule NOT set. date="+dateOk+" time="+timeOk);
            return false;
        }
        if (laterVideo)
        {
            // TikTok can reset date after the time popup closes. Check only
            // later videos, preserving the original first-video submit path.
            var finalDate = await ReadTikTokClosedDateAsync(dateTrigger);
            await LogTikTokDateReadbackAsync(dateTrigger, target, log);
            if (!TikTokSubsequentDatePolicy.MatchesDisplayedDate(finalDate, target))
            {
                log?.Invoke("TikTok: later-video date was reset while selecting time; " +
                    "trying its own calendar again.");
                if (!await RecoverTikTokDateForLaterVideoAsync(
                        page, dateTrigger, target, log, ct))
                    return false;

                var verifiedTime = (await timeTrigger.InnerTextAsync()).Trim();
                var expectedTime = target.ToString("HH:mm");
                if (verifiedTime != expectedTime &&
                    verifiedTime != target.ToString("H:mm"))
                {
                    log?.Invoke("TikTok: later-video time changed while recovering date; " +
                        "refusing to submit an incorrect schedule.");
                    await SaveTikTokPickerEvidenceAsync(page,
                        "Later-Time-Changed-After-Date", log);
                    return false;
                }
            }

            log?.Invoke("TikTok: later-video date confirmed before final Schedule. " +
                "Requested=" + target.ToString("yyyy-MM-dd") +
                "; displayed='" + await ReadTikTokClosedDateAsync(dateTrigger) + "'.");
        }
        log?.Invoke("TikTok: date AND time fields verified. Final Schedule is manual.");
        return true;
    }


    // TikTok can open this secondary content-check confirmation AFTER the
    // footer Schedule/Post action. Scope the action to its own two-button modal.
    // A submitted click is never retried, even if page navigation loses the
    // browser response.
    private static async Task<int> TryConfirmTikTokContinueToPostAsync(
        IPage page, Action<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Check that the exact prompt is visible before searching for a button.
        var heading = page.GetByText("Continue to post?", new() { Exact = true });
        if (await heading.CountAsync() == 0 || !await heading.First.IsVisibleAsync())
            return 0;

        // Mark only the actionable button inside the SMALLEST visible modal
        // containing this heading, the ongoing-check warning and exactly
        // two footer actions: Cancel and Post now.
        var found = await page.EvaluateAsync<bool>(@"
() => {
  const marker = 'data-zerovideo-continue-post-button';
  document.querySelectorAll('['+marker+']').forEach(e=>e.removeAttribute(marker));
  const visible = e => {
    const r=e.getBoundingClientRect(),s=getComputedStyle(e);
    return r.width>2 && r.height>2 && s.visibility!=='hidden' &&
           s.display!=='none' && r.bottom>0 && r.top<innerHeight;
  };
  const clean = e => (e.innerText||e.textContent||'').replace(/\s+/g,' ').trim();
  const headings=[...document.querySelectorAll('body *')].filter(e =>
      visible(e) && clean(e)==='Continue to post?' &&
      ![...e.children].some(c=>clean(c)==='Continue to post?'));
  const valid=[];
  for(const h of headings){
    for(let p=h.parentElement,depth=0;p&&depth<9;p=p.parentElement,depth++){
      const r=p.getBoundingClientRect();
      if(r.width<270 || r.width>820 || r.height<130 || r.height>530)continue;
      const buttons=[...p.querySelectorAll('button')].filter(visible);
      if(buttons.length!==2)continue;
      const cancel=buttons.filter(b=>/^Cancel$/i.test(clean(b)));
      const post=buttons.filter(b=>/^Post now$/i.test(clean(b)));
      if(cancel.length!==1 || post.length!==1)continue;
      const all=clean(p);
      if(!/still checking your video|check is complete|potential issues|copyright check is incomplete|posting your video now will stop the check/i.test(all))
        continue;
      // This is a modal in the central part of the viewport, never the
      // surrounding scheduling/privacy/copyright settings.
      const centerX=r.left+r.width/2,centerY=r.top+r.height/2;
      if(centerX<innerWidth*.2 || centerX>innerWidth*.8 ||
         centerY<innerHeight*.15 || centerY>innerHeight*.85)continue;
      valid.push({button:post[0],area:r.width*r.height});
    }
  }
  valid.sort((a,b)=>a.area-b.area);
  if(!valid.length)return false;
  const choice=valid[0].button;
  if(valid.some(v=>v.button!==choice && v.area===valid[0].area))return false;
  choice.setAttribute(marker,'yes');
  return true;
}");
        if(!found)
        {
            log?.Invoke("TikTok: Continue to post? detected but matching " +
                "two-button confirmation could not be safely identified.");
            await SaveDiagnosticAsync(page,"TikTok-Continue-Post-Unrecognized",log);
            return -1;
        }
        var action=page.Locator("button[data-zerovideo-continue-post-button='yes']");
        if(await action.CountAsync()!=1 ||
           !await action.IsVisibleAsync() || !await action.IsEnabledAsync())
        {
            log?.Invoke("TikTok: Continue to post? confirmation disabled or ambiguous.");
            return -1;
        }
        log?.Invoke("TikTok: Continue to post? content check still running. " +
            "Clicking its modal Post now button ONCE, as authorized.");
        try
        {
            await action.ClickAsync(new(){Timeout=8000,NoWaitAfter=true});
            return 1;
        }
        catch(Exception ex) when(ex is PlaywrightException or TimeoutException)
        {
            log?.Invoke("TikTok: second confirmation was attempted; result uncertain: "+
                ex.Message.Split((char)10)[0]);
            await SaveDiagnosticAsync(page,"TikTok-Continue-Post-Click-Uncertain",log);
            return -1;
        }
    }

    // A single final action: NEVER retry a clicked Schedule/Post button.
    // If TikTok doesn't clearly confirm receipt, flag an ambiguous submission
    // and require the user to inspect TikTok Studio before attempting again.
    private static async Task<string> SubmitTikTokOnceAsync(
        IPage page, bool scheduled, Action<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Uri.TryCreate(page.Url, UriKind.Absolute, out var address) ||
            !(address.Host.Equals("www.tiktok.com", StringComparison.OrdinalIgnoreCase) ||
              address.Host.Equals("tiktok.com", StringComparison.OrdinalIgnoreCase)) ||
            !address.AbsolutePath.Contains("tiktokstudio/upload", StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke("TikTok: refused submission outside TikTok Studio upload page.");
            return "TikTok: submission NOT attempted; browser is not on TikTok Studio upload.";
        }

        // The upload footer's stable data-e2e selector is more specific than
        // the Schedule radio label and avoids privacy/music controls.
        var button = page.Locator("button[data-e2e='post_video_button']:visible");
        var count = await button.CountAsync();
        if (count != 1)
        {
            // Only a single real button (never a generic text node, label,
            // radio or page coordinate) can serve as fallback.
            button = page.GetByRole(AriaRole.Button, new()
            {
                NameRegex = new System.Text.RegularExpressions.Regex(
                    scheduled ? @"^\s*Schedule\s*$" :
                    @"^\s*(Post|Publish|Post now)\s*$",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            });
            count = await button.CountAsync();
        }
        if (count != 1)
        {
            log?.Invoke("TikTok: final " + (scheduled ? "Schedule" : "Post") +
                " button missing/ambiguous (matches=" + count + "). No submission attempted.");
            await SaveTikTokPickerEvidenceAsync(page, "Final-Button-Missing", log);
            return "TikTok: submission NOT attempted; final button missing or ambiguous. Review in Studio.";
        }

        var submit = button.First;
        var label = (await submit.InnerTextAsync()).Trim();
        var validLabel = scheduled
            ? System.Text.RegularExpressions.Regex.IsMatch(label,
                @"^\s*(Schedule|Post|Publish)\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            : System.Text.RegularExpressions.Regex.IsMatch(label,
                @"^\s*(Post|Publish|Post now)\s*$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!validLabel || !await submit.IsVisibleAsync() ||
            !await submit.IsEnabledAsync() ||
            (await submit.GetAttributeAsync("aria-disabled")) == "true" ||
            (await submit.GetAttributeAsync("data-disabled")) == "true")
        {
            log?.Invoke("TikTok: final action disabled or unexpected label: '" + label +
                "'. No submission attempted.");
            return "TikTok: submission NOT attempted; final " +
                (scheduled ? "Schedule" : "Post") + " button not enabled.";
        }

        // The button must be the genuine upload CTA, not the scheduling radio:
        // require its data-e2e selector OR its exact accessible name.
        var identity = await submit.GetAttributeAsync("data-e2e");
        if (identity != "post_video_button" && scheduled &&
            !label.Equals("Schedule", StringComparison.OrdinalIgnoreCase))
        {
            log?.Invoke("TikTok: refused ambiguous non-CTA button: " + label);
            return "TikTok: submission NOT attempted; final Schedule button not verified.";
        }

        log?.Invoke("TikTok: verified date/time and upload readiness; clicking final '" +
            label + "' button ONCE. Do not retry automatically.");
        // Mark the attempt BEFORE dispatching the click. If browser navigation
        // interrupts Playwright, the outcome is unknown, not safe to retry.
        var attempted = false;
        try
        {
            attempted = true;
            await submit.ClickAsync(new() { Timeout = 8000, NoWaitAfter = true });
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            log?.Invoke("TikTok: final button click was attempted; outcome uncertain: " +
                ex.Message.Split((char)10)[0]);
            await SaveDiagnosticAsync(page, "TikTok-Submit-Outcome-Uncertain", log);
            return "TikTok: SUBMISSION_ATTEMPTED; result uncertain. Check TikTok Studio Posts before retrying.";
        }

        if (!attempted) return "TikTok: submission NOT attempted.";
        // Only trust a post/schedule success message or the upload page
        // navigating away. An ambiguous outcome is NEVER a confirmed post.
        var originalUrl = address.AbsolutePath.TrimEnd('/');
        var confirmation = page.GetByText(new System.Text.RegularExpressions.Regex(
            scheduled
                ? @"(?:successfully\s+scheduled|scheduled\s+successfully|post\s+(?:has\s+been\s+)?scheduled|video\s+(?:has\s+been\s+)?scheduled|schedule\s+successful)"
                : @"(?:your\s+video\s+has\s+been\s+uploaded|video\s+published|successfully\s+posted|post\s+published|video\s+uploaded\s+successfully)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase));
        var continuePostClicked = false;
        for (var i = 0; i < 30; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                // Only after the verified primary Schedule/Post click.
                // Confirm the content-check dialog separately, once.
                if (!continuePostClicked)
                {
                    var result = await TryConfirmTikTokContinueToPostAsync(page,log,ct);
                    if(result < 0)
                        return "TikTok: SUBMISSION_ATTEMPTED; Continue to post? " +
                            "could not be confirmed safely. Check TikTok Studio Posts before retrying.";
                    continuePostClicked = result == 1;
                }
                if (await confirmation.CountAsync() > 0 && await confirmation.First.IsVisibleAsync())
                {
                    log?.Invoke("TikTok: displayed an explicit confirmation after final submission.");
                    return "TikTok: SUBMISSION_CONFIRMED; " +
                        (scheduled ? "video scheduled" : "video published") +
                        " in TikTok Studio.";
                }
                if (Uri.TryCreate(page.Url, UriKind.Absolute, out var now) &&
                    now.Host.EndsWith("tiktok.com", StringComparison.OrdinalIgnoreCase) &&
                    now.AbsolutePath.TrimEnd('/') != originalUrl &&
                    !now.AbsolutePath.Contains("/upload", StringComparison.OrdinalIgnoreCase))
                {
                    log?.Invoke("TikTok: navigated away from the upload form after submission, " +
                        "but post status has not been independently confirmed.");
                    return "TikTok: SUBMISSION_ATTEMPTED; TikTok left the upload page. Verify the post in Studio.";
                }
            }
            catch (PlaywrightException)
            {
                // Navigation can temporarily invalidate the old page. Do not
                // click again or interpret the exception as a failed post.
            }
            await Task.Delay(900, ct);
        }
        log?.Invoke("TikTok: final button was clicked once; no definitive receipt " +
            "was detected. Check TikTok Studio Posts before retrying.");
        await SaveDiagnosticAsync(page, "TikTok-Submit-Confirmation-Unknown", log);
        return "TikTok: SUBMISSION_ATTEMPTED; confirmation unavailable. Check TikTok Studio Posts before retrying.";
    }

    public async Task<string> PrepareAsync(ZeroVideoSettings settings, string destination,
        string videoPath, string title, string description, string caption, string thumbnailPath,
        DateTimeOffset? scheduledAt, Action<string>? log = null, CancellationToken ct = default,
        bool freshTikTokUploadPage = false)
    {
        if (!File.Exists(videoPath)) throw new FileNotFoundException("Video is missing.", videoPath);
        if (new FileInfo(videoPath).Length == 0) throw new IOException("Video is empty.");
        await browser.EnsureStartedAsync(settings);
        var youtube = destination == "YouTube";
        // Preserve the historically working first-upload path EXACTLY.
        // Only later videos in the same batch need a fresh TikTok upload
        // tab, to avoid the previous video's calendar state being reused.
        var page = !youtube && freshTikTokUploadPage
            ? await browser.Context.NewPageAsync()
            : await browser.GetOrCreatePageAsync(youtube ? "studio.youtube.com" : "tiktok.com");
        await page.GotoAsync(youtube ? "https://studio.youtube.com/" : "https://www.tiktok.com/tiktokstudio/upload",
            new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 90000 });
        if (!youtube && freshTikTokUploadPage)
            log?.Invoke("TikTok: opened a fresh upload tab for the NEXT queued video; the first video's working publish flow remains unchanged.");
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
            if (!filled)
                return "TikTok: caption entry FAILED. No post was submitted; review in TikTok Studio.";
            log?.Invoke("TikTok: caption entered; automatic final submission authorized in ZeroVideo.");
            try
            {
                if (!await WaitForTikTokUploadReadyAsync(page, log, ct))
                    return "TikTok: upload still processing; final submission NOT attempted.";
                if (freshTikTokUploadPage && scheduledAt.HasValue)
                {
                    // The subsequent upload can still be replacing its React
                    // form as the file finishes loading; do not select its
                    // calendar before the controls have settled.
                    await Task.Delay(1500, ct);
                }
                if (scheduledAt.HasValue &&
                    !await SetTikTokScheduleAsync(page, scheduledAt.Value, log, ct,
                        laterVideo: freshTikTokUploadPage))
                    return "TikTok: requested date/time NOT verified. Final submission NOT attempted.";
                // Only reach the final button after all required checks passed.
                return await SubmitTikTokOnceAsync(page, scheduledAt.HasValue, log, ct);
            }
            catch (Exception ex) when (ex is PlaywrightException or TimeoutException or InvalidOperationException)
            {
                var screenshot = await SaveDiagnosticAsync(page, "TikTok-Schedule", log);
                log?.Invoke("TikTok: preparation failed before final submission: " +
                    ex.Message + ". Screenshot: " + screenshot);
                return "TikTok: preparation FAILED; final submission NOT attempted: " + ex.Message;
            }
        }
        return destination + ": video selected in Studio. Review upload completion, metadata, thumbnail and " +
            "schedule/privacy before manually confirming. No post has been submitted.";
    }
}
