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



    private static async Task<bool> SetTikTokDateStrictAsync(
        IPage page, ILocator dateTrigger, DateTime target,
        Action<string>? log, CancellationToken ct)
    {
        var culture=System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var shown=(await dateTrigger.InnerTextAsync()).Trim();
        if(DateTime.TryParse(shown,culture,System.Globalization.DateTimeStyles.None,out var existing)
            && existing.Date==target.Date) return true;
        await dateTrigger.ClickAsync(new(){Timeout=7000});
        var calendar=page.Locator(".DatePicker__panel:visible").First;
        if(await calendar.CountAsync()==0)
        {
            log?.Invoke("TikTok: expected DatePicker__panel unavailable.");
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
        var selected=(await dateTrigger.InnerTextAsync()).Trim();
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
        IPage page,DateTimeOffset slot,Action<string>? log,CancellationToken ct)
    {
        var target=slot.LocalDateTime;
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
        log?.Invoke("TikTok: strictly setting "+target.ToString("yyyy-MM-dd HH:mm")+
            "; privacy and music controls must remain untouched.");

        var dateTrigger=page.Locator(".date-picker-input:visible").First;
        if(await dateTrigger.CountAsync()==0)
            dateTrigger=page.GetByText(new System.Text.RegularExpressions.Regex(
                @"[A-Za-z]{3}\s+\d{1,2},?\s+\d{4}")).First;
        if(await dateTrigger.CountAsync()==0||!await dateTrigger.IsVisibleAsync())
        {
            log?.Invoke("TikTok: date trigger not available.");return false;
        }
        var dateOk=await SetTikTokDateStrictAsync(page,dateTrigger,target,log,ct);
        if(!dateOk) await SaveTikTokPickerEvidenceAsync(page,"Date-Failed",log);

        // Date failure does not authorize any click outside the time field;
        // time is tested independently but inside its own verified popup.
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
      if(!/still checking your video|check is complete|potential issues/i.test(all))
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
            if (!filled)
                return "TikTok: caption entry FAILED. No post was submitted; review in TikTok Studio.";
            log?.Invoke("TikTok: caption entered; automatic final submission authorized in ZeroVideo.");
            try
            {
                if (!await WaitForTikTokUploadReadyAsync(page, log, ct))
                    return "TikTok: upload still processing; final submission NOT attempted.";
                if (scheduledAt.HasValue &&
                    !await SetTikTokScheduleAsync(page, scheduledAt.Value, log, ct))
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
