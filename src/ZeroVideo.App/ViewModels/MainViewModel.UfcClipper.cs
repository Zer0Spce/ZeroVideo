using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Input;
using Microsoft.Win32;
using ZeroVideo.App.Infrastructure;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.ViewModels;

public sealed class UfcClipperEntry : ObservableObject
{
    public int PlayerId { get; set; }
    public string Name { get; set; } = "";
    public string Concept { get; set; } = "";
    public string Mode { get; set; } = "Fighter Ratings";
    public string Team { get; set; } = "";
    public double[] Scores { get; set; } = [];
    public string SeoTitle { get; set; } = "";
    public string SeoCaption { get; set; } = "";
    public string SeoHashtags { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string ThumbnailPath { get; set; } = "";
    private bool _checked = true;
    private string _status = "IdeasReady";
    public bool IsChecked { get => _checked; set => Set(ref _checked, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Label => Name + " — " + Concept;
}

public sealed class UfcClipperHistoryItem
{
    public int PlayerId { get; set; }
    public string Name { get; set; } = "";
    public string Concept { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class UfcClipperState
{
    public string Niche { get; set; } = "";
    public string Format { get; set; } = "Fighter Ratings";
    public string ChatUrl { get; set; } = "";
    public string SourceFolder { get; set; } = "";
    public string SoundFile { get; set; } = "";
    public string FirstSlot { get; set; } = "";
    public bool AutoDownload { get; set; } = true;
    public bool BrowserLogin { get; set; }
    public bool Publish { get; set; } = true;
    public double MusicLevel { get; set; } = .85;
    public List<UfcClipperEntry> Ideas { get; set; } = [];
    public List<UfcClipperHistoryItem> History { get; set; } = [];
}

public sealed partial class MainViewModel
{
    private static readonly string UfcRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZeroVideo", "UFCClipper");
    private static readonly string UfcStatePath = Path.Combine(UfcRoot, "state.json");
    private string _ufcNiche = "UFC and MMA fighters and champions";
    private string _ufcFormat = "Fighter Ratings";
    private int _ufcCount = 10;
    private string _ufcSourceFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "ZeroVideo", "UFCHighlights");
    private string _ufcSoundFile = "";
    private string _ufcStatus = "Ready: ChatGPT player ideas and scores → individual MMA fighter sanctioned highlights → beat-synced edit → TikTok queue.";
    private string _ufcFirstSlot = DateTime.Now.AddHours(3).ToString("yyyy-MM-dd HH:00",CultureInfo.InvariantCulture);
    private bool _ufcPublish = true;
    private bool _ufcAutoDownload = true;
    private bool _ufcBrowserLogin;
    private bool _ufcNewChat;
    private bool _ufcProcessing;
    private bool _ufcRetryFailed;
    private double _ufcMusicLevel = 0.85;
    private VideoProject _ufcChat = new() { Title = "UFCClipper batch", Topic = "Individual UFC MMA fighter videos" };
    private readonly List<UfcClipperHistoryItem> _ufcHistory = [];
    public ObservableCollection<UfcClipperEntry> UfcClipperIdeas { get; } = [];
    public ObservableCollection<UfcClipperHistoryItem> UfcHistory { get; } = [];
    public ObservableCollection<string> UfcFormats { get; } = ["Fighter Ratings", "Knockouts", "Fighter Legacy"];
    public string UfcNiche { get => _ufcNiche; set => Set(ref _ufcNiche,value); }
    public string UfcFormat { get => _ufcFormat; set => Set(ref _ufcFormat,value); }
    public int UfcCount { get => _ufcCount; set => Set(ref _ufcCount,Math.Clamp(value,1,20)); }
    public string UfcSourceFolder { get => _ufcSourceFolder; set => Set(ref _ufcSourceFolder,value); }
    public string UfcSoundFile { get => _ufcSoundFile; set => Set(ref _ufcSoundFile,value); }
    public string UfcStatus { get => _ufcStatus; set => Set(ref _ufcStatus,value); }
    public string UfcFirstSlot { get => _ufcFirstSlot; set => Set(ref _ufcFirstSlot,value); }
    public bool UfcPublish { get => _ufcPublish; set => Set(ref _ufcPublish,value); }
    public bool UfcAutoDownload { get => _ufcAutoDownload; set => Set(ref _ufcAutoDownload,value); }
    public bool UfcBrowserLogin { get => _ufcBrowserLogin; set => Set(ref _ufcBrowserLogin,value); }
    public bool UfcNewChat { get => _ufcNewChat; set => Set(ref _ufcNewChat,value); }
    public double UfcMusicLevel { get => _ufcMusicLevel; set => Set(ref _ufcMusicLevel,Math.Clamp(value,0,1)); }
    public string UfcHistorySummary => UfcHistory.Count+" finished player concepts in UFC history";
    public string UfcCheckpointStatus => UfcClipperIdeas.Count+" saved player ideas; "+UfcStatePath;
    public string UfcExportFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "ZeroVideo","UFCClipper");
    public ICommand UfcGenerateCommand { get; private set; } = null!;
    public ICommand UfcStartCommand { get; private set; } = null!;
    public ICommand UfcResumeCommand { get; private set; } = null!;
    public ICommand UfcRetryFailedCommand { get; private set; } = null!;
    public ICommand UfcBrowseSourceCommand { get; private set; } = null!;
    public ICommand UfcBrowseSoundCommand { get; private set; } = null!;
    public ICommand UfcFindHighlightsCommand { get; private set; } = null!;
    public ICommand UfcOpenOutputCommand { get; private set; } = null!;
    public ICommand UfcForceStopCommand => ForceStopJobCommand;

    private void InitializeUfcClipperCommands()
    {
        UfcRestore();
        UfcGenerateCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "UFCClipper: individual player ideas and SEO",UfcGenerateAsync));
        UfcStartCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "UFCClipper: one-click individual MMA fighter sanctioned highlights",UfcStartAsync,publishing:UfcPublish));
        UfcResumeCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "UFCClipper: resume",async()=>{
                UfcRestore();
                _ufcRetryFailed=false;
                await UfcStartAsync();
            },publishing:UfcPublish));
        UfcRetryFailedCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "UFCClipper: retry failures",async()=>{
                UfcRestore();
                _ufcRetryFailed=true;
                try{await UfcStartAsync();}
                finally{_ufcRetryFailed=false;}
            },publishing:UfcPublish));
        UfcBrowseSourceCommand=new RelayCommand(()=>{
            var picker=new OpenFolderDialog{Title="UFC player highlight source/cache folder"};
            if(picker.ShowDialog()==true){UfcSourceFolder=picker.FolderName;UfcSave();}
        });
        UfcBrowseSoundCommand=new RelayCommand(()=>{
            var picker=new OpenFileDialog{
                Filter="Audio files|*.mp3;*.m4a;*.wav;*.aac;*.flac",
                CheckFileExists=true};
            if(picker.ShowDialog()==true){UfcSoundFile=picker.FileName;UfcSave();}
        });
        UfcFindHighlightsCommand=new RelayCommand(()=>{
            var player=UfcClipperIdeas.FirstOrDefault(x=>x.IsChecked);
            if(player is null){UfcStatus="Select an UFC player first.";return;}
            var query=player.Name+" UFC individual highlights";
            Process.Start(new ProcessStartInfo(
                "https://www.youtube.com/results?search_query="+Uri.EscapeDataString(query))
                {UseShellExecute=true});
            UfcStatus="Opened player-specific highlights search. Use footage you are authorized to edit and post.";
        });
        UfcOpenOutputCommand=new RelayCommand(()=>{
            Directory.CreateDirectory(UfcExportFolder);
            Process.Start(new ProcessStartInfo("explorer.exe","\""+UfcExportFolder+"\"")
                {UseShellExecute=true});
        });
    }

    private void UfcSave()
    {
        Directory.CreateDirectory(UfcRoot);
        var snapshot=new UfcClipperState{
            Niche=UfcNiche,Format=UfcFormat,
            ChatUrl=_ufcChat.ChatGptConversationUrl??"",
            SourceFolder=UfcSourceFolder,SoundFile=UfcSoundFile,
            FirstSlot=UfcFirstSlot,AutoDownload=UfcAutoDownload,
            BrowserLogin=UfcBrowserLogin,Publish=UfcPublish,
            MusicLevel=UfcMusicLevel,
            Ideas=UfcClipperIdeas.ToList(),
            History=_ufcHistory.ToList()
        };
        var path=UfcStatePath+".tmp";
        File.WriteAllText(path,JsonSerializer.Serialize(snapshot,new JsonSerializerOptions{
            WriteIndented=true}));
        File.Move(path,UfcStatePath,true);
        Raise(nameof(UfcCheckpointStatus));
        Raise(nameof(UfcHistorySummary));
    }

    private void UfcRestore()
    {
        try
        {
            if(!File.Exists(UfcStatePath))return;
            var state=JsonSerializer.Deserialize<UfcClipperState>(
                File.ReadAllText(UfcStatePath));
            if(state is null)return;
            if(state.Niche.Length>0)UfcNiche=state.Niche;
            if(UfcFormats.Contains(state.Format))UfcFormat=state.Format;
            if(!string.IsNullOrWhiteSpace(state.SourceFolder))
                UfcSourceFolder=state.SourceFolder;
            if(!string.IsNullOrWhiteSpace(state.SoundFile))
                UfcSoundFile=state.SoundFile;
            // Keep the future default slot instead of restoring a time in
            // the past from a previous session.
            if(DateTime.TryParseExact(state.FirstSlot,"yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture,DateTimeStyles.AssumeLocal,
                out var restoredSlot)&&restoredSlot>DateTime.Now.AddMinutes(25))
                UfcFirstSlot=state.FirstSlot;
            UfcAutoDownload=state.AutoDownload;
            UfcBrowserLogin=state.BrowserLogin;
            UfcPublish=state.Publish;
            UfcMusicLevel=state.MusicLevel;
            _ufcChat=new VideoProject{
                Title="UFCClipper batch",Topic=UfcNiche,
                ChatGptConversationUrl=string.IsNullOrWhiteSpace(state.ChatUrl)?null:state.ChatUrl};
            UfcClipperIdeas.Clear();
            foreach(var item in state.Ideas)UfcClipperIdeas.Add(item);
            _ufcHistory.Clear();
            _ufcHistory.AddRange(state.History);
            UfcHistory.Clear();
            foreach(var item in _ufcHistory.OrderByDescending(x=>x.CompletedAt))
                UfcHistory.Add(item);
            Raise(nameof(UfcHistorySummary));
            Raise(nameof(UfcCheckpointStatus));
        }
        catch(Exception ex){UfcStatus="UFC checkpoint load failed: "+ex.Message;}
    }

    private void UfcPrepareChat()
    {
        if(UfcNewChat)
        {
            _ufcChat=new VideoProject{Title="UFCClipper batch: "+UfcNiche,Topic=UfcNiche};
            _chat.RequestNewChat();
            UfcNewChat=false;
            UfcSave();
        }
    }

    private static string UfcJson(JsonElement node,string key)=>
        node.ValueKind==JsonValueKind.Object &&
        node.TryGetProperty(key,out var field)&&field.ValueKind==JsonValueKind.String?
        field.GetString()??"":"";

    private static int UfcId(string name,string concept)
    {
        unchecked{
            uint h=2166136261;
            foreach(var c in ("Ufc|"+name+"|"+concept).ToUpperInvariant()){
                h^=c;h*=16777619;
            }
            return (int)(h&0x7fffffff);
        }
    }

    private async Task UfcGenerateAsync()
    {
        try
        {
            UfcStatus="ChatGPT: generating individual UFC player concepts, six ratings and SEO in ONE request";
            UfcPrepareChat();
            var completed=_ufcHistory.Select(x=>x.Name+" — "+x.Concept)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(200);
            var prompt="Generate exactly "+UfcCount+" REAL individual UFC MMA fighter ideas, one subject per video, no teams, duos or comparisons. "+
                "Niche: "+UfcNiche+". Video format: "+UfcFormat+". "+
                "Avoid completed name/concept pairs: "+string.Join("; ",completed)+". "+
                "For anime and gaming characters identify the canonical series or game in concept; for real people avoid invented achievements. "+
                "In the SAME response generate six SUBJECTIVE scores 0.0-10.0 for: striking, grappling, power, speed, defense, fightiq. "+
                "Those are editorial ratings, never official statistics or sourced review scores. "+
                "Write one accurate SEO title (40-85 chars), short caption (100-240 chars) and 3-5 hashtags. "+
                "Use only genuine known subjects; do not invent people, titles, games, broadcasts, stats or achievements. "+
                "Return ONLY a valid JSON object inside fenced json: "+
                "{\"response_type\":\"ufcclipper_ideas_v1\",\"players\":[{\"name\":\"Jon Jones\",\"concept\":\"Iconic individual highlight moments\",\"scores\":{\"striking\":8,\"grappling\":8,\"power\":8,\"speed\":8,\"defense\":8,\"fightiq\":8},\"seo\":{\"title\":\"Individual character or person highlights and ratings\",\"caption\":\"A short-form individual spotlight with subjective category scores.\",\"hashtags\":\"#Highlights #Clips #Edits\"}}]}";
            var raw=await _chat.GenerateTextAsync(_ufcChat,Settings,prompt,Log,_cts.Token);
            var first=raw.IndexOf('{');var last=raw.LastIndexOf('}');
            if(first<0||last<=first)throw new InvalidDataException("ChatGPT did not return JSON.");
            using var json=JsonDocument.Parse(raw.Substring(first,last-first+1));
            var players=json.RootElement.GetProperty("players");
            if(players.ValueKind!=JsonValueKind.Array)
                throw new InvalidDataException("Expected players JSON array.");
            var ready=new List<UfcClipperEntry>();
            var seen=new HashSet<int>();
            foreach(var candidate in players.EnumerateArray().Take(UfcCount*3))
            {
                if(candidate.ValueKind!=JsonValueKind.Object)continue;
                var name=UfcJson(candidate,"name").Trim();
                var concept=UfcJson(candidate,"concept").Trim();
                if(name.Length is < 4 or > 100||concept.Length is < 4 or > 130||
                    name.Any(char.IsControl))continue;
                var id=UfcId(name,concept);
                if(!seen.Add(id)||_ufcHistory.Any(x=>x.PlayerId==id)||
                    PublishingQueue.Any(x=>x.VideoPath.Contains(
                        id.ToString(CultureInfo.InvariantCulture)+"_",
                        StringComparison.OrdinalIgnoreCase)))continue;
                if(!candidate.TryGetProperty("scores",out var scores)||
                    scores.ValueKind!=JsonValueKind.Object)continue;
                string[] keys=["striking","grappling","power","speed","defense","fightiq"];
                var values=keys.Select(k=>scores.TryGetProperty(k,out var value)&&
                    value.ValueKind==JsonValueKind.Number?
                    value.GetDouble():double.NaN).ToArray();
                if(values.Any(x=>!double.IsFinite(x)||x<0||x>10))continue;
                var seo=candidate.TryGetProperty("seo",out var info)&&
                    info.ValueKind==JsonValueKind.Object?info:default;
                ready.Add(new UfcClipperEntry{
                    PlayerId=id,Name=name,Concept=concept,Mode=UfcFormat,
                    Scores=values.Select(x=>Math.Round(x,1)).ToArray(),
                    SeoTitle=UfcJson(seo,"title"),SeoCaption=UfcJson(seo,"caption"),
                    SeoHashtags=UfcJson(seo,"hashtags")
                });
                if(ready.Count>=UfcCount)break;
            }
            if(ready.Count==0)throw new InvalidDataException("No valid UFC player ideas received.");
            UfcClipperIdeas.Clear();
            foreach(var item in ready)UfcClipperIdeas.Add(item);
            UfcSave();
            UfcStatus=ready.Count+" individual UFC player ideas ready. "+
                "Ratings are subjective; actual UFC statistics are not invented.";
        }
        catch(OperationCanceledException){UfcSave();throw;}
        catch(Exception ex){UfcStatus="UFC idea generation failed: "+ex.Message;Log(UfcStatus);}
    }

    private static string? UfcCachedSource(string folder,UfcClipperEntry player)
    {
        if(!Directory.Exists(folder))return null;
        var exact=Path.Combine(folder,player.PlayerId+".mp4");
        if(File.Exists(exact)&&new FileInfo(exact).Length>10000)return exact;
        var needle=Regex.Replace(player.Name.ToLowerInvariant(),@"[^a-z0-9]","");
        return Directory.EnumerateFiles(folder).FirstOrDefault(f=>
        {
            if(!new[]{".mp4",".mkv",".mov",".webm"}.Contains(
                Path.GetExtension(f).ToLowerInvariant()))return false;
            var file=Regex.Replace(Path.GetFileNameWithoutExtension(f).ToLowerInvariant(),
                @"[^a-z0-9]","");
            return file.Contains(needle,StringComparison.Ordinal);
        });
    }

    private async Task<string> UfcDownloadAsync(UfcClipperEntry player)
    {
        Directory.CreateDirectory(UfcSourceFolder);
        var target=Path.Combine(UfcSourceFolder,player.PlayerId+".mp4");
        var downloader=MovieDownloaderPath();
        var search=player.Name+" UFC MMA fighter best knockouts highlights";
        player.Status="Finding player-specific YouTube highlights";
        var args=new List<string>{"--no-playlist","--flat-playlist","--dump-single-json",
            "--skip-download","--no-warnings","ytsearch12:"+search};
        if(UfcBrowserLogin)args.InsertRange(0,["--cookies-from-browser","edge"]);
        var result=await MovieRunProcessAsync(downloader,args,_cts.Token);
        if(result.Code!=0)throw new InvalidOperationException("YouTube highlight search: "+
            MovieDownloaderError(result.Error));
        using var json=JsonDocument.Parse(result.Output);
        if(!json.RootElement.TryGetProperty("entries",out var entries)||
            entries.ValueKind!=JsonValueKind.Array)
            throw new InvalidDataException("No individual MMA fighter sanctioned highlights found on YouTube.");
        var names=player.Name.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        var sources=new List<(string Url,int Priority)>();
        foreach(var e in entries.EnumerateArray())
        {
            if(e.ValueKind!=JsonValueKind.Object)continue;
            var title=UfcJson(e,"title");
            var videoId=UfcJson(e,"id");
            var lower=title.ToLowerInvariant();
            if(videoId.Length!=11||
                !videoId.All(c=>char.IsAsciiLetterOrDigit(c)||c=='-'||c=='_')||
                !names.All(n=>title.Contains(n,StringComparison.OrdinalIgnoreCase))||
                !new[]{"highlight","knockout","submission","best fights","fight moments"}
                    .Any(lower.Contains)||
                new[]{"interview","reaction","podcast","breakdown","2k","gameplay","simulation","fan made","fanmade"}
                    .Any(lower.Contains))continue;
            sources.Add(("https://www.youtube.com/watch?v="+videoId,
                (lower.Contains("nba")?10:0)+(lower.Contains("official")?5:0)));
        }
        if(sources.Count==0)throw new InvalidOperationException(
            "No UFC MMA fighter highlight source found. Import an authorized local clip.");
        var pattern=Path.Combine(UfcSourceFolder,player.PlayerId+".%(ext)s");
        var failures=new List<string>();
        foreach(var url in sources.OrderByDescending(x=>x.Priority)
            .Select(x=>x.Url).Distinct().Take(4))
        {
            _cts.Token.ThrowIfCancellationRequested();
            var download=new List<string>{"--no-playlist","--no-progress",
                "--retries","2","-f","bv*+ba/b","-S","res:1080,ext:mp4",
                "--merge-output-format","mp4","--recode-video","mp4",
                "-o",pattern,url};
            if(UfcBrowserLogin)download.InsertRange(0,["--cookies-from-browser","edge"]);
            if(!string.IsNullOrWhiteSpace(Settings.FfmpegPath)&&File.Exists(Settings.FfmpegPath))
                download.InsertRange(0,["--ffmpeg-location",Settings.FfmpegPath]);
            var fetched=await MovieRunProcessAsync(downloader,download,_cts.Token);
            if(fetched.Code==0&&File.Exists(target)&&new FileInfo(target).Length>10000)
                return target;
            failures.Add(MovieDownloaderError(fetched.Error));
        }
        throw new InvalidOperationException("Highlight download failed: "+
            string.Join("; ",failures.TakeLast(2))+
            ". Use a permitted local MP4 and click Retry Failed.");
    }

    private async Task<PublishQueueItem> UfcRenderAsync(UfcClipperEntry player,string sourcePath)
    {
        var music=UfcSoundFile;
        if(!File.Exists(music))throw new FileNotFoundException("Choose a music track.",music);
        var audio=await MovieProbeAsync(music);
        if(!audio.HasAudio||audio.Seconds is < 8 or > 120)
            throw new InvalidDataException("Music must contain 8–120 seconds of audio.");
        var clip=await MovieProbeAsync(sourcePath);
        var seconds=audio.Seconds;
        var frames=Math.Max(1,(int)Math.Round(seconds*30,MidpointRounding.AwayFromZero));
        var exact=frames/30d;
        var ffmpeg=!string.IsNullOrWhiteSpace(Settings.FfmpegPath)&&
            File.Exists(Settings.FfmpegPath)?Settings.FfmpegPath:"ffmpeg";
        var work=Path.Combine(UfcRoot,player.PlayerId+"-"+
            DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(UfcExportFolder);
        var cover=Path.Combine(work,"thumbnail.jpg");
        var video=Path.Combine(UfcExportFolder,player.PlayerId+"_"+
            DateTime.Now.ToString("yyyyMMdd_HHmmssfff")+".mp4");
        // Preserve the full landscape highlight in the center. Only the
        // enlarged duplicate behind it is blurred to fill a vertical canvas.
        const string verticalCover =
            "split=2[bgraw][fgraw];" +
            "[bgraw]scale=720:1280:force_original_aspect_ratio=increase," +
            "crop=720:1280,boxblur=20:2[bg];" +
            "[fgraw]scale=720:1280:force_original_aspect_ratio=decrease[fg];" +
            "[bg][fg]overlay=(W-w)/2:(H-h)/2:format=auto,format=yuv420p";
        var thumb=await MovieRunProcessAsync(ffmpeg,
            ["-y","-ss","1","-i",sourcePath,"-frames:v","1",
             "-filter_complex",verticalCover,cover],_cts.Token);
        if(thumb.Code!=0||!File.Exists(cover))
            throw new IOException("Could not generate the player thumbnail.");
        var timeline=await MovieBeatTimelineAsync(ffmpeg,music,exact);
        var cuts=await MovieSceneCutsAsync(ffmpeg,sourcePath,clip.Seconds);
        var count=Math.Max(1,timeline.Count-1);
        var offsets=new List<double>();
        for(var i=0;i<count;i++){
            var duration=timeline[i+1]-timeline[i];
            var max=Math.Max(0,clip.Seconds-duration-.1);
            var desired=max*(i+.35)/count;
            var best=cuts.Where(t=>Math.Abs(t-desired)<
                Math.Max(3,clip.Seconds/(count*3.0)))
                .OrderBy(t=>Math.Abs(t-desired)).FirstOrDefault();
            offsets.Add(Math.Clamp(best>0?best:desired,0,max));
        }
        var titleFile=Path.Combine(work,"player-title.txt");
        await File.WriteAllTextAsync(titleFile,player.Name,_cts.Token);
        await File.WriteAllTextAsync(Path.Combine(work,"edit-plan.json"),
            JsonSerializer.Serialize(new{player=player.Name,player.Concept,
                source=sourcePath,timeline,offsets,subjectiveScores=player.Scores,
                overall=Math.Round(player.Scores.Average(),1)}),_cts.Token);
        var args=new List<string>{"-y"};
        for(var i=0;i<count;i++)args.AddRange([
            "-ss",MovieNumber(offsets[i]),"-t",
            MovieNumber(timeline[i+1]-timeline[i]),"-i",sourcePath]);
        args.AddRange(["-i",music]);
        // Unlike MovieClipper, UFCClipper must never crop basketball
        // gameplay to 9:16. Keep the entire original horizontal frame.
        // Fill TikTok's 1080x1920 canvas with a blurred, enlarged duplicate.
        var filters=new List<string>();
        for(var i=0;i<count;i++)
        {
            var dur=MovieNumber(timeline[i+1]-timeline[i]);
            filters.Add("["+i+":v]fps=30,trim=duration="+dur+
                ",setpts=PTS-STARTPTS,split=2[bgsrc"+i+"][fgsrc"+i+"]");
            filters.Add("[bgsrc"+i+"]scale=1080:1920:"+
                "force_original_aspect_ratio=increase,crop=1080:1920,"+
                "boxblur=24:2,setsar=1[bg"+i+"]");
            filters.Add("[fgsrc"+i+"]scale=1080:1920:"+
                "force_original_aspect_ratio=decrease,setsar=1[fg"+i+"]");
            filters.Add("[bg"+i+"][fg"+i+"]overlay=(W-w)/2:(H-h)/2:"+
                "shortest=1:format=auto,format=yuv420p[v"+i+"]");
        }
        filters.Add(string.Concat(Enumerable.Range(0,count).Select(i=>"[v"+i+"]"))+
            "concat=n="+count+":v=1:a=0[montage]");
        filters.Add("[montage]tpad=stop_mode=clone:stop_duration=1,"+
            "trim=duration="+MovieNumber(exact)+",setpts=PTS-STARTPTS[exact]");
        var ratingsMode=player.Mode=="Fighter Ratings";
        var labels=ratingsMode
            ?new[]{"STRIKING","GRAPPLING","POWER","SPEED","DEFENSE","FIGHT IQ","OVERALL"}
            :player.Mode=="Knockouts"
                ?new[]{"FIGHT 01","FIGHT 02","FIGHT 03","FIGHT 04","FIGHT 05","FIGHT 06","KNOCKOUTS"}
                :new[]{"THE FIGHTER","CAREER HIGHLIGHTS","ICONIC FIGHTS","THE JOURNEY","GREAT MOMENTS","THE LEGEND","FIGHTER LEGACY"};
        var values=player.Scores.Concat([Math.Round(player.Scores.Average(),1)]).ToArray();
        var intro=ratingsMode?"UFC FIGHTER RATINGS":
            player.Mode=="Knockouts"?"KNOCKOUTS":"FIGHTER LEGACY";
        var overlays=new List<string>{
            "drawtext=text='"+intro+"':fontsize=95:fontcolor=white:"+
            "borderw=5:bordercolor=black:x=(w-text_w)/2:y=(h-text_h)/2:"+
            "enable='between(t,0,2)'",
            "drawtext=textfile=player-title.txt:fontsize=75:fontcolor=white:"+
            "borderw=5:bordercolor=black:x=(w-text_w)/2:y=(h-text_h)/2:"+
            "enable='between(t,2.1,5.3)'"
        };
        var froms=new[]{5.3,8.6,11.9,15.2,18.5,21.8,25.1}
            .Select(x=>x/30.0*exact).ToArray();
        for(var i=0;i<labels.Length;i++){
            var from=froms[i];
            var to=i<labels.Length-1?froms[i+1]-.05:exact-.1;
            overlays.Add("drawtext=text='"+labels[i]+"'"+
                ":fontsize="+(labels[i].Length>15?65:95)+
                ":fontcolor=white:borderw=5:bordercolor=black:"+
                "x=(w-text_w)/2:y=(h-text_h)/2-48:"+
                "enable='between(t,"+MovieNumber(from)+","+MovieNumber(to)+")'");
            if(ratingsMode)
                overlays.Add("drawtext=text='"+values[i].ToString("0.0",CultureInfo.InvariantCulture)+
                    "/10':fontsize=78:fontcolor=white:borderw=4:bordercolor=black:"+
                    "x=(w-text_w)/2:y=(h-text_h)/2+75:"+
                    "enable='between(t,"+MovieNumber(from+Math.Min(.95,(to-from)*.4))+
                    ","+MovieNumber(to)+")'");
        }
        filters.Add("[exact]"+string.Join(",",overlays)+"[v]");
        filters.Add("["+count+":a]atrim=duration="+MovieNumber(seconds)+
            ",asetpts=PTS-STARTPTS,volume="+MovieNumber(UfcMusicLevel)+"[a]");
        args.AddRange(["-filter_complex",string.Join(";",filters),
            "-map","[v]","-map","[a]","-t",MovieNumber(seconds),
            "-frames:v",frames.ToString(CultureInfo.InvariantCulture),
            "-c:v","libx264","-preset","veryfast","-crf","21",
            "-c:a","aac","-b:a","160k","-pix_fmt","yuv420p",
            "-movflags","+faststart",video]);
        player.Status="Rendering "+count+" beat-synced individual MMA fighter sanctioned highlights";
        var result=await MovieRunProcessAsync(ffmpeg,args,_cts.Token,work);
        if(result.Code!=0)
            throw new InvalidOperationException("FFmpeg: "+
                result.Error[^Math.Min(result.Error.Length,900)..]);
        if(!File.Exists(video)||new FileInfo(video).Length<10000)
            throw new IOException("UFCClipper video file was not generated.");
        player.OutputPath=video;
        player.ThumbnailPath=cover;
        UfcSave();
        return new PublishQueueItem{
            VideoPath=video,ThumbnailPath=cover,
            Title=player.Name+" — "+player.Concept,Description=player.Concept,
            TikTokCaption=player.Name+" UFC player ratings #UFC #Basketball",
            YouTubeTitle=player.Name+" — "+player.Concept+" #Shorts",
            YouTubeDescription=player.Concept
        };
    }

    private static void UfcEnsureSeo(UfcClipperEntry player)
    {
        if(player.SeoTitle.Length is < 15 or > 120 ||
            !player.SeoTitle.Contains(player.Name,StringComparison.OrdinalIgnoreCase))
            player.SeoTitle=player.Name+" — "+player.Concept+" | UFC Player Review";
        if(player.SeoCaption.Length is < 25 or > 400)
            player.SeoCaption=player.Name+" UFC player spotlight: "+player.Concept+
                ". Shooting, finishing, handles, playmaking, defense and IQ scored out of 10.";
        var tags=Regex.Matches(player.SeoHashtags,
            @"(?<!\S)#[A-Za-z][A-Za-z0-9_]{1,24}(?=\s|$)")
            .Select(x=>x.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        if(tags.Count<3)tags=["#UFC","#Basketball","#PlayerRatings"];
        player.SeoHashtags=string.Join(" ",tags);
    }

    private async Task UfcStartAsync()
    {
        if(_ufcProcessing)return;
        _ufcProcessing=true;
        UfcPrepareChat();
        try
        {
            var ideasWereEmpty=UfcClipperIdeas.Count==0;
            if(ideasWereEmpty)await UfcGenerateAsync();
            var selected=UfcClipperIdeas.Where(x=>x.IsChecked&&
                (!_ufcRetryFailed||ideasWereEmpty||x.Status.StartsWith("Failed",
                    StringComparison.OrdinalIgnoreCase))).ToList();
            if(selected.Count==0){
                UfcStatus="No pending UFC MMA fighter ideas selected.";return;
            }
            if(!DateTime.TryParseExact(UfcFirstSlot,"yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture,DateTimeStyles.AssumeLocal,out var first)||
                first<=DateTime.Now.AddMinutes(25)||first.Minute%5!=0)
                throw new InvalidDataException("First posting slot must be 25+ minutes away, on 5-minute increments.");
            if(!File.Exists(UfcSoundFile))
                throw new FileNotFoundException("Select a background soundtrack first.",UfcSoundFile);
            var originallyChecked=PublishingQueue.Where(x=>x.IsChecked).ToList();
            var completed=new List<PublishQueueItem>();
            foreach(var player in selected)
            {
                _cts.Token.ThrowIfCancellationRequested();
                try
                {
                    if(_ufcHistory.Any(h=>h.PlayerId==player.PlayerId)||
                        (!string.IsNullOrWhiteSpace(player.OutputPath)&&
                         PublishingQueue.Any(q=>q.VideoPath.Equals(player.OutputPath,
                             StringComparison.OrdinalIgnoreCase))))
                    {
                        player.Status="Already queued (no duplicate)";
                        player.IsChecked=false;UfcSave();continue;
                    }
                    var source=File.Exists(player.SourcePath)?player.SourcePath:
                        UfcCachedSource(UfcSourceFolder,player);
                    if(source is null&&UfcAutoDownload)
                        source=await UfcDownloadAsync(player);
                    if(source is null)
                    {
                        player.Status="Failed: player highlight source unavailable";
                        UfcSave();continue;
                    }
                    player.SourcePath=source;player.Status="HighlightsReady";UfcSave();
                    var output=File.Exists(player.OutputPath)?
                        new PublishQueueItem{VideoPath=player.OutputPath,
                            ThumbnailPath=player.ThumbnailPath}:
                        await UfcRenderAsync(player,source);
                    UfcEnsureSeo(player);
                    output.Title=player.SeoTitle;
                    output.Description=player.SeoCaption;
                    output.TikTokCaption=player.SeoTitle+"\n"+
                        player.SeoCaption+"\n"+player.SeoHashtags;
                    output.YouTubeTitle=player.SeoTitle;
                    output.YouTubeDescription=player.SeoCaption;
                    output.IsChecked=true;output.IsScheduled=true;
                    var slot=first.AddMinutes(SelectedPublishIntervalMinutes*completed.Count);
                    output.ScheduleDateText=slot.ToString("yyyy-MM-dd HH:mm");
                    output.ScheduledAt=new DateTimeOffset(slot);
                    output.MetadataStatus="Ready";
                    if(PublishingQueue.Any(q=>q.VideoPath.Equals(output.VideoPath,
                        StringComparison.OrdinalIgnoreCase)))continue;
                    TrackPublishingItem(output);
                    PublishingQueue.Add(output);
                    player.OutputPath=output.VideoPath;
                    player.ThumbnailPath=output.ThumbnailPath;
                    completed.Add(output);
                    player.Status="Queued "+output.ScheduleDateText;
                    UfcSave();SavePublishingQueue();
                    var history=new UfcClipperHistoryItem{
                        PlayerId=player.PlayerId,Name=player.Name,Concept=player.Concept,
                        OutputPath=output.VideoPath};
                    _ufcHistory.Add(history);
                    UfcHistory.Insert(0,history);
                    UfcSave();
                }
                catch(OperationCanceledException){player.Status="Stopped";UfcSave();throw;}
                catch(Exception ex){
                    player.Status="Failed: "+ex.Message;Log("UFCClipper "+player.Name+": "+ex.Message);
                    UfcSave();
                }
            }
            Raise(nameof(AllPublishItemsChecked));
            RefreshPublishingCalendar();
            RefreshVideoLibrary();
            UfcStatus="Exported "+completed.Count+"/"+selected.Count+
                " individual UFC player videos to "+UfcExportFolder+".";
            if(completed.Count==0||!UfcPublish)return;
            if(!PublishConsent){
                UfcStatus+=" Enable existing publishing authorization to auto-post.";
                return;
            }
            foreach(var old in originallyChecked)old.IsChecked=false;
            try{await RunTikTokBatchAsync();}
            finally{foreach(var old in originallyChecked)old.IsChecked=true;}
        }
        catch(OperationCanceledException){UfcStatus="Stopped. Resume saved UFC workflow.";UfcSave();throw;}
        catch(Exception ex){UfcStatus="UFCClipper: "+ex.Message;Log(UfcStatus);}
        finally{_ufcProcessing=false;UfcSave();}
    }
}
