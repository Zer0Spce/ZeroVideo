using System.Text.RegularExpressions;
using Microsoft.Playwright;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.Services;

/// <summary>
/// Browser-based YouTube Shorts uploader using the dedicated signed-in Edge
/// profile. Never touches TikTok's proven uploader. An uncertain final click
/// is never repeated; missing/ambiguous controls stop before submission.
/// </summary>
public sealed class YouTubeShortsPublishService(BrowserSession browser)
{
    private static readonly Regex CreateName = new(
        @"^\s*Create\s*$", RegexOptions.IgnoreCase);
    private static readonly Regex UploadName = new(
        @"^\s*Upload videos\s*$", RegexOptions.IgnoreCase);


    private static async Task<string> SaveEvidenceAsync(
        IPage page, string step, Action<string>? log)
    {
        var dir=Path.Combine(Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData),
            "ZeroVideo","PublishingDiagnostics");
        Directory.CreateDirectory(dir);
        var path=Path.Combine(dir,"YouTube-Shorts-"+step+"-"+
            DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+".png");
        try
        {
            await page.ScreenshotAsync(new() {
                Path=path,Timeout=10000,FullPage=true
            });
            log?.Invoke("YouTube Shorts: diagnostic screenshot saved: "+path);
            return path;
        }
        catch (PlaywrightException ex)
        {
            log?.Invoke("YouTube Shorts: screenshot unavailable: "+
                ex.Message.Split((char)10)[0]);
            return "(unavailable)";
        }
    }

    private static async Task SaveCreateControlsDiagnosticAsync(
        IPage page, string stage, Action<string>? log)
    {
        try
        {
            const string script = """
                () => {
                  const selectors = [
                    'ytcp-button#create-icon', '#create-icon',
                    "ytcp-button[aria-label='Create']",
                    "button[aria-label='Create']",
                    "[aria-label='Create']",
                    "tp-yt-paper-item[test-id='upload-beta']",
                    "tp-yt-paper-item[aria-label='Upload videos']"
                  ];
                  const entries=[];
                  for(const sel of selectors) {
                    for(const el of [...document.querySelectorAll(sel)].slice(0,4)){
                      const rect=el.getBoundingClientRect();
                      entries.push({selector:sel, tag:el.tagName,
                        text:(el.innerText||'').trim().slice(0,100),
                        visible:rect.width>0&&rect.height>0,
                        html:el.outerHTML.slice(0,850)});
                    }
                  }
                  return JSON.stringify({
                    pathname:location.pathname,readyState:document.readyState,
                    controls:entries
                  },null,2);
                }
                """;
            var content=await page.EvaluateAsync<string>(script);
            var dir=Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
                "ZeroVideo","PublishingDiagnostics");
            Directory.CreateDirectory(dir);
            var file=Path.Combine(dir,
                "YouTube-Shorts-Create-Controls-"+stage+"-"+
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+".txt");
            await File.WriteAllTextAsync(file,content);
            log?.Invoke("YouTube Shorts: scoped Create-control diagnostic: "+file);
        }
        catch(Exception ex) when(ex is PlaywrightException or IOException or
                                   UnauthorizedAccessException)
        {
            log?.Invoke("YouTube Shorts: Create-control diagnostic unavailable: "+
                ex.Message.Split((char)10)[0]);
        }
    }

    private static async Task<bool> OpenCreateMenuAsync(
        IPage page, Action<string>? log, CancellationToken ct)
    {
        // Navigation's DOMContentLoaded event precedes the Polymer dashboard
        // controls being mounted. Poll for the actual button for up to 25s.
        for(var poll=0;poll<100;poll++)
        {
            ct.ThrowIfCancellationRequested();
            foreach(var css in YouTubeStudioControlPolicy.CreateSelectors)
            {
                var button=page.Locator(css);
                if(await button.CountAsync()!=1 ||
                   !await button.IsVisibleAsync() ||
                   !await button.IsEnabledAsync())
                    continue;
                log?.Invoke("YouTube Shorts: Create button found using '"+css+"'.");
                try
                {
                    await button.ClickAsync(new(){Timeout=8000});
                    return true;
                }
                catch(PlaywrightException ex)
                {
                    log?.Invoke("YouTube Shorts: Create rerendered before click: "+
                        ex.Message.Split((char)10)[0]);
                    // Do not report the user as signed out; try again only
                    // while still safely before any upload or submission.
                }
            }

            // Older YouTube Studio renderings expose the button by ARIA,
            // but this is strictly a fallback, never the only lookup.
            var role=page.GetByRole(AriaRole.Button,
                new(){NameRegex=CreateName});
            if(await role.CountAsync()==1 && await role.IsVisibleAsync() &&
               await role.IsEnabledAsync())
            {
                try
                {
                    await role.ClickAsync(new(){Timeout=8000});
                    log?.Invoke("YouTube Shorts: Create menu opened by ARIA fallback.");
                    return true;
                }
                catch(PlaywrightException) { /* Dashboard is rerendering. */ }
            }
            if(poll<99)await Task.Delay(250,ct);
        }
        await SaveCreateControlsDiagnosticAsync(page,"Not-Found",log);
        await SaveEvidenceAsync(page,"Create-Missing",log);
        log?.Invoke("YouTube Shorts: Create control not detected after 25s. " +
            "Studio sign-in is NOT assumed to be missing.");
        return false;
    }

    private static async Task<bool> OpenUploadVideosAsync(
        IPage page, Action<string>? log, CancellationToken ct)
    {
        // The custom Studio menu item has a stable test-id. Avoid clicking
        // text somewhere unrelated to the opened Create menu.
        for(var poll=0;poll<60;poll++)
        {
            ct.ThrowIfCancellationRequested();
            foreach(var css in YouTubeStudioControlPolicy.UploadMenuSelectors)
            {
                var button=page.Locator(css);
                if(await button.CountAsync()!=1 ||
                   !await button.IsVisibleAsync() ||
                   !await button.IsEnabledAsync())
                    continue;
                log?.Invoke("YouTube Shorts: Upload videos found via '"+css+"'.");
                try
                {
                    await button.ClickAsync(new(){Timeout=8000});
                    return true;
                }
                catch(PlaywrightException)
                {
                    log?.Invoke("YouTube Shorts: Upload menu rerendered; retrying.");
                }
            }
            var menuItem=page.GetByRole(AriaRole.Menuitem,
                new(){NameRegex=UploadName});
            if(await menuItem.CountAsync()==1 && await menuItem.IsVisibleAsync())
            {
                await menuItem.ClickAsync(new(){Timeout=8000});
                log?.Invoke("YouTube Shorts: Upload videos selected via menu item.");
                return true;
            }
            if(poll<59)await Task.Delay(250,ct);
        }
        await SaveCreateControlsDiagnosticAsync(page,"Upload-Menu-Missing",log);
        await SaveEvidenceAsync(page,"Upload-Menu-Missing",log);
        return false;
    }

    /// <summary>
    /// YouTube's "Upload videos" window may be fully visible while its
    /// ytcp-uploads-dialog custom-element host still reports hidden.
    /// Wait for what the user actually sees: the Select files prompt.
    /// </summary>
    private static async Task<bool> WaitForUploadPickerAsync(
        IPage page, Action<string>? log, CancellationToken ct)
    {
        var selectFiles=page.GetByText(new Regex(
            @"^\s*Select files\s*$",RegexOptions.IgnoreCase));
        var dropHint=page.GetByText(new Regex(
            @"Drag and drop video files to upload",RegexOptions.IgnoreCase));
        for(var poll=0;poll<80;poll++)
        {
            ct.ThrowIfCancellationRequested();
            foreach(var locator in new[]{selectFiles,dropHint})
            {
                var count=Math.Min(await locator.CountAsync(),8);
                for(var i=0;i<count;i++)
                {
                    if(await locator.Nth(i).IsVisibleAsync())
                    {
                        log?.Invoke("YouTube Shorts: Upload videos picker is visibly " +
                            "open (Select files / drag-and-drop prompt detected).");
                        return true;
                    }
                }
            }
            if(poll<79) await Task.Delay(250,ct);
        }
        log?.Invoke("YouTube Shorts: upload picker did not show Select files " +
            "after 20 seconds, even though the Create menu was opened.");
        await SaveUploadStageDiagnosticAsync(page,"Picker-Not-Visible",log);
        await SaveEvidenceAsync(page,"Picker-Not-Visible",log);
        return false;
    }

    /// <summary>
    /// Capture the relevant upload dialog/inputs as TXT. The earlier .txt
    /// diagnostic only covered the Create button; this covers every later
    /// upload failure without including the page's private video metadata.
    /// </summary>
    private static async Task SaveUploadStageDiagnosticAsync(
        IPage page, string stage, Action<string>? log)
    {
        try
        {
            const string script = """
                () => {
                  const roots=[document], inputs=[], dialogs=[];
                  let shadowRoots=0;
                  for(let p=0;p<roots.length && p<150;p++){
                    const root=roots[p];
                    for(const n of root.querySelectorAll('input[type="file"]')){
                      const accept=n.getAttribute('accept')||'';
                      inputs.push({
                        accept,disabled:n.disabled,tag:n.tagName,
                        html:n.outerHTML.slice(0,550),
                        host:n.getRootNode()?.host?.tagName||null
                      });
                    }
                    for(const el of root.querySelectorAll('ytcp-uploads-dialog')){
                      const r=el.getBoundingClientRect();
                      dialogs.push({tag:el.tagName,visible:r.width>0&&r.height>0});
                    }
                    for(const el of root.querySelectorAll('*')){
                      if(el.shadowRoot){roots.push(el.shadowRoot);shadowRoots++;}
                    }
                  }
                  const signal=(text) => {
                    const walk=[document];
                    for(let i=0;i<walk.length&&i<150;i++){
                      const root=walk[i];
                      for(const el of root.querySelectorAll(
                        'button,ytcp-button,span,yt-formatted-string')){
                        if((el.innerText||'').trim()===text){
                          const r=el.getBoundingClientRect();
                          if(r.width>0&&r.height>0)return true;
                        }
                      }
                      for(const e of root.querySelectorAll('*'))
                        if(e.shadowRoot)walk.push(e.shadowRoot);
                    }
                    return false;
                  };
                  return JSON.stringify({
                    readyState:document.readyState,
                    dialogHosts:dialogs,shadowRoots,
                    selectFilesVisible:signal('Select files'),
                    fileInputCount:inputs.length,fileInputs:inputs.slice(0,20)
                  },null,2);
                }
                """;
            var details=await page.EvaluateAsync<string>(script);
            var dir=Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
                "ZeroVideo","PublishingDiagnostics");
            Directory.CreateDirectory(dir);
            var filepath=Path.Combine(dir,
                "YouTube-Shorts-Upload-"+stage+"-"+
                DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+".txt");
            await File.WriteAllTextAsync(filepath,details);
            log?.Invoke("YouTube Shorts: upload-stage TXT diagnostic saved: "+filepath);
        }
        catch(Exception ex) when(ex is PlaywrightException or IOException or
            UnauthorizedAccessException or InvalidOperationException)
        {
            log?.Invoke("YouTube Shorts: upload-stage TXT diagnostic failed: "+
                ex.Message.Split((char)10)[0]);
        }
    }

    private static async Task<bool> AttachVideoAsync(
        IPage page,string path,Action<string>? log,CancellationToken ct)
    {
        // Playwright's SetInputFiles may transfer the entire MP4 via its
        // protocol. For large videos, attach a LOCAL path directly through
        // Chrome DevTools (DOM.setFileInputFiles with a remote objectId).
        // Unlike DOM.querySelectorAll(document), Runtime.evaluate can find
        // a file input INSIDE nested YouTube Studio shadow roots.
        const string fileInputExpression = """
            (() => {
              const roots=[document], candidates=[];
              for(let p=0;p<roots.length && p<150;p++){
                const root=roots[p];
                for(const input of root.querySelectorAll('input[type="file"]'))
                  candidates.push(input);
                for(const el of root.querySelectorAll('*'))
                  if(el.shadowRoot)roots.push(el.shadowRoot);
              }
              const insideUploadDialog = input => {
                for(let el=input;el;){
                  if(el.tagName?.toLowerCase()==='ytcp-uploads-dialog')
                    return true;
                  const root=el.getRootNode();
                  el=el.parentElement||(root && root.host)||null;
                }
                return false;
              };
              const isVideoInput = input => {
                const accept=(input.getAttribute('accept')||'').toLowerCase();
                return accept.includes('video') || accept.includes('.mp4');
              };
              const eligible=candidates.filter(input=>{
                const accept=(input.getAttribute('accept')||'').toLowerCase();
                const imageOnly=accept.includes('image')&&!isVideoInput(input);
                return !input.disabled && !imageOnly &&
                  (insideUploadDialog(input)||isVideoInput(input));
              });
              // No arbitrary first match; ambiguous file inputs must not
              // attach a video to a thumbnail/caption picker.
              return eligible.length===1?eligible[0]:null;
            })()
            """;
        var cdp=await page.Context.NewCDPSessionAsync(page);
        try
        {
            var lastLog="";
            for(var poll=0;poll<50;poll++)
            {
                ct.ThrowIfCancellationRequested();
                System.Text.Json.JsonElement? remote=null;
                try
                {
                    remote=await cdp.SendAsync("Runtime.evaluate",
                        new Dictionary<string,object>{
                            {"expression",fileInputExpression},
                            {"returnByValue",false},
                            {"objectGroup","zerovideo-shorts-filepicker"}
                        });
                    if(remote is null ||
                        !remote.Value.TryGetProperty("result",out var result) ||
                        !result.TryGetProperty("objectId",out var objectId))
                    {
                        if(poll==0||poll==24||poll==49)
                            log?.Invoke("YouTube Shorts: upload dialog visible, " +
                                "waiting for an unambiguous MP4 file input.");
                        await Task.Delay(350,ct);
                        continue;
                    }
                    var remoteId=objectId.GetString();
                    if(string.IsNullOrWhiteSpace(remoteId))
                        throw new InvalidOperationException(
                            "YouTube file input did not have an element object ID.");

                    // ONE attachment command. If the browser fails at this
                    // point, do not automatically retry an uncertain upload.
                    log?.Invoke("YouTube Shorts: located MP4 input (including " +
                        "nested Studio components); assigning local file once.");
                    await cdp.SendAsync("DOM.setFileInputFiles",
                        new Dictionary<string,object>{
                            {"files",new[]{path}},{"objectId",remoteId}
                        });
                    log?.Invoke("YouTube Shorts: MP4 selected via local " +
                        "Edge DevTools; waiting for Studio metadata fields.");
                    return true;
                }
                catch(PlaywrightException ex)
                {
                    lastLog=ex.Message.Split((char)10)[0];
                    log?.Invoke("YouTube Shorts: local file selection failed: "+
                        lastLog+". Do not retry an uncertain attachment.");
                    break;
                }
                catch(InvalidOperationException ex)
                {
                    lastLog=ex.Message;
                    log?.Invoke("YouTube Shorts: "+lastLog);
                    break;
                }
            }
            await SaveUploadStageDiagnosticAsync(page,"File-Input-Not-Selected",log);
            return false;
        }
        finally
        {
            try { await cdp.DetachAsync(); }
            catch(PlaywrightException) { /* Session may already be closed. */ }
        }
    }

    private static async Task<bool> FillMetadataAsync(
        IPage page,string title,string description,Action<string>? log,
        CancellationToken ct)
    {
        var dialog=page.Locator("ytcp-uploads-dialog");
        var editors=dialog.Locator("ytcp-social-suggestions-textbox #textbox");
        for(var poll=0;poll<60;poll++)
        {
            ct.ThrowIfCancellationRequested();
            if(await editors.CountAsync()>=2 &&
               await editors.Nth(0).IsVisibleAsync() &&
               await editors.Nth(1).IsVisibleAsync())
            {
                await editors.Nth(0).FillAsync(title,new(){Timeout=10000});
                await editors.Nth(1).FillAsync(description,new(){Timeout=10000});
                var actualTitle=(await editors.Nth(0).InnerTextAsync()).Trim();
                if(!string.Equals(actualTitle,title,StringComparison.Ordinal))
                {
                    log?.Invoke("YouTube Shorts: title did not read back exactly. Not submitting.");
                    return false;
                }
                log?.Invoke($"YouTube Shorts: title verified ({title.Length}/100 characters); description entered.");
                return true;
            }
            await Task.Delay(500,ct);
        }
        log?.Invoke("YouTube Shorts: title/description editors not visible.");
        await SaveEvidenceAsync(page,"Metadata-Fields-Missing",log);
        return false;
    }

    private static async Task<bool> ChooseNotMadeForKidsAsync(
        IPage page,Action<string>? log)
    {
        var dialog=page.Locator("ytcp-uploads-dialog");
        var choice=dialog.GetByText(
            new Regex(@"No,?\s+it'?s\s+not\s+made\s+for\s+kids",
                RegexOptions.IgnoreCase)).First;
        if(await choice.CountAsync()!=1 || !await choice.IsVisibleAsync())
        {
            log?.Invoke("YouTube Shorts: audience 'not made for kids' option missing.");
            await SaveEvidenceAsync(page,"Audience-Missing",log);
            return false;
        }
        await choice.ClickAsync(new(){Timeout=8000});
        log?.Invoke("YouTube Shorts: user-authorized audience selected: not made for kids.");
        return true;
    }

    private static async Task<bool> OpenVisibilityAsync(
        IPage page,Action<string>? log,CancellationToken ct)
    {
        var dialog=page.Locator("ytcp-uploads-dialog");
        var visibility=dialog.Locator("ytcp-video-visibility-select");
        for(var step=0;step<4;step++)
        {
            ct.ThrowIfCancellationRequested();
            if(await visibility.CountAsync()==1 && await visibility.IsVisibleAsync())
            {
                log?.Invoke("YouTube Shorts: reached Visibility step.");
                return true;
            }
            var next=dialog.Locator("#next-button").First;
            if(await next.CountAsync()!=1 || !await next.IsVisibleAsync() ||
               !await next.IsEnabledAsync())
            {
                log?.Invoke("YouTube Shorts: Next is not available. " +
                    "Review required YouTube Studio checks; no post submitted.");
                await SaveEvidenceAsync(page,"Wizard-Next-Blocked",log);
                return false;
            }
            await next.ClickAsync(new(){Timeout=10000});
            await Task.Delay(550,ct);
            log?.Invoke("YouTube Shorts: advanced upload wizard step "+(step+1)+".");
        }
        if(await visibility.CountAsync()==1 && await visibility.IsVisibleAsync())
            return true;
        await SaveEvidenceAsync(page,"Visibility-Missing",log);
        return false;
    }

    private static async Task<bool> ChoosePublicAsync(
        IPage page,Action<string>? log)
    {
        var visibility=page.Locator("ytcp-uploads-dialog ytcp-video-visibility-select");
        var radio=visibility.Locator(
            "tp-yt-paper-radio-button[name='PUBLIC'], " +
            "[role='radio'][name='PUBLIC']").First;
        if(await radio.CountAsync()!=1 || !await radio.IsVisibleAsync())
        {
            log?.Invoke("YouTube Shorts: the Public visibility radio was not found.");
            await SaveEvidenceAsync(page,"Public-Option-Missing",log);
            return false;
        }
        await radio.ClickAsync(new(){Timeout=7000});
        if((await radio.GetAttributeAsync("aria-checked"))!="true" &&
           (await radio.GetAttributeAsync("checked")) is null &&
           (await radio.GetAttributeAsync("selected")) is null)
        {
            log?.Invoke("YouTube Shorts: Public was clicked but not verified as selected.");
            await SaveEvidenceAsync(page,"Public-Not-Selected",log);
            return false;
        }
        log?.Invoke("YouTube Shorts: Public visibility selected and checked.");
        return true;
    }

    private static async Task<bool> ChooseScheduleAsync(
        IPage page,DateTimeOffset target,Action<string>? log,CancellationToken ct)
    {
        var visibility=page.Locator("ytcp-uploads-dialog ytcp-video-visibility-select");
        var radio=visibility.Locator(
            "tp-yt-paper-radio-button[name='SCHEDULE'], " +
            "[role='radio'][name='SCHEDULE']").First;
        if(await radio.CountAsync()!=1 || !await radio.IsVisibleAsync())
        {
            log?.Invoke("YouTube Shorts: Schedule radio is missing or ambiguous.");
            await SaveEvidenceAsync(page,"Schedule-Option-Missing",log);
            return false;
        }
        await radio.ClickAsync(new(){Timeout=8000});
        await Task.Delay(550,ct);

        // Unlike TikTok, YouTube Studio uses its own date/time widgets and
        // shows a channel timezone. Fill ONLY inputs under Visibility. Never
        // locate a date by searching caption text across the page.
        var date=visibility.Locator(
            "ytcp-date-picker input:visible, " +
            "input[aria-label*='Date' i]:visible, " +
            "input[type=date]:visible").First;
        var time=visibility.Locator(
            "ytcp-time-of-day-picker input:visible, " +
            "input[aria-label*='Time' i]:visible, " +
            "input[type=time]:visible").First;
        if(await date.CountAsync()!=1 || await time.CountAsync()!=1 ||
           !await date.IsVisibleAsync() || !await time.IsVisibleAsync())
        {
            log?.Invoke("YouTube Shorts: schedule date/time widgets aren't " +
                "editable or are not uniquely found. No final click.");
            await SaveEvidenceAsync(page,"Schedule-Date-Time-Missing",log);
            return false;
        }

        var dateValue=target.LocalDateTime.ToString("MMM d, yyyy",
            System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        var timeValue=target.LocalDateTime.ToString("h:mm tt",
            System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        await date.FillAsync(dateValue,new(){Timeout=8000});
        await date.PressAsync("Tab");
        await time.FillAsync(timeValue,new(){Timeout=8000});
        await time.PressAsync("Tab");
        await Task.Delay(450,ct);
        var readDate=await date.InputValueAsync();
        var readTime=await time.InputValueAsync();

        // YouTube may normalize date/time formats. Parse instead of comparing
        // unnormalized strings and NEVER accept a previous video's date.
        var culture=System.Globalization.CultureInfo.GetCultureInfo("en-US");
        var sameDate=DateTime.TryParse(readDate,culture,
            System.Globalization.DateTimeStyles.None,out var parsedDate) &&
            parsedDate.Date==target.LocalDateTime.Date;
        var sameTime=DateTime.TryParse(readTime,culture,
            System.Globalization.DateTimeStyles.None,out var parsedTime) &&
            parsedTime.Hour==target.LocalDateTime.Hour &&
            parsedTime.Minute==target.LocalDateTime.Minute;
        if(!sameDate||!sameTime)
        {
            log?.Invoke("YouTube Shorts: schedule readback mismatch; expected="+
                target.LocalDateTime.ToString("yyyy-MM-dd HH:mm")+
                "; Studio date='"+readDate+"', time='"+readTime+"'. " +
                "No final click.");
            await SaveEvidenceAsync(page,"Schedule-Readback-Mismatch",log);
            return false;
        }
        log?.Invoke("YouTube Shorts: date/time readback verified: "+
            target.LocalDateTime.ToString("yyyy-MM-dd HH:mm")+
            ". Confirm Studio's displayed timezone matches your intended timezone.");
        return true;
    }

    private static async Task<string> SubmitOnceAsync(
        IPage page,bool scheduled,Action<string>? log,CancellationToken ct)
    {
        var dialog=page.Locator("ytcp-uploads-dialog");
        var final=dialog.Locator("#done-button").First;
        if(await final.CountAsync()!=1 || !await final.IsVisibleAsync() ||
            !await final.IsEnabledAsync())
        {
            log?.Invoke("YouTube Shorts: final button missing or disabled. No submission attempted.");
            await SaveEvidenceAsync(page,"Final-Button-Missing",log);
            return "YouTube: NOT_SUBMITTED; final button missing or disabled.";
        }
        var label=(await final.InnerTextAsync()).Trim();
        if(!Regex.IsMatch(label,scheduled
            ? @"^(Schedule|Save)$" : @"^(Publish|Save)$",RegexOptions.IgnoreCase))
        {
            log?.Invoke("YouTube Shorts: unexpected final button label '"+label+
                "'; no click attempted.");
            await SaveEvidenceAsync(page,"Final-Button-Unexpected",log);
            return "YouTube: NOT_SUBMITTED; unexpected final action.";
        }

        // Guard exactly-once semantics. Once attempted, even a browser error
        // is ambiguous and must not cause an automatic retry.
        log?.Invoke("YouTube Shorts: clicking the verified final '"+label+
            "' button ONCE; no automatic retries.");
        try
        {
            await final.ClickAsync(new(){Timeout=10000,NoWaitAfter=true});
        }
        catch(Exception ex) when(ex is PlaywrightException or TimeoutException)
        {
            log?.Invoke("YouTube Shorts: final click attempted; outcome uncertain: "+
                ex.Message.Split((char)10)[0]);
            await SaveEvidenceAsync(page,"Final-Click-Uncertain",log);
            return "YouTube: SUBMISSION_ATTEMPTED; final click uncertain. " +
                "Check YouTube Studio before trying again.";
        }

        var receipt=page.GetByText(new Regex(
            scheduled
                ? @"(?:video\s+scheduled|scheduled\s+for|successfully\s+scheduled)"
                : @"(?:video\s+published|published\s+successfully|your\s+video\s+is\s+live)",
            RegexOptions.IgnoreCase));
        for(var poll=0;poll<24;poll++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if(await receipt.CountAsync()>0 && await receipt.First.IsVisibleAsync())
                {
                    log?.Invoke("YouTube Shorts: explicit confirmation displayed.");
                    return "YouTube: SUBMISSION_CONFIRMED; "+
                        (scheduled?"scheduled":"published")+".";
                }
            }
            catch(PlaywrightException) {}
            await Task.Delay(500,ct);
        }
        log?.Invoke("YouTube Shorts: final click performed once, but no explicit " +
            "confirmation was detected. Verify Studio before retrying.");
        await SaveEvidenceAsync(page,"Confirmation-Unknown",log);
        return "YouTube: SUBMISSION_ATTEMPTED; confirmation unavailable. " +
            "Review YouTube Studio Content before retrying.";
    }

    public async Task<string> PublishAsync(
        ZeroVideoSettings settings,string videoPath,string title,string description,
        bool confirmNotForKids,DateTimeOffset? slot,Action<string>? log,
        CancellationToken ct)
    {
        var metadataError=YouTubeShortsPolicy.ValidateMetadata(title,description);
        if(metadataError is not null)return "YouTube: NOT_SUBMITTED; "+metadataError;
        if(!confirmNotForKids)
            return "YouTube: NOT_SUBMITTED; confirm the videos are not made for kids.";
        var videoError=await YouTubeShortsPolicy.ValidateVideoAsync(
            videoPath,settings.FfprobePath,ct);
        if(videoError is not null)return "YouTube: NOT_SUBMITTED; "+videoError;
        if(slot.HasValue && slot.Value<=DateTimeOffset.Now.AddMinutes(20))
            return "YouTube: NOT_SUBMITTED; scheduled date must be in the future.";

        await browser.EnsureStartedAsync(settings);
        // Each video uses a fresh tab, with the existing authenticated Edge
        // profile. No state from the previous YouTube or TikTok upload is reused.
        var page=await browser.Context.NewPageAsync();
        var stage="Studio-Dashboard";
        try
        {
            await page.GotoAsync("https://studio.youtube.com/",new(){
                WaitUntil=WaitUntilState.DOMContentLoaded,Timeout=90000
            });
            if(!Uri.TryCreate(page.Url,UriKind.Absolute,out var address) ||
               !address.Host.Equals("studio.youtube.com",
                   StringComparison.OrdinalIgnoreCase))
                return "YouTube: NOT_SUBMITTED; sign in to YouTube Studio in the dedicated Edge profile first.";

            log?.Invoke("YouTube Shorts: Studio dashboard loaded at "+
                page.Url+". Looking for the Create toolbar control.");
            stage="Create-Menu";
            if(!await OpenCreateMenuAsync(page,log,ct))
                return "YouTube: NOT_SUBMITTED; Studio Create control not found. " +
                    "You may already be signed in. See Create-control diagnostics.";
            stage="Upload-Videos-Menu";
            if(!await OpenUploadVideosAsync(page,log,ct))
                return "YouTube: NOT_SUBMITTED; Create opened, but Upload videos " +
                    "menu item was not found. See the Upload-Menu diagnostic.";
            stage="Upload-Picker";
            log?.Invoke("YouTube Shorts: stage=upload-picker-ready; " +
                "waiting for visible Select files control (not dialog host).");
            if(!await WaitForUploadPickerAsync(page,log,ct))
                return "YouTube: NOT_SUBMITTED; upload picker not visibly ready. " +
                    "See YouTube-Shorts-Upload-Picker diagnostics.";

            stage="Attach-MP4";
            log?.Invoke("YouTube Shorts: stage=attach-mp4.");
            if(!await AttachVideoAsync(page,videoPath,log,ct))
            {
                await SaveEvidenceAsync(page,"File-Input-Missing",log);
                return "YouTube: NOT_SUBMITTED; MP4 could not be selected. " +
                    "See YouTube-Shorts-Upload-File-Input-Not-Selected diagnostics.";
            }
            stage="Fill-Metadata";
            if(!await FillMetadataAsync(page,title,description,log,ct))
                return "YouTube: NOT_SUBMITTED; title or description could not be verified.";
            stage="Audience";
            if(!await ChooseNotMadeForKidsAsync(page,log))
                return "YouTube: NOT_SUBMITTED; audience option not confirmed.";
            stage="Visibility-Wizard";
            if(!await OpenVisibilityAsync(page,log,ct))
                return "YouTube: NOT_SUBMITTED; visibility setup not ready.";
            stage="Visibility-Selection";
            var visibilityOk=slot.HasValue
                ? await ChooseScheduleAsync(page,slot.Value,log,ct)
                : await ChoosePublicAsync(page,log);
            if(!visibilityOk)
                return "YouTube: NOT_SUBMITTED; visibility/date/time not verified.";
            stage="Final-Submission";
            return await SubmitOnceAsync(page,slot.HasValue,log,ct);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex) when(ex is PlaywrightException or InvalidOperationException
                                    or TimeoutException)
        {
            log?.Invoke("YouTube Shorts: failure at stage="+stage+"; "+
                ex.Message[..Math.Min(1200,ex.Message.Length)]);
            await SaveUploadStageDiagnosticAsync(page,
                "Workflow-Failed-"+stage,log);
            await SaveEvidenceAsync(page,"Workflow-Interrupted-"+stage,log);
            // Earlier stages cannot publish; an exception once the final
            // submission stage is entered must be considered uncertain.
            return stage=="Final-Submission"
                ? "YouTube: SUBMISSION_ATTEMPTED; final-stage outcome uncertain. " +
                    "Check YouTube Studio before retrying."
                : "YouTube: NOT_SUBMITTED; failed at "+stage+
                    ". Check the YouTube-Shorts-Upload-Workflow-Failed-*.txt diagnostic.";
        }
    }
}
