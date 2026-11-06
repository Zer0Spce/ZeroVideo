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

    private static async Task<bool> AttachVideoAsync(
        IPage page,string path,Action<string>? log,CancellationToken ct)
    {
        // Existing ZeroVideo Edge is attached by CDP, which has a 50 MB
        // Playwright driver transfer limit. Assign a local MP4 path through
        // Chrome DevTools instead, without uploading it through Playwright.
        var cdp=await page.Context.NewCDPSessionAsync(page);
        try
        {
            for(var poll=0;poll<35;poll++)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var doc=await cdp.SendAsync("DOM.getDocument",
                        new Dictionary<string,object>{{"depth",-1},{"pierce",true}});
                    if(doc is null)throw new InvalidOperationException("No CDP document.");
                    var root=doc.Value.GetProperty("root").GetProperty("nodeId").GetInt32();
                    var ids=await cdp.SendAsync("DOM.querySelectorAll",
                        new Dictionary<string,object>{
                            {"nodeId",root},{"selector","input[type=file]"}});
                    if(ids is not null)
                    {
                        foreach(var e in ids.Value.GetProperty("nodeIds").EnumerateArray())
                        {
                            var id=e.GetInt32();
                            var info=await cdp.SendAsync("DOM.describeNode",
                                new Dictionary<string,object>{{"nodeId",id}});
                            if(info is null)continue;
                            var attrs=info.Value.GetProperty("node").GetProperty("attributes");
                            string accept="";
                            for(var i=0;i+1<attrs.GetArrayLength();i+=2)
                                if(attrs[i].GetString()=="accept")
                                    accept=attrs[i+1].GetString()??"";
                            if(accept.Contains("image",StringComparison.OrdinalIgnoreCase)
                               && !accept.Contains("video",StringComparison.OrdinalIgnoreCase))
                                continue;
                            await cdp.SendAsync("DOM.setFileInputFiles",
                                new Dictionary<string,object>{
                                    {"files",new[]{path}},{"nodeId",id}});
                            log?.Invoke("YouTube Shorts: attached MP4 through local Edge DevTools.");
                            return true;
                        }
                    }
                }
                catch(PlaywrightException ex)
                {
                    if(poll==0||poll==20)
                        log?.Invoke("YouTube Shorts: file input still loading: "+
                            ex.Message.Split((char)10)[0]);
                }
                await Task.Delay(500,ct);
            }
            return false;
        }
        finally
        {
            await cdp.DetachAsync();
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
        try
        {
            await page.GotoAsync("https://studio.youtube.com/",new(){
                WaitUntil=WaitUntilState.DOMContentLoaded,Timeout=90000
            });
            if(!Uri.TryCreate(page.Url,UriKind.Absolute,out var address) ||
               !address.Host.Equals("studio.youtube.com",
                   StringComparison.OrdinalIgnoreCase))
                return "YouTube: NOT_SUBMITTED; sign in to YouTube Studio in the dedicated Edge profile first.";

            var create=page.GetByRole(AriaRole.Button,new(){NameRegex=CreateName});
            if(await create.CountAsync()<1)
                create=page.GetByText("Create",new(){Exact=true});
            if(await create.CountAsync()!=1 || !await create.First.IsVisibleAsync())
            {
                await SaveEvidenceAsync(page,"Create-Missing",log);
                return "YouTube: NOT_SUBMITTED; Create menu not visible. Sign in to Studio first.";
            }
            await create.First.ClickAsync(new(){Timeout=10000});

            var upload=page.GetByText("Upload videos",new(){Exact=true}).First;
            await upload.WaitForAsync(new(){
                State=WaitForSelectorState.Visible,Timeout=15000});
            await upload.ClickAsync(new(){Timeout=10000});
            var dialog=page.Locator("ytcp-uploads-dialog");
            await dialog.WaitForAsync(new(){
                State=WaitForSelectorState.Visible,Timeout=20000});

            if(!await AttachVideoAsync(page,videoPath,log,ct))
            {
                await SaveEvidenceAsync(page,"File-Input-Missing",log);
                return "YouTube: NOT_SUBMITTED; MP4 input not found.";
            }
            if(!await FillMetadataAsync(page,title,description,log,ct))
                return "YouTube: NOT_SUBMITTED; title or description could not be verified.";
            if(!await ChooseNotMadeForKidsAsync(page,log))
                return "YouTube: NOT_SUBMITTED; audience option not confirmed.";
            if(!await OpenVisibilityAsync(page,log,ct))
                return "YouTube: NOT_SUBMITTED; visibility setup not ready.";
            var visibilityOk=slot.HasValue
                ? await ChooseScheduleAsync(page,slot.Value,log,ct)
                : await ChoosePublicAsync(page,log);
            if(!visibilityOk)
                return "YouTube: NOT_SUBMITTED; visibility/date/time not verified.";
            return await SubmitOnceAsync(page,slot.HasValue,log,ct);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception ex) when(ex is PlaywrightException or InvalidOperationException
                                    or TimeoutException)
        {
            log?.Invoke("YouTube Shorts: stopped before confirming submission: "+
                ex.Message.Split((char)10)[0]);
            await SaveEvidenceAsync(page,"Workflow-Interrupted",log);
            return "YouTube: NOT_SUBMITTED_OR_UNCERTAIN; browser step failed. " +
                "Inspect YouTube Studio before retrying: "+ex.Message.Split((char)10)[0];
        }
    }
}
