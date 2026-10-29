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




    // Read the calendar by its VISIBLE month/year heading and 7-column day
    // grid, not TikTok's changing CSS class names. The October 2026 DOM in the
    // provided diagnostic renders a calendar that our old selectors cannot find.
    // Only click when the grid index and number agree with the target date.
    private static async Task<bool> SelectTikTokVisualCalendarAsync(
        IPage page, ILocator trigger, DateTime target, Action<string>? log, CancellationToken ct)
    {
        const string probeScript = @"
(args) => {
  const months=['January','February','March','April','May','June','July',
    'August','September','October','November','December'];
  const styleVisible = el => {
    const r=el.getBoundingClientRect(), s=getComputedStyle(el);
    return r.width>3 && r.height>3 && s.display!=='none' && s.visibility!=='hidden';
  };
  const smallLeaf = el => {
    const t=(el.textContent||'').trim();
    return ![...el.children].some(c=>(c.textContent||'').trim()===t);
  };
  const nodes=[...document.querySelectorAll('body *')].filter(
    el=>styleVisible(el) && smallLeaf(el));
  const monthCandidates = nodes.filter(el=>months.some(m=>
    m.toLowerCase()===(el.textContent||'').trim().toLowerCase()));
  const nearby = monthCandidates.map(el=>{
    const r=el.getBoundingClientRect();
    const x=r.left+r.width/2,y=r.top+r.height/2;
    return { el,x,y,idx:months.findIndex(m=>
      m.toLowerCase()===(el.textContent||'').trim().toLowerCase()),
      score:Math.abs(x-(args.x+110))*0.7+Math.abs(y-args.y) };
  }).filter(m=>m.x>args.x-130 && m.x<args.x+530 &&
    m.y>args.y-600 && m.y<args.y+440)
    .sort((a,b)=>a.score-b.score);
  if (!nearby.length) return {error:'No visible calendar month header'};
  const header=nearby[0];
  const hr=header.el.getBoundingClientRect();
  const years=nodes.filter(el=>/^20\d{2}$/.test((el.textContent||'').trim()))
    .map(el=>{
      const r=el.getBoundingClientRect();
      return {year:Number((el.textContent||'').trim()),
        x:r.left+r.width/2,y:r.top+r.height/2};
    }).filter(y=>Math.abs(y.y-header.y)<35 && Math.abs(y.x-header.x)<240)
    .sort((a,b)=>Math.abs(a.x-header.x)-Math.abs(b.x-header.x));
  const shownYear=years.length?years[0].year:args.initialYear;
  const shownMonth=header.idx+1;
  const delta=(args.targetYear-shownYear)*12+args.targetMonth-shownMonth;
  const left=header.x-265, right=header.x+335;
  const top=hr.bottom+22;
  const bottom=header.y<args.y ? Math.min(args.y-6,top+360) : top+390;
  const dayNodes = nodes.filter(el=>{
    const t=(el.textContent||'').trim();
    if (!/^(?:[1-9]|[12][0-9]|3[01])$/.test(t)) return false;
    const r=el.getBoundingClientRect();
    const x=r.left+r.width/2,y=r.top+r.height/2;
    return x>left && x<right && y>top && y<bottom &&
      r.width<105 && r.height<90;
  }).map(el=>{
    const r=el.getBoundingClientRect();
    return {n:Number((el.textContent||'').trim()),
      x:r.left+r.width/2,y:r.top+r.height/2};
  });
  // Ignore overlapping ancestors that duplicate the same day glyph.
  const dedup=[];
  for (const item of dayNodes) {
    if (!dedup.some(d=>Math.abs(d.x-item.x)<17 && Math.abs(d.y-item.y)<17))
      dedup.push(item);
  }
  dedup.sort((a,b)=>Math.abs(a.y-b.y)>16 ? a.y-b.y : a.x-b.x);
  const weekday=new Date(args.targetYear,args.targetMonth-1,1).getDay();
  const idx=weekday+args.targetDay-1;
  const matched=dedup.length>=28 && dedup.length<=42 &&
    dedup.length%7===0 && delta===0 && dedup[idx] &&
    dedup[idx].n===args.targetDay;
  const result={shownMonth,shownYear,delta,cellCount:dedup.length,
    index:idx,observed:dedup[idx]?.n ?? -1,
    preview:dedup.slice(0,9).map(x=>x.n).join(','),
    x:matched?dedup[idx].x:0,y:matched?dedup[idx].y:0,
    matched:!!matched};
  if (delta!==0) {
    // Locate chevron icons near the month/year heading; this is the header's
    // only pair of horizontal navigation icons in the calendar popup.
    const arrows=[...document.querySelectorAll('svg')].filter(styleVisible)
      .map(el=>{ const r=el.getBoundingClientRect();
        return {x:r.left+r.width/2,y:r.top+r.height/2,w:r.width,h:r.height};
      }).filter(a=>a.w<50 && a.h<50 && Math.abs(a.y-header.y)<35 &&
        a.x>header.x-200 && a.x<header.x+235);
    const rightArrow=arrows.filter(a=>a.x>header.x+38).sort((a,b)=>b.x-a.x)[0];
    const leftArrow=arrows.filter(a=>a.x<header.x-38).sort((a,b)=>a.x-b.x)[0];
    const arrow=delta>0?rightArrow:leftArrow;
    result.arrowX=arrow?.x ?? 0; result.arrowY=arrow?.y ?? 0;
  }
  return result;
}";
        var anchor = await trigger.BoundingBoxAsync();
        if (anchor is null) return false;
        var initialLabel = (await trigger.InnerTextAsync()).Trim();
        DateTime.TryParse(initialLabel, System.Globalization.CultureInfo.GetCultureInfo("en-US"),
            System.Globalization.DateTimeStyles.None, out var initialDate);
        for (var attempt = 0; attempt < 18; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var data = await page.EvaluateAsync<System.Text.Json.JsonElement>(probeScript,
                new { x = anchor.X, y = anchor.Y,
                    initialYear = initialDate == default ? DateTime.Today.Year : initialDate.Year,
                    targetYear = target.Year, targetMonth = target.Month, targetDay = target.Day });
            if (data.TryGetProperty("error", out var failure))
            {
                log?.Invoke("TikTok: visual calendar detection: " + failure.GetString());
                return false;
            }
            var delta = data.GetProperty("delta").GetInt32();
            var count = data.GetProperty("cellCount").GetInt32();
            log?.Invoke("TikTok: visible calendar " +
                data.GetProperty("shownMonth").GetInt32() + "/" +
                data.GetProperty("shownYear").GetInt32() +
                ", desired " + target.ToString("yyyy-MM-dd") +
                ", offset " + delta + ", cells " + count +
                ", index cell " + data.GetProperty("observed").GetInt32() +
                ", preview " + data.GetProperty("preview").GetString());
            if (delta == 0)
            {
                if (!data.GetProperty("matched").GetBoolean()) return false;
                await page.Mouse.ClickAsync((float)data.GetProperty("x").GetDouble(),
                    (float)data.GetProperty("y").GetDouble());
                await Task.Delay(350, ct);
                return true;
            }
            var arrowX = (float)data.GetProperty("arrowX").GetDouble();
            var arrowY = (float)data.GetProperty("arrowY").GetDouble();
            if (arrowX == 0 || arrowY == 0)
            {
                log?.Invoke("TikTok: no reliable month arrow visible.");
                return false;
            }
            await page.Mouse.ClickAsync(arrowX, arrowY);
            await Task.Delay(250, ct);
        }
        log?.Invoke("TikTok: calendar month navigation limit reached.");
        return false;
    }

    private static async Task SaveTikTokCalendarDomAsync(IPage page, Action<string>? log)
    {
        try
        {
            // Run even when all calendar CSS selectors fail. Capture the
            // visible month text and its surrounding DOM, not the entire page.
            var html = await page.EvaluateAsync<string>(@"
() => {
  const visible=e=>{ const r=e.getBoundingClientRect();
    return r.width>0 && r.height>0 };
  const month=/^(January|February|March|April|May|June|July|August|September|October|November|December)$/i;
  const candidates=[...document.querySelectorAll('body *')].filter(e=>
    visible(e) && month.test((e.textContent||'').trim()) &&
    ![...e.children].some(c=>(c.textContent||'').trim()===(e.textContent||'').trim()));
  return candidates.slice(0,4).map(e=>{
    let p=e;
    for (let n=0;n<4&&p.parentElement;n++)p=p.parentElement;
    return p.outerHTML.slice(0,30000);
  }).join('\n--- calendar candidate ---\n').slice(0,80000);
}");
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZeroVideo", "PublishingDiagnostics");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, "TikTok-Calendar-DOM-" +
                DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
            await File.WriteAllTextAsync(dest, html);
            log?.Invoke("TikTok: calendar HTML diagnostic saved: " + dest);
        }
        catch (Exception ex)
        {
            log?.Invoke("TikTok: calendar HTML diagnostic failed: " + ex.Message);
        }
    }

    // Fallback for TikTok's October-2026 layout when DOM wrappers prevent a
    // clean 35/42-cell grid. Locate the exact date number in the expected
    // weekday column and week row using the visible header and weekdays.
    private static async Task<bool> SelectTikTokCalendarDayByWeekdayAsync(
        IPage page, ILocator trigger, DateTime target, Action<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var bounds = await trigger.BoundingBoxAsync();
        if (bounds is null) return false;
        const string inspect = @"
(args) => {
  const vis=e=>{let r=e.getBoundingClientRect(),s=getComputedStyle(e);
    return r.width>4&&r.height>4&&s.display!=='none'&&s.visibility!=='hidden';};
  const text=e=>(e.textContent||'').trim();
  const leaf=e=>![...e.children].some(c=>text(c)===text(e));
  const nodes=[...document.querySelectorAll('body *')].filter(e=>vis(e)&&leaf(e));
  const months=['January','February','March','April','May','June','July',
    'August','September','October','November','December'];
  const headers=nodes.filter(e=>months.some(m=>m.toLowerCase()===text(e).toLowerCase()))
    .map(e=>{const r=e.getBoundingClientRect();return {x:r.left+r.width/2,
      y:r.top+r.height/2, bottom:r.bottom,
      month:months.findIndex(m=>m.toLowerCase()===text(e).toLowerCase())+1};})
    .filter(h=>Math.abs(h.x-args.ax)<500 && h.y<args.ay && h.y>args.ay-650)
    .sort((a,b)=>Math.abs(a.x-args.ax)-Math.abs(b.x-args.ax));
  if(!headers.length)return {error:'month header not found'};
  const h=headers[0];
  if(h.month!==args.month)return {error:'calendar displays month '+h.month+' not '+args.month};
  const weekdays=['sun','mon','tue','wed','thu','fri','sat'];
  const labels=nodes.filter(e=>{
    const t=text(e).toLowerCase();
    return weekdays.some(d=>t===d||t===(d==='thu'?'thurs':''));
  }).map(e=>{const r=e.getBoundingClientRect();return {x:r.left+r.width/2,
    y:r.top+r.height/2,key:text(e).toLowerCase()};})
    .filter(e=>e.y>h.y && e.y<h.y+95 && e.x>h.x-270 && e.x<h.x+330)
    .sort((a,b)=>a.x-b.x);
  const distinct=[];
  for(const l of labels) if(!distinct.some(d=>Math.abs(d.x-l.x)<12))distinct.push(l);
  if(distinct.length!==7)return {error:'weekday columns '+distinct.length};
  const weekday=(new Date(args.year,args.month-1,1).getDay()+args.day-1)%7;
  const row=Math.floor((new Date(args.year,args.month-1,1).getDay()+args.day-1)/7);
  const days=nodes.filter(e=>/^(?:[1-9]|[12][0-9]|3[01])$/.test(text(e)))
    .map(e=>{const r=e.getBoundingClientRect();return {n:Number(text(e)),
      x:r.left+r.width/2,y:r.top+r.height/2};})
    .filter(v=>v.y>distinct[0].y+12 && v.y<args.ay-9 &&
      v.x>=distinct[0].x-18 && v.x<=distinct[6].x+18);
  const unique=[];
  for(const d of days)if(!unique.some(v=>Math.abs(v.x-d.x)<12&&Math.abs(v.y-d.y)<12))unique.push(d);
  const rows=[];
  for(const d of unique.sort((a,b)=>a.y-b.y)){
    let existing=rows.find(r=>Math.abs(r.y-d.y)<14);
    if(existing)existing.items.push(d);
    else rows.push({y:d.y,items:[d]});
  }
  const x=distinct[weekday].x;
  const candidates=unique.filter(d=>d.n===args.day && Math.abs(d.x-x)<23)
    .sort((a,b)=>Math.abs(a.y-(rows[row]?.y??0))-Math.abs(b.y-(rows[row]?.y??0)));
  const selected=candidates.find(c=>rows[row] && Math.abs(c.y-rows[row].y)<14);
  return {error:selected?null:'target date cell missing or wrong row',
    rows:rows.length,weekday,row,dayCandidates:candidates.length,
    x:selected?.x??0,y:selected?.y??0,
    labels:distinct.map(v=>v.key).join(','),rowCounts:rows.map(v=>v.items.length).join(',')};
}";
        var data = await page.EvaluateAsync<System.Text.Json.JsonElement>(inspect,
            new { ax = bounds.X + bounds.Width / 2, ay = bounds.Y,
                month = target.Month, year = target.Year, day = target.Day });
        if (data.TryGetProperty("error", out var error) &&
            error.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            log?.Invoke("TikTok: weekday-grid selection unavailable: " + error.GetString());
            return false;
        }
        log?.Invoke("TikTok: weekday-grid labels " + data.GetProperty("labels").GetString() +
            "; rows " + data.GetProperty("rowCounts").GetString() +
            "; target week " + data.GetProperty("row").GetInt32() +
            " weekday " + data.GetProperty("weekday").GetInt32());
        await page.Mouse.ClickAsync((float)data.GetProperty("x").GetDouble(),
            (float)data.GetProperty("y").GetDouble());
        await Task.Delay(350, ct);
        return true;
    }

    // Match the real weekday positions from the live popup, then locate the
    // target NUMBER directly under its correct weekday. This avoids depending
    // on TikTok's undocumented calendar class names and avoids requiring 35
    // or 42 readable DOM cells (some are virtualized or nested).
    private static async Task<bool> ClickTikTokDateUnderWeekdayAsync(
        IPage page, ILocator dateTrigger, DateTime target,
        Action<string>? log, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var bounds = await dateTrigger.BoundingBoxAsync();
        if (bounds is null) return false;
        const string inspect = @"
(args) => {
  const labels=['sun','mon','tue','wed','thu','fri','sat'];
  const monthNames=['january','february','march','april','may','june',
    'july','august','september','october','november','december'];
  const visible=e=>{
    const r=e.getBoundingClientRect(), style=getComputedStyle(e);
    return r.width>3 && r.height>3 && style.display!=='none' &&
      style.visibility!=='hidden' && r.bottom>0 && r.top<innerHeight;
  };
  const text=e=>(e.textContent||'').trim();
  const leaf=e=>![...e.children].some(c=>text(c)===text(e));
  const all=[...document.querySelectorAll('body *')].filter(e=>visible(e)&&leaf(e))
    .map(e=>{const r=e.getBoundingClientRect();return {
      e,t:text(e),x:r.left+r.width/2,y:r.top+r.height/2,
      w:r.width,h:r.height};});
  const around=all.filter(e=>e.x>args.left-35 && e.x<args.left+520 &&
    e.y>args.top-620 && e.y<args.top-5);
  const months=around.filter(e=>monthNames.includes(e.t.toLowerCase()))
    .sort((a,b)=>b.y-a.y);
  // The calendar month must match the user-requested month, never select
  // day 30 in a stale calendar belonging to a different month.
  if(!months.length)return {error:'calendar month header is not readable'};
  const month=months[0];
  const shownMonth=monthNames.indexOf(month.t.toLowerCase())+1;
  if(shownMonth!==args.month)return {error:'wrong visible month: '+shownMonth};
  const yearLabels=around.filter(e=>/^20\d{2}$/.test(e.t) &&
    Math.abs(e.y-month.y)<36).sort((a,b)=>
      Math.abs(a.x-month.x)-Math.abs(b.x-month.x));
  if(yearLabels.length && Number(yearLabels[0].t)!==args.year)
    return {error:'wrong visible year '+yearLabels[0].t};
  const possible=around.filter(e=>['sun','mon','tue','wed','thu','thurs','fri','sat']
    .includes(e.t.toLowerCase()) && e.y>month.y+10 && e.y<month.y+110);
  const weekdayRows=[];
  for(const e of possible){
    const normalized=e.t.toLowerCase()==='thurs'?'thu':e.t.toLowerCase();
    let r=weekdayRows.find(r=>Math.abs(r.y-e.y)<13);
    if(!r) weekdayRows.push(r={y:e.y,values:[]});
    r.values.push({...e,t:normalized});
  }
  const week=weekdayRows.sort((a,b)=>b.values.length-a.values.length)[0];
  if(!week)return {error:'weekday header row absent'};
  const headers=[];
  for(const n of labels){
    const opts=week.values.filter(e=>e.t===n);
    if(opts.length!==1)return {error:'weekday '+n+' count='+opts.length};
    headers.push(opts[0]);
  }
  const weekday=(new Date(args.year,args.month-1,args.day)).getDay();
  const x=headers[weekday].x;
  // Require a literal target-day label in this weekday column, inside the
  // calendar between the weekday header and the date trigger.
  const choices=around.filter(e=>e.t===String(args.day) &&
    e.y>week.y+12 && e.y<args.top-9 &&
    Math.abs(e.x-x)<25 && e.w<100 && e.h<75)
    .sort((a,b)=>b.y-a.y);
  if(!choices.length) return {error:'day '+args.day+' not found under '+
    labels[weekday]+'; column x='+x.toFixed(1), weekday, x,
    labels:week.values.map(v=>v.t).join(',')};
  // Do not match a ghost duplicate from the prior or next month: the active
  // date will be in its mathematically expected week-row.
  const firstWeekday=new Date(args.year,args.month-1,1).getDay();
  const targetRow=Math.floor((firstWeekday+args.day-1)/7);
  const firstRowNumbers=around.filter(e=>/^\d{1,2}$/.test(e.t) &&
    e.y>week.y+12 && e.y<args.top-9 && e.x>=headers[0].x-22 &&
    e.x<=headers[6].x+22 && e.w<100 && e.h<75);
  const rowYs=[];
  for(const e of firstRowNumbers.sort((a,b)=>a.y-b.y))
    if(!rowYs.some(y=>Math.abs(y-e.y)<14)) rowYs.push(e.y);
  const expectedY=rowYs[targetRow];
  const candidate=choices.find(e=>expectedY!==undefined && Math.abs(e.y-expectedY)<15);
  if(!candidate)return {error:'target day not in expected calendar week',
    weekRow:targetRow, rowY:rowYs.join(','), dayYs:choices.map(x=>x.y).join(',')};
  return {found:true,x:candidate.x,y:candidate.y,
    targetRow,weekday,read:candidate.t,month:shownMonth,dayCount:choices.length};
}";
        var result = await page.EvaluateAsync<System.Text.Json.JsonElement>(inspect,
            new { left = bounds.X, top = bounds.Y, month = target.Month,
                year = target.Year, day = target.Day });
        if (result.TryGetProperty("error", out var err) &&
            err.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            log?.Invoke("TikTok: weekday-aligned date detection: " + err.GetString());
            return false;
        }
        if (!result.TryGetProperty("found", out var found) || !found.GetBoolean())
            return false;
        var x = (float)result.GetProperty("x").GetDouble();
        var y = (float)result.GetProperty("y").GetDouble();
        log?.Invoke("TikTok: exact calendar day " + target.Day +
            " verified at weekday " + result.GetProperty("weekday").GetInt32() +
            ", row " + result.GetProperty("targetRow").GetInt32() +
            "; clicking x=" + x + " y=" + y);
        await page.Mouse.ClickAsync(x, y);
        await Task.Delay(350, ct);
        return true;
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

    // Fallback for the visually rendered two-column TikTok wheel when its
    // DOM container classes are unavailable. Target only numeric leaf options
    // above the time trigger and choose the appropriate left/right column.
    private static async Task<bool> PickTikTokVisibleTimeColumnAsync(
        IPage page, ILocator trigger, int wanted, bool hour, Action<string>? log,
        CancellationToken ct)
    {
        var bounds = await trigger.BoundingBoxAsync();
        if (bounds is null) return false;
        const string script = @"
(args) => {
  const vis=e=>{let r=e.getBoundingClientRect(),s=getComputedStyle(e);
    return r.width>4&&r.height>4&&s.visibility!=='hidden'&&s.display!=='none';};
  const numeric=[];
  for(const e of document.querySelectorAll('body *')){
    if(!vis(e))continue;
    const t=(e.textContent||'').trim();
    if(!/^\d{1,2}$/.test(t))continue;
    if([...e.children].some(c=>(c.textContent||'').trim()===t))continue;
    const r=e.getBoundingClientRect();
    if(r.width>120||r.height>80)continue;
    const x=r.left+r.width/2,y=r.top+r.height/2;
    if(x<args.ax-140||x>args.ax+500||y<args.ay-520||y>args.ay-8)continue;
    numeric.push({n:Number(t),x,y});
  }
  const distinct=[];
  for(const e of numeric){
    if(!distinct.some(d=>Math.abs(d.x-e.x)<9&&Math.abs(d.y-e.y)<9))distinct.push(e);
  }
  const sortedX=distinct.map(e=>e.x).sort((a,b)=>a-b);
  if(sortedX.length<6)return {error:'numeric wheel options missing',count:sortedX.length};
  // Identify two center columns from the largest gap in x distribution.
  const groups=[];
  for(const x of sortedX){
    let g=groups.find(g=>Math.abs(g.x-x)<26);
    if(!g)groups.push({x,count:1});
    else {g.x=(g.x*g.count+x)/(g.count+1);g.count++;}
  }
  groups.sort((a,b)=>b.count-a.count);
  const best=groups.slice(0,2).sort((a,b)=>a.x-b.x);
  if(best.length!==2||best[1].x-best[0].x<60)
    return {error:'cannot identify two wheel columns',count:distinct.length};
  const center=best[args.hour?0:1].x;
  const options=distinct.filter(e=>Math.abs(e.x-center)<26 &&
    (args.hour?e.n<=23:e.n<=59&&e.n%5===0));
  const min=options.length?Math.min(...options.map(e=>e.n)):-1;
  const max=options.length?Math.max(...options.map(e=>e.n)):-1;
  const centerY=args.ay-190;
  const found=options.filter(e=>e.n===args.value)
    .sort((a,b)=>Math.abs(a.y-centerY)-Math.abs(b.y-centerY))[0];
  return {error:null,found:!!found,count:options.length,min,max,
    x:found?.x??center,y:found?.y??centerY};
}";
        for (var attempt = 0; attempt < 26; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var data = await page.EvaluateAsync<System.Text.Json.JsonElement>(script,
                new { ax = bounds.X, ay = bounds.Y, value = wanted, hour });
            if (data.TryGetProperty("error", out var failure) &&
                failure.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                log?.Invoke("TikTok: visual time column: " + failure.GetString());
                return false;
            }
            var x = (float)data.GetProperty("x").GetDouble();
            var y = (float)data.GetProperty("y").GetDouble();
            if (data.GetProperty("found").GetBoolean())
            {
                await page.Mouse.ClickAsync(x, y);
                await Task.Delay(300, ct);
                log?.Invoke("TikTok: visual wheel clicked " + (hour ? "hour " : "minute ") + wanted);
                return true;
            }
            var min = data.GetProperty("min").GetInt32();
            var max = data.GetProperty("max").GetInt32();
            if (attempt == 0) log?.Invoke("TikTok: visual " + (hour ? "hour" : "minute") +
                " wheel options " + min + "-" + max + ", requested " + wanted);
            var direction = min >= 0 && wanted < min ? -1 : 1;
            if (max >= 0 && wanted > max) direction = 1;
            if (attempt >= 15) direction = -direction;
            await page.Mouse.MoveAsync(x, y);
            await page.Mouse.WheelAsync(0, 130 * direction);
            await Task.Delay(200, ct);
        }
        log?.Invoke("TikTok: could not scroll to " + (hour ? "hour " : "minute ") + wanted);
        return false;
    }


    // Uploading a large TikTok MP4 happens asynchronously after attaching the
    // local file. Editing custom Schedule controls while its upload card still
    // reads "Uploading..." can be reset when the Studio UI rerenders.
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

        await SaveTikTokCalendarDomAsync(page, log);
        var selected = await SelectTikTokVisualCalendarAsync(page, dateTrigger, target, log, ct);
        if (!selected)
        {
            log?.Invoke("TikTok: grid geometry did not locate target; trying visible weekday columns.");
            selected = await SelectTikTokCalendarDayByWeekdayAsync(page, dateTrigger, target, log, ct);
        }
        if (!selected)
        {
            log?.Invoke("TikTok: trying exact day under visible weekday header.");
            selected = await ClickTikTokDateUnderWeekdayAsync(
                page, dateTrigger, target, log, ct);
        }
        if (!selected)
        {
            await SaveDiagnosticAsync(page, "TikTok-Visual-Calendar-Selection-Failed", log);
            await page.Keyboard.PressAsync("Escape");
            // Explicitly click OUTSIDE the date popup if Escape did not work.
            var bounds = await dateTrigger.BoundingBoxAsync();
            if (bounds is not null)
                await page.Mouse.ClickAsync(Math.Max(12, bounds.X - 50),
                    Math.Max(12, bounds.Y + bounds.Height + 65));
            return false;
        }
        // A day click may leave the popup visible briefly; dismiss it by
        // clicking an unquestionably outside area, not the toggle trigger.
        var rect = await dateTrigger.BoundingBoxAsync();
        if (rect is not null)
            await page.Mouse.ClickAsync(Math.Max(12, rect.X - 50),
                Math.Max(12, rect.Y + rect.Height + 65));
        await Task.Delay(300, ct);
        } // end date-change branch

        var selectedDate = (await dateTrigger.InnerTextAsync()).Trim();
        var dateOk = DateTime.TryParse(selectedDate, culture,
            System.Globalization.DateTimeStyles.None, out var parsedDate) && parsedDate.Date == target.Date;
        log?.Invoke("TikTok: date after click: " + selectedDate + ", expected " + wantedDate);
        if (!dateOk)
        {
            // One explicit retry using DOM weekday/day geometry. The first
            // popup click may land on a noninteractive wrapper or be cancelled
            // by TikTok while the video upload is still initializing.
            log?.Invoke("TikTok: date uncommitted; attempting single verified retry.");
            await dateTrigger.ClickAsync(new() { Timeout = 7000 });
            await Task.Delay(300, ct);
            await SaveTikTokCalendarDomAsync(page, log);
            var retry = await ClickTikTokDateUnderWeekdayAsync(
                page, dateTrigger, target, log, ct);
            if (retry)
            {
                await page.Keyboard.PressAsync("Escape");
                await Task.Delay(250, ct);
                selectedDate = (await dateTrigger.InnerTextAsync()).Trim();
                dateOk = DateTime.TryParse(selectedDate, culture,
                    System.Globalization.DateTimeStyles.None, out parsedDate) &&
                    parsedDate.Date == target.Date;
                log?.Invoke("TikTok: date after retry: " + selectedDate);
            }
            if (!dateOk)
            {
                await SaveDiagnosticAsync(page, "TikTok-Date-Uncommitted", log);
                return false;
            }
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

        var hourSet = false;
        var minuteSet = false;
        if (await picker.CountAsync() > 0)
        {
            hourSet = await PickTikTokWheelValueAsync(page, picker, target.Hour, true, log, ct);
            minuteSet = hourSet && await PickTikTokWheelValueAsync(
                page, picker, target.Minute, false, log, ct);
        }
        // A screenshot of the current TikTok Studio wheel shows two text-number
        // columns, but the prior class-based selector may not match either.
        if (!hourSet || !minuteSet)
        {
            log?.Invoke("TikTok: using visible hour/minute column fallback.");
            if (!hourSet)
                hourSet = await PickTikTokVisibleTimeColumnAsync(
                    page, timeTrigger, target.Hour, true, log, ct);
            if (hourSet && !minuteSet)
                minuteSet = await PickTikTokVisibleTimeColumnAsync(
                    page, timeTrigger, target.Minute, false, log, ct);
        }
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
                    if (!await WaitForTikTokUploadReadyAsync(page, log, ct))
                        return "TikTok: video upload is still in progress. Schedule selection was not attempted. " +
                            "Review the upload manually. No post has been submitted.";
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
