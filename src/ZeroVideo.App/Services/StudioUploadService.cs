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
 '.tiktok-timepicker-time-picker-container','.TimePicker__panel']
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



    // Capture ONLY the date-trigger neighborhood, not an entire page with
    // user/session data. The earlier Date-Panel-Missing files logged only
    // popup HTML and could not explain why the trigger did not open it.
    private static async Task SaveTikTokDateTriggerDiagnosticAsync(
        IPage page, ILocator dateTrigger, string stage, DateTime target,
        Action<string>? log, bool subsequent)
    {
        try
        {
            const string script = """
                element => {
                  const visible=n=>{
                    const r=n.getBoundingClientRect();
                    return r.width>0 && r.height>0;
                  };
                  const format=n=>n ? {
                    tag:n.tagName,
                    className:typeof n.className==='string'?n.className:'',
                    role:n.getAttribute('role'),
                    ariaLabel:n.getAttribute('aria-label'),
                    text:(n.innerText||'').slice(0,200),
                    markup:n.outerHTML.slice(0,1700),
                    visible:visible(n)
                  } : null;
                  return JSON.stringify({
                    url:location.pathname,
                    trigger:format(element),
                    parent:format(element.parentElement),
                    grandparent:format(element.parentElement?.parentElement),
                    calendarPanels:[...document.querySelectorAll('.DatePicker__panel')]
                      .map(n=>({visible:visible(n),
                        header:(n.querySelector('.DatePicker__header')?.innerText||'').slice(0,100)})),
                    dateCandidates:[...document.querySelectorAll(
                      '.date-picker-input,[class*="DatePicker"],[class*="date-picker"]')]
                      .filter(n=>visible(n)).slice(0,18).map(format)
                  },null,2);
                }
                """;
            var data=await dateTrigger.EvaluateAsync<string>(script);
            var dir=Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
                "ZeroVideo","PublishingDiagnostics");
            Directory.CreateDirectory(dir);
            var path=Path.Combine(dir,(subsequent ? "TikTok-Subsequent-Date-" :
                "TikTok-First-Date-")+stage+"-"+
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+".txt");
            await File.WriteAllTextAsync(path,
                "Requested date: "+target.ToString("yyyy-MM-dd")+"\n"+data);
            log?.Invoke("TikTok: date trigger diagnostic saved: "+path);
        }
        catch(Exception ex)
        {
            log?.Invoke("TikTok: date trigger evidence unavailable: "+
                ex.Message.Split((char)10)[0]);
        }
    }


    private static async Task<ILocator?> FindTikTokDateTriggerWrapperAsync(
        IPage page, ILocator dateTrigger, Action<string>? log)
    {
        const string script = """
            element => {
              document.querySelectorAll('[data-zerovideo-date-wrapper]')
                .forEach(n=>n.removeAttribute('data-zerovideo-date-wrapper'));
              const visible=n=>{
                const r=n.getBoundingClientRect(),css=getComputedStyle(n);
                return r.width>30 && r.height>8 && css.visibility!=='hidden' &&
                  css.display!=='none';
              };
              // The text node can be a non-clickable child of a calendar
              // control. Search only its own ancestors, never other inputs.
              let p=element;
              for(let d=0;p && d<=5;p=p.parentElement,d++){
                if(!visible(p))continue;
                const cls=typeof p.className==='string'?p.className.toLowerCase():'';
                const role=(p.getAttribute('role')||'').toLowerCase();
                const label=(p.getAttribute('aria-label')||'').toLowerCase();
                const attr=(p.getAttribute('data-testid')||'').toLowerCase();
                const dateHint=/date|calendar/.test(cls+' '+label+' '+attr);
                const interactive=p.matches('button,input,[role="button"],[role="combobox"],[tabindex]');
                // Never climb into generic layout / publishing controls.
                if(!dateHint || (!interactive && !/picker|input/.test(cls)))continue;
                const bounds=p.getBoundingClientRect();
                if(bounds.width>650 || bounds.height>150)continue;
                // The fallback must target a date widget, not privacy,
                // copyright checks or the final upload button.
                p.setAttribute('data-zerovideo-date-wrapper','true');
                return {found:true,tag:p.tagName,className:cls.slice(0,100)};
              }
              return {found:false};
            }
            """;
        try
        {
            var result=await dateTrigger.EvaluateAsync<System.Text.Json.JsonElement>(script);
            if(!result.GetProperty("found").GetBoolean())
            {
                log?.Invoke("TikTok: date-specific clickable wrapper not found; no unsafe fallback click.");
                return null;
            }
            log?.Invoke("TikTok: retrying only date wrapper " +
                result.GetProperty("tag").GetString() + " class='" +
                result.GetProperty("className").GetString() + "'.");
            var candidate=page.Locator("[data-zerovideo-date-wrapper='true']");
            return await candidate.CountAsync()==1 && await candidate.IsVisibleAsync()
                ? candidate : null;
        }
        catch(PlaywrightException ex)
        {
            log?.Invoke("TikTok: date wrapper changed during recovery: " +
                ex.Message.Split((char)10)[0]);
            return null;
        }
    }

    /// <summary>
    /// Find ONLY TikTok's scheduling date widget. The previous page-wide
    /// GetByText(date regex) matched "February 9, 2004" inside the VIDEO
    /// DESCRIPTION when the schedule controls weren't mounted. Never search
    /// the caption or arbitrary page text for a date.
    /// </summary>

    // TikTok periodically renders the schedule controls without the old
    // .date-picker-input / .time-picker-input classes. Identify a visible
    // date/time value ONLY beneath the explicit "When to post" heading.
    // Never search the caption, video description, sidebar, or the full page
    // for a date to click. If ambiguous, stop before final submission.
    private static async Task<ILocator?> FindTikTokScheduleFieldNearHeadingAsync(
        IPage page, bool date, Action<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var field = date ? "date" : "time";
        var result = await page.EvaluateAsync<System.Text.Json.JsonElement>(
            """
            kind => {
              const marker='data-zerovideo-schedule-'+kind+'-trigger';
              document.querySelectorAll('['+marker+']').forEach(e=>e.removeAttribute(marker));
              const visible=e=>{
                if(!e)return false;
                const r=e.getBoundingClientRect(),s=getComputedStyle(e);
                return r.width>4 && r.height>6 && r.top>=0 && r.bottom<=innerHeight+40 &&
                  s.display!=='none' && s.visibility!=='hidden';
              };
              const text=e=>(e.value||e.innerText||e.textContent||'').trim()
                .replace(/\s+/g,' ');
              const heads=[...document.querySelectorAll('h1,h2,h3,h4,h5,div,span,label,strong')]
                .filter(e=>visible(e)&&text(e)==='When to post'&&
                  ![...e.children].some(c=>text(c)==='When to post'));
              if(heads.length!==1)return {found:false,reason:'When to post heading absent/ambiguous'};
              const head=heads[0].getBoundingClientRect();
              const regex=kind==='date'
                ?/^(?:\d{4}-\d{2}-\d{2}|\d{1,2}\/\d{1,2}\/\d{4}|[A-Za-z]{3,9}\s+\d{1,2},?\s+\d{4})$/
                :/^\d{1,2}:\d{2}(?:\s*[AP]M)?$/i;
              const nodes=[...document.querySelectorAll('input,button,[role="button"],[role="combobox"],span,div')];
              const leaves=nodes.filter(e=>{
                if(!visible(e)||!regex.test(text(e)))return false;
                if(e.closest('[contenteditable="true"],[role="textbox"],[data-e2e="post_video_button"]'))
                  return false;
                const r=e.getBoundingClientRect();
                if(r.top<head.bottom-5 || r.top>head.bottom+175)return false;
                // Do not allow a large container/description/sidebar as a target.
                if(r.width>400 || r.height>90)return false;
                return ![...e.children].some(c=>visible(c)&&text(c)===text(e));
              });
              const choices=[];
              for(const e of leaves){
                let chosen=e, best=0;
                for(let a=e,depth=0;a&&depth<4;a=a.parentElement,depth++){
                  const r=a.getBoundingClientRect();
                  if(r.width<65||r.width>360||r.height<22||r.height>70)continue;
                  if(r.top<head.bottom-6||r.top>head.bottom+175)continue;
                  if(a.closest('[contenteditable="true"],[role="textbox"]'))continue;
                  const cls=typeof a.className==='string'?a.className.toLowerCase():'';
                  const isButton=a.matches('button,input,[role="button"],[role="combobox"],[tabindex]');
                  const dateHint=/date|calendar/.test(cls+' '+(a.getAttribute('aria-label')||''));
                  const timeHint=/time/.test(cls+' '+(a.getAttribute('aria-label')||''));
                  const score=(isButton?12:0)+(kind==='date'&&dateHint?20:0)+
                    (kind==='time'&&timeHint?20:0)+(r.height>=30?3:0)-depth;
                  if(score>best){best=score;chosen=a;}
                }
                choices.push({el:chosen,label:text(e),area:chosen.getBoundingClientRect().width*
                    chosen.getBoundingClientRect().height});
              }
              const unique=[];
              for(const c of choices)if(!unique.some(v=>v.el===c.el))unique.push(c);
              if(unique.length!==1)return {found:false,
                reason:'Expected one '+kind+' field, found '+unique.length};
              unique[0].el.setAttribute(marker,'true');
              return {found:true,label:unique[0].label};
            }
            """, field);
        if (!result.GetProperty("found").GetBoolean())
        {
            log?.Invoke("TikTok: scoped " + field + " fallback unavailable: " +
                result.GetProperty("reason").GetString());
            return null;
        }
        var locator = page.Locator("[data-zerovideo-schedule-" + field + "-trigger='true']");
        if (await locator.CountAsync() != 1 || !await locator.First.IsVisibleAsync())
            return null;
        log?.Invoke("TikTok: schedule " + field + " identified under When to post: '" +
            result.GetProperty("label").GetString() + "'.");
        return locator.First;
    }

    // Some TikTok accounts render the newer "When to post" toolbar as
    // three adjacent controls: [calendar date] [clock time] [GMT timezone].
    // Identify ONLY the date/time control beside that exact heading; do not
    // interact with privacy, caption, or timezone dropdowns.
    private static async Task<ILocator?> FindTikTokNewScheduleToolbarFieldAsync(
        IPage page, bool date, Action<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var kind=date?"date":"time";
        var result=await page.EvaluateAsync<System.Text.Json.JsonElement>(
            """
            kind=>{
              const marker='data-zerovideo-schedule-'+kind+'-trigger';
              const visible=e=>{
                if(!e)return false;
                const r=e.getBoundingClientRect(),c=getComputedStyle(e);
                return r.width>10&&r.height>10&&r.top>=0&&r.bottom<innerHeight+50&&
                  c.display!=='none'&&c.visibility!=='hidden';
              };
              const text=e=>(e.value||e.innerText||e.textContent||'')
                .replace(/\s+/g,' ').trim();
              const datePattern=/^(?:(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)[a-z]*\s+\d{1,2},?\s+\d{4}|\d{4}-\d{2}-\d{2})$/i;
              const timePattern=/^\d{1,2}:\d{2}(?:\s*[AP]M)?$/i;
              const pattern=kind==='date'?datePattern:timePattern;
              const heads=[...document.querySelectorAll('h1,h2,h3,h4,span,div,strong')]
                .filter(e=>visible(e)&&text(e)==='When to post'&&
                  ![...e.children].some(c=>text(c)==='When to post'));
              if(heads.length!==1)return {ok:false,reason:'When to post heading ambiguous'};
              const h=heads[0].getBoundingClientRect();
              const nodes=[...document.querySelectorAll('input,button,[role="button"],[role="combobox"],span,div')]
                .filter(e=>{
                  if(!visible(e)||!pattern.test(text(e))||
                    e.closest('[contenteditable="true"],[role="textbox"],[data-e2e="post_video_button"]'))return false;
                  const r=e.getBoundingClientRect();
                  return r.top>=h.bottom-5&&r.top<=h.bottom+180&&
                    r.left>=h.left-25&&r.width<=340&&r.height<=85&&
                    ![...e.children].some(c=>visible(c)&&text(c)===text(e));
                });
              const boxes=[];
              for(const leaf of nodes) {
                let chosen=leaf;
                // Prefer the clickable enclosing tile rather than the icon
                // or text span. Keep the tile short enough not to include
                // the adjacent GMT+8 selector.
                for(let p=leaf.parentElement,depth=0;p&&depth<4;p=p.parentElement,depth++){
                  const r=p.getBoundingClientRect();
                  if(r.width>360||r.width<90||r.height>75||r.height<30)continue;
                  if(r.top<h.bottom-8||r.top>h.bottom+180)continue;
                  if(/GMT|Local Time/i.test(text(p)))continue;
                  chosen=p;
                  if(p.matches('button,[role="button"],[role="combobox"],[tabindex]')||
                    /picker|select|input/i.test(typeof p.className==='string'?p.className:''))break;
                }
                const rect=chosen.getBoundingClientRect();
                boxes.push({el:chosen,center:rect.left+rect.width/2,label:text(leaf)});
              }
              const unique=[];
              for(const b of boxes)if(!unique.some(x=>Math.abs(x.center-b.center)<24))unique.push(b);
              if(unique.length!==1)return {ok:false,reason:'Expected one '+kind+' toolbar tile, found '+unique.length};
              document.querySelectorAll('['+marker+']').forEach(e=>e.removeAttribute(marker));
              unique[0].el.setAttribute(marker,'true');
              return {ok:true,label:unique[0].label};
            }
            """,kind);
        if(!result.GetProperty("ok").GetBoolean())
        {
            log?.Invoke("TikTok: new toolbar "+kind+" locator: "+
                result.GetProperty("reason").GetString());
            return null;
        }
        var marked=page.Locator("[data-zerovideo-schedule-"+kind+"-trigger='true']");
        if(await marked.CountAsync()!=1||!await marked.IsVisibleAsync())return null;
        log?.Invoke("TikTok: selected "+kind+" field in new When to post toolbar: "+
            result.GetProperty("label").GetString());
        return marked.First;
    }

    private static async Task<ILocator?> FindTikTokScheduleDateTriggerAsync(
        IPage page, Action<string>? log, CancellationToken ct)
    {
        // Prefer the newly positioned account-specific date tile; it can
        // coexist with hidden legacy date widgets elsewhere in the document.
        var newToolbar=await FindTikTokNewScheduleToolbarFieldAsync(
            page,true,log,ct);
        if(newToolbar is not null)return newToolbar;
        var preferred = page.Locator(".date-picker-input:visible");
        for(var poll=0;poll<20;poll++)
        {
            ct.ThrowIfCancellationRequested();
            if(await preferred.CountAsync()==1 &&
                await preferred.First.IsVisibleAsync())
            {
                log?.Invoke("TikTok: date trigger resolved through dedicated " +
                    ".date-picker-input control.");
                return preferred.First;
            }
            if(poll<19) await Task.Delay(250,ct);
        }

        // TikTok sometimes changes the date widget's wrapper class.
        // These alternatives require explicit DATE/CALENDAR semantics,
        // not incidental dates in captions, titles, hashtags or descriptions.
        const string inspect = """
            () => {
              document.querySelectorAll('[data-zerovideo-schedule-date-trigger]')
                .forEach(n=>n.removeAttribute('data-zerovideo-schedule-date-trigger'));
              const visible=n=>{
                const r=n.getBoundingClientRect(),css=getComputedStyle(n);
                return r.width>35 && r.height>10 &&
                    css.display!=='none' && css.visibility!=='hidden';
              };
              const valid=n=>!n.closest('[contenteditable="true"],[role="textbox"]') &&
                !n.matches('[contenteditable="true"]') &&
                !n.closest('[data-e2e="post_video_button"]');
              const scored=[];
              for(const el of document.querySelectorAll(
                'input,button,[role="button"],[role="combobox"],[class*="date-picker"],[class*="DatePicker"]')) {
                if(!visible(el) || !valid(el))continue;
                const cls=typeof el.className==='string'?el.className.toLowerCase():'';
                const hint=[cls,el.getAttribute('aria-label')||'',
                  el.getAttribute('placeholder')||'',
                  el.getAttribute('data-testid')||''].join(' ').toLowerCase();
                if(!/date|calendar/.test(hint) || /time|privacy|music|copyright/.test(hint))
                    continue;
                const rect=el.getBoundingClientRect();
                if(rect.width>750 || rect.height>135)continue;
                const text=(el.innerText||el.value||'').trim();
                if(text.length>85)continue;
                const interact=el.matches('input,button,[role="button"],[role="combobox"],[tabindex]');
                const score=(cls.includes('date-picker-input')?100:0) +
                  (/date|calendar/.test(el.getAttribute('aria-label')||'')?25:0) +
                  (interact?16:0) + (/picker|input/.test(cls)?5:0);
                scored.push({el,score,tag:el.tagName,cls:cls.slice(0,110),
                  text:text.slice(0,80)});
              }
              scored.sort((a,b)=>b.score-a.score);
              if(!scored.length)return {found:false,reason:'No visible date-labeled scheduling widget'};
              const top=scored[0];
              const unrelated=scored.filter(x=>x.score===top.score &&
                x.el!==top.el && !x.el.contains(top.el) && !top.el.contains(x.el));
              if(unrelated.length)return {found:false,
                reason:'Ambiguous date controls at the same confidence'};
              top.el.setAttribute('data-zerovideo-schedule-date-trigger','true');
              return {found:true,tag:top.tag,cls:top.cls,text:top.text};
            }
            """;
        var found=await page.EvaluateAsync<System.Text.Json.JsonElement>(inspect);
        if(!found.GetProperty("found").GetBoolean())
        {
            var scoped = await FindTikTokScheduleFieldNearHeadingAsync(
                page, date: true, log, ct);
            if(scoped is not null) return scoped;
            log?.Invoke("TikTok: date widget NOT available after Schedule activation: " +
                found.GetProperty("reason").GetString() +
                ". Will not click dates in the video caption.");
            await SaveDiagnosticAsync(page,"TikTok-Date-Widget-Unavailable",log);
            return null;
        }
        var marked=page.Locator("[data-zerovideo-schedule-date-trigger='true']");
        if(await marked.CountAsync()!=1 || !await marked.IsVisibleAsync())
        {
            log?.Invoke("TikTok: date widget rerendered during identification.");
            return null;
        }
        log?.Invoke("TikTok: confirmed dedicated date control " +
            found.GetProperty("tag").GetString() + " class='" +
            found.GetProperty("cls").GetString() + "' text='" +
            found.GetProperty("text").GetString() + "'.");
        return marked.First;
    }


    // The November 2026 TikTok calendar is a floating TUX popover rather than
    // the legacy .DatePicker__panel. Select a day only from the unique visible
    // calendar for the requested month, adjacent to the scheduling date input.
    // Do not use GetByText(day) on the page; dates also occur in captions.
    // Match TikTok's current TUX calendar: month header "November / 2026",
    // weekday row and 7-column day grid (not the legacy DatePicker__panel).
    // Only click a day after identifying the unique visible monthly grid and
    // confirming both its text and its weekday column / calendar row.
    private static async Task<bool> SelectTikTokTuxCalendarDayAsync(
        IPage page, ILocator dateTrigger, DateTime target,
        Action<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var result=await page.EvaluateAsync<System.Text.Json.JsonElement>(
            """
            arg => {
              const marker='data-zerovideo-tux-calendar-day';
              document.querySelectorAll('['+marker+']').forEach(e=>e.removeAttribute(marker));
              const visible=e=>{
                const r=e.getBoundingClientRect(),s=getComputedStyle(e);
                return r.width>7&&r.height>7&&r.bottom>0&&r.top<innerHeight&&
                  s.display!=='none'&&s.visibility!=='hidden';
              };
              const t=e=>(e.innerText||e.textContent||'').replace(/\s+/g,' ').trim();
              const input=document.querySelector('[data-zerovideo-schedule-date-trigger]');
              if(!input||!visible(input))return {ok:false,why:'date input missing'};
              const ir=input.getBoundingClientRect();
              const months=['January','February','March','April','May','June',
                'July','August','September','October','November','December'];
              const header=new RegExp('^'+months[arg.month-1]+'\\s*[/, -]?\\s*'+arg.year+'$','i');
              // Require a leaf header, not the whole upload page.
              const headers=[...document.querySelectorAll('div,span,strong,h2,h3')]
                .filter(e=>visible(e)&&header.test(t(e))&&
                  ![...e.children].some(c=>header.test(t(c))));
              const valid=[];
              for(const h of headers){
                for(let p=h.parentElement,depth=0;p&&depth<7;p=p.parentElement,depth++){
                  const r=p.getBoundingClientRect();
                  if(r.width<280||r.width>510||r.height<245||r.height>540)continue;
                  if(r.left>ir.right+100||r.right<ir.left-100||
                     r.top>ir.bottom+20||r.bottom<ir.top-150)continue;
                  const txt=t(p);
                  if(!/Sun\s+Mon\s+Tue\s+Wed\s+Thurs?\s+Fri\s+Sat/i.test(txt))
                    continue;
                  const leaves=[...p.querySelectorAll('button,span,div,[role="gridcell"]')]
                    .filter(n=>visible(n)&&/^(?:[1-9]|[12]\d|3[01])$/.test(t(n))&&
                      ![...n.children].some(c=>visible(c)&&t(c)===t(n)))
                    .map(e=>{const d=e.getBoundingClientRect();
                      return {e,x:d.left+d.width/2,y:d.top+d.height/2,text:t(e)};});
                  // Unique visual cells (some day elements are nested)
                  const cells=[];
                  for(const n of leaves){
                    if(cells.some(c=>Math.abs(c.x-n.x)<8&&Math.abs(c.y-n.y)<8))continue;
                    cells.push(n);
                  }
                  if(cells.length<28||cells.length>42)continue;
                  const ys=[...new Set(cells.map(c=>Math.round(c.y/12)*12))]
                    .sort((a,b)=>a-b);
                  const rows=[];
                  for(const c of cells) {
                    let row=rows.find(x=>Math.abs(x.y-c.y)<13);
                    if(!row){row={y:c.y,cells:[]};rows.push(row)}
                    row.cells.push(c);
                  }
                  rows.sort((a,b)=>a.y-b.y);
                  if(rows.some(row=>row.cells.length!==7))continue;
                  rows.forEach(row=>row.cells.sort((a,b)=>a.x-b.x));
                  const startDay=new Date(arg.year,arg.month-1,1).getDay();
                  const index=startDay+arg.day-1;
                  const row=Math.floor(index/7),col=index%7;
                  const cell=rows[row]?.cells[col];
                  if(!cell||cell.text!==String(arg.day))continue;
                  const element=cell.e.closest('button,[role="gridcell"],[role="button"]')||cell.e;
                  if(element.matches(':disabled,[aria-disabled="true"],[data-disabled="true"]')||
                    element.closest('[aria-disabled="true"],[data-disabled="true"]'))continue;
                  valid.push({element,area:r.width*r.height,cells:cells.length});
                  break;
                }
              }
              const choices=[...new Set(valid.map(x=>x.element))];
              if(choices.length!==1)return {ok:false,why:'monthly grid selection ambiguous',
                headers:headers.length,grids:valid.length};
              choices[0].setAttribute(marker,'yes');
              return {ok:true,day:arg.day,cells:valid[0].cells};
            }
            """,new{year=target.Year,month=target.Month,day=target.Day});
        if(!result.GetProperty("ok").GetBoolean())
        {
            log?.Invoke("TikTok: TUX calendar day selection unavailable: "+
                result.GetProperty("why").GetString()+"; diagnostic="+result.GetRawText());
            return false;
        }
        var day=page.Locator("[data-zerovideo-tux-calendar-day='yes']");
        if(await day.CountAsync()!=1||!await day.IsVisibleAsync()||
           !await day.IsEnabledAsync())return false;
        log?.Invoke("TikTok: selecting "+target.ToString("yyyy-MM-dd")+
            " inside verified seven-column monthly calendar.");
        await day.ClickAsync(new(){Timeout=7000});
        for(var poll=0;poll<22;poll++)
        {
            ct.ThrowIfCancellationRequested();
            var shown=await dateTrigger.EvaluateAsync<string>(
                "e=>(e.value||e.innerText||e.textContent||'').trim()");
            if(DateTime.TryParse(shown,
                System.Globalization.CultureInfo.GetCultureInfo("en-US"),
                System.Globalization.DateTimeStyles.None,out var date)&&
                date.Date==target.Date)
            {
                log?.Invoke("TikTok: selected calendar date verified: "+shown);
                return true;
            }
            await Task.Delay(170,ct);
        }
        await SaveTikTokPickerEvidenceAsync(page,"TUX-Date-Not-Committed",log);
        return false;
    }

    private static async Task<bool> SetTikTokDateStrictAsync(
        IPage page, ILocator dateTrigger, DateTime target,
        Action<string>? log, CancellationToken ct, bool subsequent = false)
    {
        var culture=System.Globalization.CultureInfo.GetCultureInfo("en-US");
        // Gate EVERY click on real date-control semantics. This is the
        // definitive regression guard for a video description containing
        // "February 9, 2004": the old page-wide text regex picked a caption span.
        var identity = await dateTrigger.EvaluateAsync<System.Text.Json.JsonElement>(
            """
            el => ({
              elementClass:typeof el.className==='string'?el.className:'',
              parentClass:typeof el.parentElement?.className==='string'
                ?el.parentElement.className:'',
              ariaLabel:el.getAttribute('aria-label')||'',
              placeholder:el.getAttribute('placeholder')||'',
              testId:(el.getAttribute('data-testid')||'')+' '+
                (el.getAttribute('data-zerovideo-schedule-date-trigger')?'date-calendar-trigger':''),
              visibleText:(el.innerText||el.value||'').trim().slice(0,200),
              insideEditor:!!el.closest('[contenteditable="true"],[role="textbox"]'),
              insideFinalSubmission:!!el.closest('[data-e2e="post_video_button"]')
            })
            """);
        string? Prop(string key) => identity.GetProperty(key).GetString();
        if (!TikTokScheduleDateGuard.IsDateControl(
            Prop("elementClass"), Prop("parentClass"), Prop("ariaLabel"),
            Prop("placeholder"), Prop("testId"), Prop("visibleText"),
            identity.GetProperty("insideEditor").GetBoolean(),
            identity.GetProperty("insideFinalSubmission").GetBoolean()))
        {
            log?.Invoke("TikTok: rejected NON-DATE element. tag/label/class do not " +
                "identify a scheduling calendar. Never click caption text as a date.");
            await SaveTikTokDateTriggerDiagnosticAsync(
                page,dateTrigger,"Wrong-Element-Blocked",target,log,subsequent);
            return false;
        }

        await SaveTikTokDateTriggerDiagnosticAsync(
            page,dateTrigger,"Before-Click",target,log,subsequent);
        var shown=(await dateTrigger.EvaluateAsync<string>(
            "el => (el.value||el.innerText||el.textContent||'').trim()")).Trim();
        if(DateTime.TryParse(shown,culture,System.Globalization.DateTimeStyles.None,out var existing)
            && existing.Date==target.Date) return true;

        var calendar=page.Locator(".DatePicker__panel:visible").First;
        // The new TUX calendar is a portal; it never creates a DatePicker__panel.
        // Try its verified day picker first, BEFORE the seven-second legacy wait.
        // The subsequent-video path must not click the date wrapper twice:
        // doing so closes the already-open calendar.
        if(await calendar.CountAsync()==0)
        {
            log?.Invoke("TikTok: opening date picker for " +
                (subsequent ? "video 2+" : "first/standalone video") +
                "; displayed='" + shown + "', target=" + target.ToString("yyyy-MM-dd"));
            await dateTrigger.ClickAsync(new(){Timeout=7000});
            await Task.Delay(220,ct);
            if(await SelectTikTokTuxCalendarDayAsync(page,dateTrigger,target,log,ct))
                return true;
            // If TUX is visibly open but ambiguous, do NOT toggle the
            // wrapper and close the calendar before another attempt.
            var tuxVisible=await page.Locator("[data-zerovideo-tux-calendar-day='yes']").CountAsync()>0;
            if(tuxVisible)return false;
        }
        var panelOpen=false;
        try
        {
            await calendar.WaitForAsync(new(){
                State=WaitForSelectorState.Visible,Timeout=7000});
            panelOpen=true;
        }
        catch(PlaywrightException ex)
        {
            log?.Invoke("TikTok: date trigger was clicked but calendar did not open: " +
                ex.Message.Split((char)10)[0]);
            await SaveTikTokDateTriggerDiagnosticAsync(
                page,dateTrigger,"Panel-Not-Opened",target,log,subsequent);
        }

        if(!panelOpen)
        {
            // Do not click a second date ancestor. The TUX calendar is an
            // overlay that toggles closed on a repeated click.
            if(await SelectTikTokTuxCalendarDayAsync(page,dateTrigger,target,log,ct))
                return true;
        }
        if(!panelOpen || await calendar.CountAsync()==0)
        {
            // Current TikTok UI opens a November/2026 TUX calendar whose panel
            // is not named DatePicker__panel. Select day using strict, scoped
            // geometry and verify the read-only input before final submission.
            if(await SelectTikTokTuxCalendarDayAsync(page,dateTrigger,target,log,ct))
                return true;
            log?.Invoke("TikTok: date calendar still unavailable — stopping scheduling " +
                "before time or final submit, without selecting an unverified date.");
            await SaveTikTokPickerEvidenceAsync(page,"Date-Panel-Missing",log);
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
        var selected=(await dateTrigger.EvaluateAsync<string>(
            "el => (el.value||el.innerText||el.textContent||'').trim()")).Trim();
        var ok=DateTime.TryParse(selected,culture,
            System.Globalization.DateTimeStyles.None,out var read)&&read.Date==target.Date;
        log?.Invoke("TikTok: date requested "+target.ToString("yyyy-MM-dd")+
            "; displayed "+selected+"; verified="+ok);
        if(!ok) await SaveTikTokPickerEvidenceAsync(page,"Date-Uncommitted",log);
        return ok;
    }

    private static async Task<ILocator?> GetTikTokTimePopupAsync(
        IPage page, ILocator timeTrigger, Action<string>? log)
    {
        // TikTok keeps inactive picker copies mounted with nonzero geometry.
        // Playwright :visible alone does not exclude .tiktok-timepicker-invisible.
        var popup=page.Locator(
            ".tiktok-timepicker-time-picker-container:not(.tiktok-timepicker-invisible):visible, " +
            ".TimePicker__panel:visible").First;
        if(await popup.CountAsync()>0 && await popup.IsVisibleAsync())return popup;
        // Current TikTok CSS may differ. A candidate must be a small visible
        // popup containing at least 10 short numeric option texts in TWO
        // distinct horizontal columns, adjacent to the time trigger.
        var marked=await page.EvaluateAsync<bool>(@"
()=>{document.querySelectorAll('[data-zerovideo-time-popup]').forEach(
 e=>e.removeAttribute('data-zerovideo-time-popup'));
 const trigger=[...document.querySelectorAll(
   "[data-zerovideo-schedule-time-trigger='true'],.time-picker-input")].find(e=>{
 const r=e.getBoundingClientRect();return r.width>10&&r.height>10});
 if(!trigger)return false;
 const tr=trigger.getBoundingClientRect();
 let matches=[];
 for(const p of document.querySelectorAll('[role=dialog],[role=listbox],[role=menu],[class*=TimePicker],[class*=time-picker],[class*=timePicker]')){
  const r=p.getBoundingClientRect(),s=getComputedStyle(p);
  if(s.display==='none'||s.visibility==='hidden'||p.classList.contains('tiktok-timepicker-invisible')||r.width<180||
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
        ct.ThrowIfCancellationRequested();
        // Second and later uploads often leave an old, inactive time picker
        // mounted in the DOM. Work exclusively inside the active TUX container.
        var col = hour ? "left" : "right";
        var target = value.ToString("00",System.Globalization.CultureInfo.InvariantCulture);
        var active = page.Locator(
            ".tiktok-timepicker-time-picker-container:not(.tiktok-timepicker-invisible):visible");
        if (await active.CountAsync() != 1)
        {
            log?.Invoke("TikTok: expected one active time popup, found " +
                await active.CountAsync() + "; will not click another video's picker.");
            return false;
        }
        var exact = active.Locator(
            ".tiktok-timepicker-option-text.tiktok-timepicker-" + col)
            .Filter(new(){HasTextRegex=new System.Text.RegularExpressions.Regex(
                @"^\s*" + target + @"\s*$")});
        if (await exact.CountAsync() != 1)
        {
            log?.Invoke("TikTok: expected one " + col + " time option " + target +
                ", found " + await exact.CountAsync());
            return false;
        }
        var choice=exact.First.Locator("xpath=..");
        if(!await choice.IsVisibleAsync() || !await choice.IsEnabledAsync())
            return false;
        await choice.ScrollIntoViewIfNeededAsync(new(){Timeout=5000});
        await choice.ClickAsync(new(){Timeout=5000});
        log?.Invoke("TikTok: selected " + (hour?"hour ":"minute ") + target +
            " from unique ACTIVE time picker.");
        await Task.Delay(200,ct);
        return true;
    }

    // The TUX time value may be held by a child INPUT or text node. Reading
    // el.value || el.innerText from the outer wrapper can return an empty
    // string, even while the UI visibly displays e.g. 23:00.
    private static async Task<string> ReadTikTokTimeValueAsync(
        ILocator timeTrigger, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var raw=await timeTrigger.EvaluateAsync<string>(
            """
            el => {
              const options=[el];
              options.push(...el.querySelectorAll('input,[role="combobox"],span'));
              if(el.parentElement)
                options.push(...el.parentElement.querySelectorAll('input'));
              for(const n of options){
                const value=(n.value||n.innerText||n.textContent||'')
                  .replace(/[\u200b-\u200f\u202a-\u202e]/g,'')
                  .trim();
                const match=value.match(/(?:^|[^\d])(\d{1,2}):([0-5]\d)(?!\d)/);
                if(match)return match[1].padStart(2,'0')+':'+match[2];
              }
              return '';
            }
            """);
        return raw.Trim();
    }

    private static async Task<bool> WaitTikTokTimeValueAsync(
        ILocator trigger, DateTime target, Action<string>? log,
        CancellationToken ct)
    {
        var wanted=target.ToString("HH:mm",System.Globalization.CultureInfo.InvariantCulture);
        string last="";
        for(var poll=0;poll<14;poll++)
        {
            ct.ThrowIfCancellationRequested();
            last=await ReadTikTokTimeValueAsync(trigger,ct);
            if(last==wanted)
            {
                log?.Invoke("TikTok: committed time verified: "+last);
                return true;
            }
            await Task.Delay(180,ct);
        }
        log?.Invoke("TikTok: time readback expected "+wanted+
            ", actual '"+last+"'. No final submission.");
        return false;
    }

    private static async Task<bool> VerifyTikTokScheduledFieldsAsync(
        IPage page, DateTimeOffset requested, Action<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var date=await FindTikTokScheduleDateTriggerAsync(page,log,ct);
        var time=await FindTikTokNewScheduleToolbarFieldAsync(
            page,false,log,ct) ??
            page.Locator(".time-picker-input:visible").First;
        if(await time.CountAsync()==0)
            time=await FindTikTokScheduleFieldNearHeadingAsync(
                page,date:false,log,ct) ??
                page.Locator(".time-picker-input:visible").First;
        if(date is null||await time.CountAsync()!=1)
        {
            log?.Invoke("TikTok: date or time field missing during final schedule readback.");
            return false;
        }
        var displayedDate=await date.EvaluateAsync<string>(
            "el => (el.value||el.innerText||el.textContent||'').trim()");
        var displayedTime=await ReadTikTokTimeValueAsync(time,ct);
        var target=requested.LocalDateTime;
        var parsed=DateTime.TryParse(displayedDate,
            System.Globalization.CultureInfo.GetCultureInfo("en-US"),
            System.Globalization.DateTimeStyles.None,out var day);
        var valid=parsed&&day.Date==target.Date&&
            (displayedTime==target.ToString("HH:mm")||
             displayedTime==target.ToString("H:mm"));
        log?.Invoke("TikTok: FINAL readback expected " +
            target.ToString("yyyy-MM-dd HH:mm") + " displayed " +
            displayedDate + " " + displayedTime + " valid=" + valid);
        if(!valid)await SaveTikTokPickerEvidenceAsync(page,"Final-Readback-Mismatch",log);
        return valid;
    }

    private static async Task<bool> SetTikTokScheduleAsync(
        IPage page,DateTimeOffset slot,Action<string>? log,CancellationToken ct,
        bool subsequent = false)
    {
        var target=slot.LocalDateTime;
        log?.Invoke("TikTok: schedule mode="+
            (subsequent?"subsequent-video":"first-video-original")+
            "; requested "+target.ToString("yyyy-MM-dd HH:mm"));
        var culture=System.Globalization.CultureInfo.GetCultureInfo("en-US");
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
        log?.Invoke("TikTok: Schedule mode clicked; verifying that the date " +
            "widget mounted before attempting any calendar interaction.");
        log?.Invoke("TikTok: strictly setting "+target.ToString("yyyy-MM-dd HH:mm")+
            "; privacy and music controls must remain untouched.");

        var dateTrigger=await FindTikTokScheduleDateTriggerAsync(page,log,ct);
        if(dateTrigger is null)
        {
            log?.Invoke("TikTok: Schedule date control unavailable; the calendar " +
                "will NOT be clicked through a page-wide text match.");
            return false;
        }
        var dateOk=await SetTikTokDateStrictAsync(
            page,dateTrigger,target,log,ct,subsequent);
        if(!dateOk)
        {
            await SaveTikTokPickerEvidenceAsync(page,"Date-Failed",log);
            log?.Invoke("TikTok: date never committed; stopping before time selection " +
                "and final Schedule for ALL videos.");
            return false;
        }

        // Date failure does not authorize any click outside the time field;
        // time is tested independently but inside its own verified popup.
        var timeTrigger=await FindTikTokNewScheduleToolbarFieldAsync(
            page,false,log,ct) ??
            page.Locator(".time-picker-input:visible").First;
        if(await timeTrigger.CountAsync()==0)
            timeTrigger=await FindTikTokScheduleFieldNearHeadingAsync(
                page, date: false, log, ct) ??
                page.Locator(".time-picker-input:visible").First;
        var timeOk=false;
        if(await timeTrigger.CountAsync()>0&&await timeTrigger.IsVisibleAsync())
        {
            timeOk=await ReadTikTokTimeValueAsync(timeTrigger,ct)==
                target.ToString("HH:mm",culture);
            if(!timeOk)
            {
                await page.Keyboard.PressAsync("Escape");
                await timeTrigger.ClickAsync(new(){Timeout=6000});
                await Task.Delay(300,ct);
                await SaveTikTokPickerEvidenceAsync(page,"Time-Open",log);
                var popup=await GetTikTokTimePopupAsync(page,timeTrigger,log);
                if(popup is not null)
                {
                    var hour=await SelectTikTokTimeColumnAsync(
                        page,popup,target.Hour,true,log,ct);
                    if(!hour)
                    {
                        log?.Invoke("TikTok: hour could not be selected in active picker.");
                    }
                    else
                    {
                        // The popup can close or re-render after the hour click.
                        // If it already committed the requested clock, do NOT
                        // toggle the trigger and accidentally reopen it.
                        timeOk=await ReadTikTokTimeValueAsync(timeTrigger,ct)==
                            target.ToString("HH:mm",culture);
                        if(!timeOk)
                        {
                            var minutePopup=await GetTikTokTimePopupAsync(
                                page,timeTrigger,log);
                            if(minutePopup is null)
                            {
                                await timeTrigger.ClickAsync(new(){Timeout=6000});
                                await Task.Delay(220,ct);
                                minutePopup=await GetTikTokTimePopupAsync(
                                    page,timeTrigger,log);
                            }
                            var minute=minutePopup is not null &&
                                await SelectTikTokTimeColumnAsync(
                                    page,minutePopup,target.Minute,false,log,ct);
                            log?.Invoke("TikTok: time-picker selections hour="+hour+
                                " minute="+minute);
                        }
                    }
                    // Picking the minute itself often closes the TUX popup.
                    // Do not click the time trigger to close it: that may
                    // toggle it back open or re-render an old clock state.
                    timeOk=await WaitTikTokTimeValueAsync(
                        timeTrigger,target,log,ct);
                    if(timeOk)
                        await page.Keyboard.PressAsync("Escape");
                }
                if(!timeOk)
                    await SaveTikTokPickerEvidenceAsync(page,"Time-Failed",log);
            }
        }
        if(!dateOk||!timeOk)
        {
            log?.Invoke("TikTok: schedule NOT set. date="+dateOk+" time="+timeOk);
            return false;
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

    // A navigation to /tiktokstudio/content is NOT enough to confirm a post.
    // Reconcile against the actual Studio Posts row: title + exact scheduled
    // local date + local clock time. No Submit/Post action occurs here.
    public async Task<bool> VerifyExistingTikTokScheduledPostAsync(
        ZeroVideoSettings settings, string expectedTitle, DateTimeOffset slot,
        Action<string>? log = null, CancellationToken ct = default)
    {
        await browser.EnsureStartedAsync(settings);
        var page=await browser.GetOrCreatePageAsync("tiktok.com");
        return await VerifyTikTokPostInStudioAsync(page,expectedTitle,slot,log,ct);
    }

    private static async Task<bool> VerifyTikTokPostInStudioAsync(
        IPage origin, string expectedTitle, DateTimeOffset? slot,
        Action<string>? log, CancellationToken ct)
    {
        if (!slot.HasValue || string.IsNullOrWhiteSpace(expectedTitle)) return false;
        // A TikTok Posts row is the receipt, NOT a redirect from /upload.
        // React splits/truncates the visible movie title and wraps the blue
        // date badge. Identify *one physical row* holding BOTH the movie
        // title prefix and its scheduled local date + clock.
        var prefix=System.Text.RegularExpressions.Regex.Replace(
            expectedTitle.Trim(), @"\s+", " ");
        prefix=prefix[..Math.Min(prefix.Length,25)];
        var local=slot.Value.LocalDateTime;
        var monthDay=local.ToString("MMM d",
            System.Globalization.CultureInfo.InvariantCulture);
        var clock=local.ToString("h:mmtt",
            System.Globalization.CultureInfo.InvariantCulture);
        var onPosts=origin.Url.Contains("/tiktokstudio/content",
            StringComparison.OrdinalIgnoreCase);
        var audit=onPosts?origin:await origin.Context.NewPageAsync();
        try
        {
            if(!onPosts)
                await audit.GotoAsync("https://www.tiktok.com/tiktokstudio/content",
                    new(){WaitUntil=WaitUntilState.DOMContentLoaded,Timeout=45000});
            for(var pass=0;pass<22;pass++)
            {
                ct.ThrowIfCancellationRequested();
                var result=await audit.EvaluateAsync<System.Text.Json.JsonElement>(
                    """
                    arg => {
                      const norm=s=>(s||'').replace(/[\u200b-\u200f]/g,'')
                        .replace(/\s+/g,' ').trim().toLowerCase();
                      const key=s=>norm(s).replace(/[^a-z0-9]/g,'');
                      const prefix=key(arg.prefix);
                      const date=key(arg.monthDay),clock=key(arg.clock);
                      const dateStart=date+clock;
                      const text=e=>norm(e.innerText||e.textContent||'');
                      const isVisible=e=>{
                        const r=e.getBoundingClientRect();
                        const css=getComputedStyle(e);
                        return r.width>1 && r.height>1 &&
                          css.display!=='none'&&css.visibility!=='hidden';
                      };
                      // Parse the blue schedule badge first. A date anywhere
                      // else on the Posts page must NOT confirm a movie.
                      const badges=[...document.querySelectorAll('span,a,div,p,time')]
                        .filter(e=>{
                          if(!isVisible(e))return false;
                          const own=key(text(e));
                          if(own.length>110||!own.includes(dateStart))return false;
                          return ![...e.children].some(c=>
                            key(text(c)).includes(dateStart));
                        });
                      const matched=new Map(),samples=[];
                      for(const badge of badges){
                        const badgeRect=badge.getBoundingClientRect();
                        const candidates=[];
                        // TikTok uses flex rows, not always <tr>. A title may
                        // occupy a sibling cell and can be split by <span>s.
                        for(let p=badge.parentElement,depth=0;p&&depth<14;
                            p=p.parentElement,depth++){
                          const r=p.getBoundingClientRect();
                          if(r.height>310)break; // never accept the Posts table
                          if(r.width<240||r.height<48)continue;
                          if(!key(text(p)).includes(dateStart))continue;
                          if(!key(text(p)).includes(prefix))continue;
                          // A real row has a video thumbnail and several
                          // action controls; avoid matching surrounding cards.
                          const hasThumbnail=!!p.querySelector('img,video,canvas');
                          const actions=p.querySelectorAll('button,[role="button"]').length;
                          // New Studio cards sometimes use CSS background
                          // thumbnails and icon-only div actions. A compact,
                          // wide row with BOTH exact date/clock and title is
                          // sufficient even when those markup tags are absent.
                          const rowShape=r.width>=600&&r.height>=60&&r.height<=170;
                          if(!hasThumbnail && actions<2 && !rowShape)continue;
                          candidates.push({el:p,area:r.width*r.height,top:Math.round(r.top)});
                        }
                        candidates.sort((a,b)=>a.area-b.area);
                        if(candidates.length) {
                          const chosen=candidates[0];
                          // Deduplicate nested date badges from the same row.
                          const rowKey=String(chosen.top);
                          matched.set(rowKey,chosen);
                        }
                      }
                      // Observability: log safe row previews even when no
                      // match, to distinguish a title mismatch from date parsing.
                      const preview=badges.slice(0,5).map(e=>{
                        const r=e.getBoundingClientRect();
                        return {badge:text(e).slice(0,75),top:Math.round(r.top)};
                      });
                      return {matches:matched.size,badges:badges.length,
                        preview,path:location.pathname,ready:
                          !!document.querySelector('img,video')};
                    }
                    """,new{prefix,monthDay,clock});
                var hits=result.GetProperty("matches").GetInt32();
                if(hits==1)
                {
                    log?.Invoke("TikTok: VERIFIED Studio Posts row for '"+prefix+
                        "' at "+monthDay+" "+clock+
                        ". Marking Scheduled and continuing to next queued video.");
                    return true;
                }
                if(hits>1)
                {
                    log?.Invoke("TikTok: multiple distinct rows match title and "+
                        "time; stopping to avoid a false confirmation. "+
                        result.GetRawText());
                    return false;
                }
                if(pass is 2 or 8 or 15 or 21)
                    log?.Invoke("TikTok: Studio Posts matching pass "+(pass+1)+
                        "/22: "+result.GetRawText());
                if(pass is 8 or 15)
                    await audit.EvaluateAsync(
                        "() => { let list=[...document.querySelectorAll('*')]"+
                        ".filter(e=>e.clientHeight>250 && e.scrollHeight>e.clientHeight+80)"+
                        ".sort((a,b)=>b.clientHeight-a.clientHeight)[0];"+
                        "if(list)list.scrollTop+=list.clientHeight*.8;"+
                        "else window.scrollBy(0,500); }");
                await Task.Delay(850,ct);
            }
            log?.Invoke("TikTok: no unambiguous Studio Posts row for '"+prefix+
                "' at "+monthDay+" "+clock+
                ". No duplicate upload will be attempted.");
            return false;
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex)
        {
            log?.Invoke("TikTok: Studio Posts read-only verification error: "+
                ex.Message.Split('\n')[0]);
            return false;
        }
        finally
        {
            if(!onPosts)
                try{await audit.CloseAsync();}catch(PlaywrightException){}
        }
    }

    // A single final action: NEVER retry a clicked Schedule/Post button.
    // If TikTok doesn't clearly confirm receipt, flag an ambiguous submission
    // and require the user to inspect TikTok Studio before attempting again.
    // The Schedule radio in "When to post" can have the same name as the
    // final CTA. On newer Studio pages the footer button may lack the older
    // data-e2e value. Mark a unique action ONLY below the Checks section.
    private static async Task<ILocator?> FindTikTokFinalFooterButtonAsync(
        IPage page, bool scheduled, Action<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var lookup = await page.EvaluateAsync<System.Text.Json.JsonElement>(
            """
            scheduled => {
              const marker='data-zerovideo-final-upload-cta';
              document.querySelectorAll('['+marker+']').forEach(x=>x.removeAttribute(marker));
              const visible=e=>{
                const r=e.getBoundingClientRect(),s=getComputedStyle(e);
                return r.width>5&&r.height>10&&r.bottom>0&&r.top<innerHeight&&
                  s.display!=='none'&&s.visibility!=='hidden';
              };
              const value=e=>(e.innerText||e.textContent||'')
                .replace(/\s+/g,' ').trim();
              const checks=[...document.querySelectorAll('h1,h2,h3,h4,h5,h6,div,span')]
                .filter(e=>value(e)==='Checks'&&
                  ![...e.children].some(c=>value(c)==='Checks'));
              // If we can locate Checks, the genuine CTA must be below it.
              const checksBottom=checks.length===1 ?
                checks[0].getBoundingClientRect().bottom+scrollY : null;
              const labels=scheduled ?
                /^(?:Schedule|Post|Publish)$/i : /^(?:Post|Post now|Publish)$/i;
              const candidates=[...document.querySelectorAll('button,[role="button"]')]
                .filter(e=>visible(e)&&labels.test(value(e))).filter(e=>{
                  const y=e.getBoundingClientRect().top+scrollY;
                  if(checksBottom!==null && y<=checksBottom)return false;
                  if(e.closest('[role="radio"],[role="radiogroup"]'))return false;
                  return true;
                }).map(e=>{
                  const r=e.getBoundingClientRect();
                  const data=(e.getAttribute('data-e2e')||'')+' '+
                    (e.getAttribute('data-testid')||'');
                  const score=(data.includes('post_video_button')?100:0)+
                    (/post|submit|publish/i.test(data)?30:0)+
                    (checksBottom!==null?25:0)+
                    (r.top>innerHeight*.5?10:0);
                  return {e,score,label:value(e),y:r.top};
                }).sort((a,b)=>b.score-a.score);
              if(candidates.length===0)
                return {ok:false,reason:'No visible footer Schedule/Post CTA'};
              const best=candidates[0];
              if(candidates.length>1&&candidates[1].score===best.score)
                return {ok:false,reason:'Ambiguous footer CTA candidates: '+
                  candidates.map(x=>x.label+'@'+Math.round(x.y)).join(', ')};
              if(checksBottom===null &&
                  !/post_video_button|submit|publish/i.test(
                    (best.e.getAttribute('data-e2e')||'')+' '+
                    (best.e.getAttribute('data-testid')||'')))
                return {ok:false,reason:'Checks section missing and CTA has no trusted identifier'};
              best.e.setAttribute(marker,'true');
              return {ok:true,label:best.label,source:'scoped footer button'};
            }
            """, scheduled);
        if (!lookup.GetProperty("ok").GetBoolean())
        {
            log?.Invoke("TikTok: final footer CTA fallback: " +
                lookup.GetProperty("reason").GetString());
            return null;
        }
        var candidate=page.Locator("[data-zerovideo-final-upload-cta='true']");
        if(await candidate.CountAsync()!=1 || !await candidate.IsVisibleAsync())
            return null;
        log?.Invoke("TikTok: identified real footer action: '" +
            lookup.GetProperty("label").GetString()+"'.");
        return candidate;
    }

    private static async Task<string> SubmitTikTokOnceAsync(
        IPage page, bool scheduled, string expectedTitle,
        DateTimeOffset? expectedSlot, Action<string>? log, CancellationToken ct)
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

        // The final Schedule/Post CTA is below the fold on TikTok Studio.
        // Its presence on the page is not enough: scroll to the real upload
        // footer and let React finish any pending editor/schedule rerender.
        await page.EvaluateAsync("() => window.scrollTo({top: document.documentElement.scrollHeight, behavior: 'instant'})");
        await Task.Delay(500, ct);
        log?.Invoke("TikTok: locating the FINAL publishing CTA at the bottom of the upload page.");

        // The upload footer's stable data-e2e selector is more specific than
        // the Schedule radio label and avoids privacy/music controls.
        var button = page.Locator("button[data-e2e='post_video_button']:visible");
        var count = await button.CountAsync();
        if (count != 1)
        {
            // Different TikTok builds expose the scheduling toggle and
            // the publishing CTA with the same text. Scope fallback to
            // a unique button BELOW Checks, never the schedule radio.
            button = await FindTikTokFinalFooterButtonAsync(
                page,scheduled,log,ct) ??
                page.Locator("[data-zerovideo-final-upload-cta='true']");
            count = await button.CountAsync();
        }
        if (count != 1)
        {
            log?.Invoke("TikTok: final " + (scheduled ? "Schedule" : "Post") +
                " button missing/ambiguous (matches=" + count + "). No submission attempted.");
            await SaveTikTokFinalSubmitEvidenceAsync(page,"Final-Button-Missing",log);
            return "TikTok: submission NOT attempted; final button missing or ambiguous. Review in Studio.";
        }

        var submit = button.First;
        await submit.ScrollIntoViewIfNeededAsync(new(){Timeout=8000});
        await Task.Delay(250, ct);
        var label = (await submit.InnerTextAsync()).Trim();
        // A finished transfer isn't necessarily a processed TikTok video.
        // Wait for the *actual final button* to be enabled. Do not click
        // while TikTok still has a pending validation/processing state.
        for (var poll = 0; poll < 20; poll++)
        {
            ct.ThrowIfCancellationRequested();
            if (await submit.IsEnabledAsync() &&
                (await submit.GetAttributeAsync("aria-disabled")) != "true" &&
                (await submit.GetAttributeAsync("data-disabled")) != "true")
                break;
            if (poll % 5 == 0)
                log?.Invoke("TikTok: final Schedule/Post button not enabled yet; waiting for validation/processing.");
            await Task.Delay(500, ct);
        }
        log?.Invoke("TikTok: final CTA label='" + label +
            "', data-e2e='" + (await submit.GetAttributeAsync("data-e2e") ?? "") +
            "', enabled=" + await submit.IsEnabledAsync());
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
                "'. No submission attempted; CTA HTML: " +
                (await submit.EvaluateAsync<string>("e=>e.outerHTML")).Substring(0,
                    Math.Min(800,(await submit.EvaluateAsync<string>("e=>e.outerHTML")).Length)));
            await SaveTikTokFinalSubmitEvidenceAsync(page,"Final-Button-Not-Enabled",log);
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

        // Do not send a post while the hashtag suggestion menu is still open:
        // TikTok may consume the button click as a suggestion selection.
        var hashtagSuggestions=page.Locator(
            "[role='listbox']:visible, [data-e2e='hashtag-suggestion-list']:visible");
        if(await hashtagSuggestions.CountAsync()>0)
        {
            await page.Keyboard.PressAsync("Escape");
            await Task.Delay(200,ct);
            if(await hashtagSuggestions.CountAsync()>0)
            {
                log?.Invoke("TikTok: hashtag autocomplete still covers Studio controls.");
                return "TikTok: submission NOT attempted; dismiss hashtag dropdown first.";
            }
        }
        // Playwright trial click checks actionability without dispatching a
        // second publish. It detects obstructing overlays, disabled buttons,
        // sticky controls and incomplete React rerenders before the one click.
        try
        {
            await submit.ClickAsync(new(){Timeout=9000,Trial=true});
        }
        catch (Exception ex) when (ex is PlaywrightException or TimeoutException)
        {
            log?.Invoke("TikTok: final CTA is not clickable: "+ex.Message.Split('\n')[0]);
            await SaveDiagnosticAsync(page,"TikTok-Final-CTA-Blocked",log);
            await SaveTikTokFinalSubmitEvidenceAsync(page,"Final-CTA-Blocked",log);
            return "TikTok: submission NOT attempted; final button blocked. Inspect TikTok Studio.";
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
            await SaveTikTokFinalSubmitEvidenceAsync(page,"Submit-Outcome-Uncertain",log);
            // An exception during navigation is NOT proof of success. Never
            // make a second submit attempt, and never block by scraping Posts.
            return "TikTok: SUBMISSION_ATTEMPTED; click outcome uncertain. Check TikTok Studio Posts before retrying.";
        }

        if (!attempted) return "TikTok: submission NOT attempted.";
        await SaveTikTokFinalSubmitEvidenceAsync(page,"After-One-Submit-Click",log);
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
                    // TikTok navigated to its Posts page following our
                    // one permitted final Schedule/Post click. Treat this as
                    // accepted-for-handoff, NOT independently verified.
                    // Skip the fragile title/date DOM scrape and let the
                    // publisher advance to the next fresh upload tab.
                    if(now.AbsolutePath.Contains("/tiktokstudio/content",
                        StringComparison.OrdinalIgnoreCase))
                    {
                        log?.Invoke("TikTok: final submission redirected to Studio Posts. "+
                            "Proceeding to next queued upload without Posts DOM verification.");
                        return "TikTok: SUBMISSION_REDIRECTED; final submit navigated to Studio Posts; platform outcome not independently verified.";
                    }
                    log?.Invoke("TikTok: submission navigated to unexpected page '"+
                        now.AbsolutePath+"'. Batch must pause.");
                    return "TikTok: SUBMISSION_ATTEMPTED; unexpected redirect after submit. Check Studio Posts.";
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
        await SaveTikTokFinalSubmitEvidenceAsync(page,"Submit-Confirmation-Unknown",log);
        return "TikTok: SUBMISSION_ATTEMPTED; confirmation unavailable. Check TikTok Studio Posts before retrying.";
    }


    // TikTok may asynchronously prefill the contenteditable caption with the
    // uploaded file name (e.g. 1226578_20261119_142908085...). Always verify
    // the editor AFTER upload processing, not merely after FillAsync returns.
    private static string TikTokCaptionComparison(string text)
        => System.Text.RegularExpressions.Regex.Replace(
            (text ?? "").Replace("\u200B", "").Replace("\uFEFF", ""),
            @"\s+", " ").Trim();

    private static string TikTokCleanCaption(string caption)
    {
        var value = (caption ?? "").Trim();
        // Strip a leftover file-name prefix only when it is clearly a generated
        // MovieClipper export identifier followed by a long timestamp.
        value = System.Text.RegularExpressions.Regex.Replace(value,
            @"^\s*\d{5,12}_(?:\d{8}_\d{6,9}|\d{14,19})\s*",
            "", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        return value;
    }

    private static async Task<bool> TikTokFillVerifiedCaptionAsync(
        IPage page, string wanted, Action<string>? log, CancellationToken ct)
    {
        // TikTok presents a hashtag autocomplete dropdown when a
        // contenteditable caption ends in #MovieReview etc. The dropdown can
        // obscure scheduling fields and can restore focus after a React render.
        // Verify existing text BEFORE FillAsync: rewriting an already-correct
        // caption would needlessly reopen the autocomplete on every post.
        for (var attempt=1;attempt<=3;attempt++)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var frame in page.Frames)
            {
                try
                {
                    var editor = frame.Locator("[contenteditable='true']:visible").First;
                    if (await editor.CountAsync()!=1 || !await editor.IsVisibleAsync()) continue;
                    var found=await editor.EvaluateAsync<string>(
                        "el => (el.innerText||el.textContent||'').replace(/\\u200b/g,'').trim()");
                    if(TikTokCaptionComparison(found)!=TikTokCaptionComparison(wanted))
                    {
                        await editor.FillAsync(wanted);
                        await Task.Delay(750,ct);
                    }
                    // Explicitly commit the caption and dismiss autocomplete.
                    // Escape only affects the editor popup, not the form submit.
                    await editor.PressAsync("Escape",new(){Timeout=4000});
                    await editor.PressAsync("Tab",new(){Timeout=4000});
                    await Task.Delay(600,ct);
                    found=await editor.EvaluateAsync<string>(
                        "el => (el.innerText||el.textContent||'').replace(/\\u200b/g,'').trim()");
                    if(TikTokCaptionComparison(found)==TikTokCaptionComparison(wanted))
                    {
                        log?.Invoke("TikTok: SEO caption verified and hashtag autocomplete dismissed.");
                        return true;
                    }
                    log?.Invoke("TikTok: caption changed after upload/focus switch ("+attempt+
                        "/3); expected "+wanted.Length+", actual "+found.Length+".");
                }
                catch (PlaywrightException ex)
                {
                    log?.Invoke("TikTok: caption editor unavailable: "+ex.Message.Split('\n')[0]);
                }
            }
            await Task.Delay(400,ct);
        }
        await SaveDiagnosticAsync(page,"TikTok-Caption-Mismatch",log);
        log?.Invoke("TikTok: caption verification failed; submission blocked.");
        return false;
    }

    // Passive post-scheduling verification. Never change focus or press Tab:
    // doing so scrolls TikTok back to Description instead of submitting.
    private static async Task<bool> TikTokVerifyCaptionReadOnlyAsync(
        IPage page, string wanted, Action<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var matching = 0;
        foreach (var frame in page.Frames)
        {
            var editors = frame.Locator("[contenteditable='true']:visible");
            if (await editors.CountAsync() == 0) continue;
            var actual = await editors.First.EvaluateAsync<string>(
                "el => (el.innerText||el.textContent||'').trim()");
            if(TikTokCaptionComparison(actual)==TikTokCaptionComparison(wanted))
                matching++;
        }
        log?.Invoke("TikTok: passive caption verification after schedule: "+
            (matching == 1 ? "MATCHED (no focus changes)" :
            "FAILED (matching editors="+matching+")"));
        return matching == 1;
    }

    // Preserve a privacy-limited snapshot of only the final button, visible
    // validation messages, current schedule inputs, and dialog actions. Never
    // collect captions, cookies, uploaded media or page HTML wholesale.
    private static async Task SaveTikTokFinalSubmitEvidenceAsync(
        IPage page, string step, Action<string>? log)
    {
        try
        {
            var snapshot = await page.EvaluateAsync<string>(
                """
                () => {
                  const visible = e => {
                    const r=e.getBoundingClientRect();
                    const s=getComputedStyle(e);
                    return r.width>2 && r.height>2 &&
                      s.visibility!=='hidden' && s.display!=='none';
                  };
                  const text = e => (e.innerText||e.value||'').trim().replace(/\s+/g,' ').slice(0,100);
                  const all = [...document.querySelectorAll('button,[role="button"]')];
                  const buttons = all.filter(visible).map(e=>({
                    text:text(e),dataE2e:e.getAttribute('data-e2e'),
                    disabled:e.disabled||e.getAttribute('aria-disabled')==='true',
                    type:e.getAttribute('type'),className:typeof e.className==='string'?e.className.slice(0,110):'',
                    y:Math.round(e.getBoundingClientRect().top)
                  })).filter(x=>/post|publish|schedule|confirm|continue|cancel/i.test(x.text)||
                    /post_video_button/.test(x.dataE2e||''));
                  const fields = [...document.querySelectorAll('input[readonly]')]
                    .filter(visible).map(e=>({value:e.value,type:e.type}));
                  const errors = [...document.querySelectorAll(
                    '[role="alert"],[aria-invalid="true"],[class*="error"],[class*="Error"],[data-e2e*="error"]')]
                    .filter(visible).map(e=>text(e)).filter(Boolean).slice(0,12);
                  const dialogs=[...document.querySelectorAll('[role="dialog"],[aria-modal="true"]')]
                    .filter(visible).map(e=>({label:text(e).slice(0,140),
                      buttons:[...e.querySelectorAll('button')].filter(visible).map(text)}));
                  return JSON.stringify({path:location.pathname,scrollY,buttons,
                    fields,errors,dialogs},null,2);
                }
                """);
            var dir=Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
                "ZeroVideo","PublishingDiagnostics");
            Directory.CreateDirectory(dir);
            var file=Path.Combine(dir,"TikTok-Final-"+step+"-"+
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+".txt");
            await File.WriteAllTextAsync(file,snapshot);
            log?.Invoke("TikTok: final CTA diagnostic saved: "+file);
        }
        catch(Exception ex)
        {
            log?.Invoke("TikTok: final CTA evidence unavailable: "+
                ex.Message.Split('\n')[0]);
        }
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
            // TikTok asynchronously inserts the video filename as a default
            // caption. Wait for upload completion BEFORE overwriting it.
            if (!await WaitForTikTokUploadReadyAsync(page, log, ct))
                return "TikTok: upload still processing; final submission NOT attempted.";
            var finalCaption = TikTokCleanCaption(caption);
            if (string.IsNullOrWhiteSpace(finalCaption) ||
                finalCaption.Length > 3500)
                return "TikTok: invalid SEO caption. No post submitted.";
            if (!await TikTokFillVerifiedCaptionAsync(page,finalCaption,log,ct))
                return "TikTok: caption mismatch after upload; final submission NOT attempted.";
            try
            {
                if (scheduledAt.HasValue &&
                    !await SetTikTokScheduleAsync(page, scheduledAt.Value, log, ct,
                        subsequent: freshTikTokUploadPage))
                    return "TikTok: requested date/time NOT verified. Final submission NOT attempted.";

                // CRITICAL: after scheduling, do NOT call the interactive
                // caption fill routine again. It presses Escape + Tab and
                // sends focus/scroll back to the Description at the top,
                // which can dismiss the final Schedule action before clicking.
                // Read back caption in place, without focusing or changing DOM.
                if (!await TikTokVerifyCaptionReadOnlyAsync(page,finalCaption,log,ct))
                {
                    await SaveTikTokFinalSubmitEvidenceAsync(page,"Caption-Changed-After-Schedule",log);
                    return "TikTok: caption changed after scheduling; final submission NOT attempted.";
                }
                if (scheduledAt.HasValue &&
                    !await VerifyTikTokScheduledFieldsAsync(page,scheduledAt.Value,log,ct))
                    return "TikTok: schedule changed after upload metadata update. Final post NOT attempted.";
                // The upload/editor can take minutes. A slot that was valid
                // at batch start can become too close before TikTok accepts it.
                // Do not silently change a user-approved schedule.
                if (scheduledAt.HasValue &&
                    scheduledAt.Value <= DateTimeOffset.Now.AddMinutes(20))
                {
                    log?.Invoke("TikTok: scheduling slot expired during upload: " +
                        scheduledAt.Value.ToString("yyyy-MM-dd HH:mm zzz") +
                        ". Move it 30+ minutes later and resume.");
                    return "TikTok: submission NOT attempted; schedule is now less than " +
                        "20 minutes away. Choose a later slot.";
                }
                return await SubmitTikTokOnceAsync(page, scheduledAt.HasValue,
                    finalCaption.Split('\n')[0].Trim(),scheduledAt,log,ct);
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
