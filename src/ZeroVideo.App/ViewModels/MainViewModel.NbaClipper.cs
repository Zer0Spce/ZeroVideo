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

public sealed class NbaClipperEntry : ObservableObject
{
    public int PlayerId { get; set; }
    public string Name { get; set; } = "";
    public string Concept { get; set; } = "";
    public string Mode { get; set; } = "Player Ratings";
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

public sealed class NbaClipperHistoryItem
{
    public int PlayerId { get; set; }
    public string Name { get; set; } = "";
    public string Concept { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class NbaClipperState
{
    public string Niche { get; set; } = "";
    public string Format { get; set; } = "Player Ratings";
    public string ChatUrl { get; set; } = "";
    public string SourceFolder { get; set; } = "";
    public string SoundFile { get; set; } = "";
    public string FirstSlot { get; set; } = "";
    public bool AutoDownload { get; set; } = true;
    public bool BrowserLogin { get; set; }
    public bool Publish { get; set; } = true;
    public double MusicLevel { get; set; } = .85;
    public List<NbaClipperEntry> Ideas { get; set; } = [];
    public List<NbaClipperHistoryItem> History { get; set; } = [];
}

public sealed partial class MainViewModel
{
    private static readonly string NbaRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZeroVideo", "NBAClipper");
    private static readonly string NbaStatePath = Path.Combine(NbaRoot, "state.json");
    private string _nbaNiche = "Current NBA stars and legendary players";
    private string _nbaFormat = "Player Ratings";
    private int _nbaCount = 10;
    private string _nbaSourceFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "ZeroVideo", "NBAHighlights");
    private string _nbaSoundFile = "";
    private string _nbaStatus = "Ready: ChatGPT player ideas and scores → player highlights → beat-synced edit → TikTok queue.";
    private string _nbaFirstSlot = DateTime.Now.AddHours(3).ToString("yyyy-MM-dd HH:00",CultureInfo.InvariantCulture);
    private bool _nbaPublish = true;
    private bool _nbaAutoDownload = true;
    private bool _nbaBrowserLogin;
    private bool _nbaNewChat;
    private bool _nbaProcessing;
    private bool _nbaRetryFailed;
    private double _nbaMusicLevel = 0.85;
    private VideoProject _nbaChat = new() { Title = "NBAClipper batch", Topic = "Individual NBA player videos" };
    private readonly List<NbaClipperHistoryItem> _nbaHistory = [];
    public ObservableCollection<NbaClipperEntry> NbaClipperIdeas { get; } = [];
    public ObservableCollection<NbaClipperHistoryItem> NbaHistory { get; } = [];
    public ObservableCollection<string> NbaFormats { get; } = ["Player Ratings", "Best Plays", "Player Legacy"];
    public string NbaNiche { get => _nbaNiche; set => Set(ref _nbaNiche,value); }
    public string NbaFormat { get => _nbaFormat; set => Set(ref _nbaFormat,value); }
    public int NbaCount { get => _nbaCount; set => Set(ref _nbaCount,Math.Clamp(value,1,20)); }
    public string NbaSourceFolder { get => _nbaSourceFolder; set => Set(ref _nbaSourceFolder,value); }
    public string NbaSoundFile { get => _nbaSoundFile; set => Set(ref _nbaSoundFile,value); }
    public string NbaStatus { get => _nbaStatus; set => Set(ref _nbaStatus,value); }
    public string NbaFirstSlot { get => _nbaFirstSlot; set => Set(ref _nbaFirstSlot,value); }
    public bool NbaPublish { get => _nbaPublish; set => Set(ref _nbaPublish,value); }
    public bool NbaAutoDownload { get => _nbaAutoDownload; set => Set(ref _nbaAutoDownload,value); }
    public bool NbaBrowserLogin { get => _nbaBrowserLogin; set => Set(ref _nbaBrowserLogin,value); }
    public bool NbaNewChat { get => _nbaNewChat; set => Set(ref _nbaNewChat,value); }
    public double NbaMusicLevel { get => _nbaMusicLevel; set => Set(ref _nbaMusicLevel,Math.Clamp(value,0,1)); }
    public string NbaHistorySummary => NbaHistory.Count+" finished player concepts in NBA history";
    public string NbaCheckpointStatus => NbaClipperIdeas.Count+" saved player ideas; "+NbaStatePath;
    public string NbaExportFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "ZeroVideo","NBAClipper");
    public ICommand NbaGenerateCommand { get; private set; } = null!;
    public ICommand NbaStartCommand { get; private set; } = null!;
    public ICommand NbaResumeCommand { get; private set; } = null!;
    public ICommand NbaRetryFailedCommand { get; private set; } = null!;
    public ICommand NbaBrowseSourceCommand { get; private set; } = null!;
    public ICommand NbaBrowseSoundCommand { get; private set; } = null!;
    public ICommand NbaFindHighlightsCommand { get; private set; } = null!;
    public ICommand NbaOpenOutputCommand { get; private set; } = null!;
    public ICommand NbaForceStopCommand => ForceStopJobCommand;

    private void InitializeNbaClipperCommands()
    {
        NbaRestore();
        NbaGenerateCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "NBAClipper: individual player ideas and SEO",NbaGenerateAsync));
        NbaStartCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "NBAClipper: one-click player highlights",NbaStartAsync,publishing:NbaPublish));
        NbaResumeCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "NBAClipper: resume",async()=>{
                NbaRestore();
                _nbaRetryFailed=false;
                await NbaStartAsync();
            },publishing:NbaPublish));
        NbaRetryFailedCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "NBAClipper: retry failures",async()=>{
                NbaRestore();
                _nbaRetryFailed=true;
                try{await NbaStartAsync();}
                finally{_nbaRetryFailed=false;}
            },publishing:NbaPublish));
        NbaBrowseSourceCommand=new RelayCommand(()=>{
            var picker=new OpenFolderDialog{Title="NBA player highlight source/cache folder"};
            if(picker.ShowDialog()==true){NbaSourceFolder=picker.FolderName;NbaSave();}
        });
        NbaBrowseSoundCommand=new RelayCommand(()=>{
            var picker=new OpenFileDialog{
                Filter="Audio files|*.mp3;*.m4a;*.wav;*.aac;*.flac",
                CheckFileExists=true};
            if(picker.ShowDialog()==true){NbaSoundFile=picker.FileName;NbaSave();}
        });
        NbaFindHighlightsCommand=new RelayCommand(()=>{
            var player=NbaClipperIdeas.FirstOrDefault(x=>x.IsChecked);
            if(player is null){NbaStatus="Select an NBA player first.";return;}
            var query=player.Name+" NBA individual highlights";
            Process.Start(new ProcessStartInfo(
                "https://www.youtube.com/results?search_query="+Uri.EscapeDataString(query))
                {UseShellExecute=true});
            NbaStatus="Opened player-specific highlights search. Use footage you are authorized to edit and post.";
        });
        NbaOpenOutputCommand=new RelayCommand(()=>{
            Directory.CreateDirectory(NbaExportFolder);
            Process.Start(new ProcessStartInfo("explorer.exe","\""+NbaExportFolder+"\"")
                {UseShellExecute=true});
        });
    }

    private void NbaSave()
    {
        Directory.CreateDirectory(NbaRoot);
        var snapshot=new NbaClipperState{
            Niche=NbaNiche,Format=NbaFormat,
            ChatUrl=_nbaChat.ChatGptConversationUrl??"",
            SourceFolder=NbaSourceFolder,SoundFile=NbaSoundFile,
            FirstSlot=NbaFirstSlot,AutoDownload=NbaAutoDownload,
            BrowserLogin=NbaBrowserLogin,Publish=NbaPublish,
            MusicLevel=NbaMusicLevel,
            Ideas=NbaClipperIdeas.ToList(),
            History=_nbaHistory.ToList()
        };
        var path=NbaStatePath+".tmp";
        File.WriteAllText(path,JsonSerializer.Serialize(snapshot,new JsonSerializerOptions{
            WriteIndented=true}));
        File.Move(path,NbaStatePath,true);
        Raise(nameof(NbaCheckpointStatus));
        Raise(nameof(NbaHistorySummary));
    }

    private void NbaRestore()
    {
        try
        {
            if(!File.Exists(NbaStatePath))return;
            var state=JsonSerializer.Deserialize<NbaClipperState>(
                File.ReadAllText(NbaStatePath));
            if(state is null)return;
            if(state.Niche.Length>0)NbaNiche=state.Niche;
            if(NbaFormats.Contains(state.Format))NbaFormat=state.Format;
            if(!string.IsNullOrWhiteSpace(state.SourceFolder))
                NbaSourceFolder=state.SourceFolder;
            if(!string.IsNullOrWhiteSpace(state.SoundFile))
                NbaSoundFile=state.SoundFile;
            // Keep the future default slot instead of restoring a time in
            // the past from a previous session.
            if(DateTime.TryParseExact(state.FirstSlot,"yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture,DateTimeStyles.AssumeLocal,
                out var restoredSlot)&&restoredSlot>DateTime.Now.AddMinutes(25))
                NbaFirstSlot=state.FirstSlot;
            NbaAutoDownload=state.AutoDownload;
            NbaBrowserLogin=state.BrowserLogin;
            NbaPublish=state.Publish;
            NbaMusicLevel=state.MusicLevel;
            _nbaChat=new VideoProject{
                Title="NBAClipper batch",Topic=NbaNiche,
                ChatGptConversationUrl=string.IsNullOrWhiteSpace(state.ChatUrl)?null:state.ChatUrl};
            NbaClipperIdeas.Clear();
            foreach(var item in state.Ideas)NbaClipperIdeas.Add(item);
            _nbaHistory.Clear();
            _nbaHistory.AddRange(state.History);
            NbaHistory.Clear();
            foreach(var item in _nbaHistory.OrderByDescending(x=>x.CompletedAt))
                NbaHistory.Add(item);
            Raise(nameof(NbaHistorySummary));
            Raise(nameof(NbaCheckpointStatus));
        }
        catch(Exception ex){NbaStatus="NBA checkpoint load failed: "+ex.Message;}
    }

    private void NbaPrepareChat()
    {
        if(NbaNewChat)
        {
            _nbaChat=new VideoProject{Title="NBAClipper batch: "+NbaNiche,Topic=NbaNiche};
            _chat.RequestNewChat();
            NbaNewChat=false;
            NbaSave();
        }
    }

    private static string NbaJson(JsonElement node,string key)=>
        node.ValueKind==JsonValueKind.Object &&
        node.TryGetProperty(key,out var field)&&field.ValueKind==JsonValueKind.String?
        field.GetString()??"":"";

    private static int NbaId(string name,string concept)
    {
        unchecked{
            uint h=2166136261;
            foreach(var c in (name+"|"+concept).ToUpperInvariant()){
                h^=c;h*=16777619;
            }
            return (int)(h&0x7fffffff);
        }
    }

    private async Task NbaGenerateAsync()
    {
        try
        {
            NbaStatus="ChatGPT: generating individual NBA player concepts, six ratings and SEO in ONE request";
            NbaPrepareChat();
            var completed=_nbaHistory.Select(x=>x.Name+" — "+x.Concept)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(200);
            var prompt="Generate exactly "+NbaCount+" REAL individual NBA PLAYER short-video ideas. "+
                "Niche: "+NbaNiche+". Video format: "+NbaFormat+". "+
                "One player per idea ONLY, never teams or comparisons. "+
                "Avoid finished player-concept pairs: "+string.Join("; ",completed)+". "+
                "Use famous current players or real retired NBA legends, without inventing careers or stats. "+
                "In THIS SAME response generate SIX SUBJECTIVE 0.0–10.0 scores: shooting, "+
                "finishing, handles, playmaking, defense and basketball IQ. "+
                "These are editorial opinions, NOT verified NBA or NBA2K statistics. "+
                "Include factual, compelling search-friendly title (40–85 characters), "+
                "caption (100–240 characters) and 3–5 relevant TikTok hashtags. "+
                "Do not make up teams, dates, records or achievements. "+
                "Return ONLY a JSON object inside a fenced json block: "+
                "{\"response_type\":\"nbaclipper_ideas_v1\",\"players\":[{"+
                "\"name\":\"Stephen Curry\",\"concept\":\"Elite shooting from deep\","+
                "\"scores\":{\"shooting\":9.8,\"finishing\":8.0,\"handles\":9.4,"+
                "\"playmaking\":9.3,\"defense\":7.0,\"iq\":9.6},"+
                "\"seo\":{\"title\":\"Stephen Curry's Incredible Range: Player Ratings\","+
                "\"caption\":\"A quick breakdown of Curry's game and subjective ratings.\","+
                "\"hashtags\":\"#NBA #StephenCurry #Basketball #PlayerRatings\"}}]}";
            var raw=await _chat.GenerateTextAsync(_nbaChat,Settings,prompt,Log,_cts.Token);
            var first=raw.IndexOf('{');var last=raw.LastIndexOf('}');
            if(first<0||last<=first)throw new InvalidDataException("ChatGPT did not return JSON.");
            using var json=JsonDocument.Parse(raw.Substring(first,last-first+1));
            var players=json.RootElement.GetProperty("players");
            if(players.ValueKind!=JsonValueKind.Array)
                throw new InvalidDataException("Expected players JSON array.");
            var ready=new List<NbaClipperEntry>();
            var seen=new HashSet<int>();
            foreach(var candidate in players.EnumerateArray().Take(NbaCount*3))
            {
                if(candidate.ValueKind!=JsonValueKind.Object)continue;
                var name=NbaJson(candidate,"name").Trim();
                var concept=NbaJson(candidate,"concept").Trim();
                if(name.Length is < 4 or > 100||concept.Length is < 4 or > 130||
                    !name.Contains(' ')||name.Any(char.IsControl))continue;
                var id=NbaId(name,concept);
                if(!seen.Add(id)||_nbaHistory.Any(x=>x.PlayerId==id)||
                    PublishingQueue.Any(x=>x.VideoPath.Contains(
                        id.ToString(CultureInfo.InvariantCulture)+"_",
                        StringComparison.OrdinalIgnoreCase)))continue;
                if(!candidate.TryGetProperty("scores",out var scores)||
                    scores.ValueKind!=JsonValueKind.Object)continue;
                string[] keys=["shooting","finishing","handles","playmaking","defense","iq"];
                var values=keys.Select(k=>scores.TryGetProperty(k,out var value)&&
                    value.ValueKind==JsonValueKind.Number?
                    value.GetDouble():double.NaN).ToArray();
                if(values.Any(x=>!double.IsFinite(x)||x<0||x>10))continue;
                var seo=candidate.TryGetProperty("seo",out var info)&&
                    info.ValueKind==JsonValueKind.Object?info:default;
                ready.Add(new NbaClipperEntry{
                    PlayerId=id,Name=name,Concept=concept,Mode=NbaFormat,
                    Scores=values.Select(x=>Math.Round(x,1)).ToArray(),
                    SeoTitle=NbaJson(seo,"title"),SeoCaption=NbaJson(seo,"caption"),
                    SeoHashtags=NbaJson(seo,"hashtags")
                });
                if(ready.Count>=NbaCount)break;
            }
            if(ready.Count==0)throw new InvalidDataException("No valid NBA player ideas received.");
            NbaClipperIdeas.Clear();
            foreach(var item in ready)NbaClipperIdeas.Add(item);
            NbaSave();
            NbaStatus=ready.Count+" individual NBA player ideas ready. "+
                "Ratings are subjective; actual NBA statistics are not invented.";
        }
        catch(OperationCanceledException){NbaSave();throw;}
        catch(Exception ex){NbaStatus="NBA idea generation failed: "+ex.Message;Log(NbaStatus);}
    }

    private static string? NbaCachedSource(string folder,NbaClipperEntry player)
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

    private async Task<string> NbaDownloadAsync(NbaClipperEntry player)
    {
        Directory.CreateDirectory(NbaSourceFolder);
        var target=Path.Combine(NbaSourceFolder,player.PlayerId+".mp4");
        var downloader=MovieDownloaderPath();
        var search=player.Name+" NBA "+(player.Mode=="Best Plays"?"best plays highlights":
            player.Mode=="Player Legacy"?"career highlights":"player highlights mix");
        player.Status="Finding player-specific YouTube highlights";
        var args=new List<string>{"--no-playlist","--flat-playlist","--dump-single-json",
            "--skip-download","--no-warnings","ytsearch12:"+search};
        if(NbaBrowserLogin)args.InsertRange(0,["--cookies-from-browser","edge"]);
        var result=await MovieRunProcessAsync(downloader,args,_cts.Token);
        if(result.Code!=0)throw new InvalidOperationException("YouTube highlight search: "+
            MovieDownloaderError(result.Error));
        using var json=JsonDocument.Parse(result.Output);
        if(!json.RootElement.TryGetProperty("entries",out var entries)||
            entries.ValueKind!=JsonValueKind.Array)
            throw new InvalidDataException("No player highlights found on YouTube.");
        var names=player.Name.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        var sources=new List<(string Url,int Priority)>();
        foreach(var e in entries.EnumerateArray())
        {
            if(e.ValueKind!=JsonValueKind.Object)continue;
            var title=NbaJson(e,"title");
            var videoId=NbaJson(e,"id");
            var lower=title.ToLowerInvariant();
            if(videoId.Length!=11||
                !videoId.All(c=>char.IsAsciiLetterOrDigit(c)||c=='-'||c=='_')||
                !names.All(n=>title.Contains(n,StringComparison.OrdinalIgnoreCase))||
                !(lower.Contains("highlight")||lower.Contains("best plays")||lower.Contains("mix"))||
                new[]{"interview","reaction","podcast","breakdown","2k","gameplay",
                    "simulation","fan made","fanmade"}.Any(lower.Contains))continue;
            sources.Add(("https://www.youtube.com/watch?v="+videoId,
                (lower.Contains("nba")?10:0)+(lower.Contains("official")?5:0)));
        }
        if(sources.Count==0)throw new InvalidOperationException(
            "No individual-player highlight source found. Import an authorized local clip.");
        var pattern=Path.Combine(NbaSourceFolder,player.PlayerId+".%(ext)s");
        var failures=new List<string>();
        foreach(var url in sources.OrderByDescending(x=>x.Priority)
            .Select(x=>x.Url).Distinct().Take(4))
        {
            _cts.Token.ThrowIfCancellationRequested();
            var download=new List<string>{"--no-playlist","--no-progress",
                "--retries","2","-f","bv*+ba/b","-S","res:1080,ext:mp4",
                "--merge-output-format","mp4","--recode-video","mp4",
                "-o",pattern,url};
            if(NbaBrowserLogin)download.InsertRange(0,["--cookies-from-browser","edge"]);
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

    private async Task<PublishQueueItem> NbaRenderAsync(NbaClipperEntry player,string sourcePath)
    {
        var music=NbaSoundFile;
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
        var work=Path.Combine(NbaRoot,player.PlayerId+"-"+
            DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(NbaExportFolder);
        var cover=Path.Combine(work,"thumbnail.jpg");
        var video=Path.Combine(NbaExportFolder,player.PlayerId+"_"+
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
        // Unlike MovieClipper, NBAClipper must never crop basketball
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
        var ratingsMode=player.Mode=="Player Ratings";
        var labels=ratingsMode
            ?new[]{"SHOOTING","FINISHING","HANDLES","PLAYMAKING",
                "DEFENSE","BASKETBALL IQ","OVERALL"}
            :player.Mode=="Best Plays"
                ?new[]{"PLAY 01","PLAY 02","PLAY 03","PLAY 04",
                    "PLAY 05","PLAY 06","BEST PLAYS"}
                :new[]{"THE PLAYER","CAREER HIGHLIGHTS","SIGNATURE SKILLS",
                    "GREAT MOMENTS","THE JOURNEY","THE IMPACT","PLAYER LEGACY"};
        var values=player.Scores.Concat([Math.Round(player.Scores.Average(),1)]).ToArray();
        var intro=ratingsMode?"PLAYER RATINGS":
            player.Mode=="Best Plays"?"BEST PLAYS":"PLAYER LEGACY";
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
            ",asetpts=PTS-STARTPTS,volume="+MovieNumber(NbaMusicLevel)+"[a]");
        args.AddRange(["-filter_complex",string.Join(";",filters),
            "-map","[v]","-map","[a]","-t",MovieNumber(seconds),
            "-frames:v",frames.ToString(CultureInfo.InvariantCulture),
            "-c:v","libx264","-preset","veryfast","-crf","21",
            "-c:a","aac","-b:a","160k","-pix_fmt","yuv420p",
            "-movflags","+faststart",video]);
        player.Status="Rendering "+count+" beat-synced player highlights";
        var result=await MovieRunProcessAsync(ffmpeg,args,_cts.Token,work);
        if(result.Code!=0)
            throw new InvalidOperationException("FFmpeg: "+
                result.Error[^Math.Min(result.Error.Length,900)..]);
        if(!File.Exists(video)||new FileInfo(video).Length<10000)
            throw new IOException("NBAClipper video file was not generated.");
        player.OutputPath=video;
        player.ThumbnailPath=cover;
        NbaSave();
        return new PublishQueueItem{
            VideoPath=video,ThumbnailPath=cover,
            Title=player.Name+" — "+player.Concept,Description=player.Concept,
            TikTokCaption=player.Name+" NBA player ratings #NBA #Basketball",
            YouTubeTitle=player.Name+" — "+player.Concept+" #Shorts",
            YouTubeDescription=player.Concept
        };
    }

    private static void NbaEnsureSeo(NbaClipperEntry player)
    {
        if(player.SeoTitle.Length is < 15 or > 120 ||
            !player.SeoTitle.Contains(player.Name,StringComparison.OrdinalIgnoreCase))
            player.SeoTitle=player.Name+" — "+player.Concept+" | NBA Player Review";
        if(player.SeoCaption.Length is < 25 or > 400)
            player.SeoCaption=player.Name+" NBA player spotlight: "+player.Concept+
                ". Shooting, finishing, handles, playmaking, defense and IQ scored out of 10.";
        var tags=Regex.Matches(player.SeoHashtags,
            @"(?<!\S)#[A-Za-z][A-Za-z0-9_]{1,24}(?=\s|$)")
            .Select(x=>x.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        if(tags.Count<3)tags=["#NBA","#Basketball","#PlayerRatings"];
        player.SeoHashtags=string.Join(" ",tags);
    }

    private async Task NbaStartAsync()
    {
        if(_nbaProcessing)return;
        _nbaProcessing=true;
        NbaPrepareChat();
        try
        {
            var ideasWereEmpty=NbaClipperIdeas.Count==0;
            if(ideasWereEmpty)await NbaGenerateAsync();
            var selected=NbaClipperIdeas.Where(x=>x.IsChecked&&
                (!_nbaRetryFailed||ideasWereEmpty||x.Status.StartsWith("Failed",
                    StringComparison.OrdinalIgnoreCase))).ToList();
            if(selected.Count==0){
                NbaStatus="No pending individual-player ideas selected.";return;
            }
            if(!DateTime.TryParseExact(NbaFirstSlot,"yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture,DateTimeStyles.AssumeLocal,out var first)||
                first<=DateTime.Now.AddMinutes(25)||first.Minute%5!=0)
                throw new InvalidDataException("First posting slot must be 25+ minutes away, on 5-minute increments.");
            if(!File.Exists(NbaSoundFile))
                throw new FileNotFoundException("Select a background soundtrack first.",NbaSoundFile);
            var originallyChecked=PublishingQueue.Where(x=>x.IsChecked).ToList();
            var completed=new List<PublishQueueItem>();
            foreach(var player in selected)
            {
                _cts.Token.ThrowIfCancellationRequested();
                try
                {
                    if(_nbaHistory.Any(h=>h.PlayerId==player.PlayerId)||
                        (!string.IsNullOrWhiteSpace(player.OutputPath)&&
                         PublishingQueue.Any(q=>q.VideoPath.Equals(player.OutputPath,
                             StringComparison.OrdinalIgnoreCase))))
                    {
                        player.Status="Already queued (no duplicate)";
                        player.IsChecked=false;NbaSave();continue;
                    }
                    var source=File.Exists(player.SourcePath)?player.SourcePath:
                        NbaCachedSource(NbaSourceFolder,player);
                    if(source is null&&NbaAutoDownload)
                        source=await NbaDownloadAsync(player);
                    if(source is null)
                    {
                        player.Status="Failed: player highlight source unavailable";
                        NbaSave();continue;
                    }
                    player.SourcePath=source;player.Status="HighlightsReady";NbaSave();
                    var output=File.Exists(player.OutputPath)?
                        new PublishQueueItem{VideoPath=player.OutputPath,
                            ThumbnailPath=player.ThumbnailPath}:
                        await NbaRenderAsync(player,source);
                    NbaEnsureSeo(player);
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
                    NbaSave();SavePublishingQueue();
                    var history=new NbaClipperHistoryItem{
                        PlayerId=player.PlayerId,Name=player.Name,Concept=player.Concept,
                        OutputPath=output.VideoPath};
                    _nbaHistory.Add(history);
                    NbaHistory.Insert(0,history);
                    NbaSave();
                }
                catch(OperationCanceledException){player.Status="Stopped";NbaSave();throw;}
                catch(Exception ex){
                    player.Status="Failed: "+ex.Message;Log("NBAClipper "+player.Name+": "+ex.Message);
                    NbaSave();
                }
            }
            Raise(nameof(AllPublishItemsChecked));
            RefreshPublishingCalendar();
            RefreshVideoLibrary();
            NbaStatus="Exported "+completed.Count+"/"+selected.Count+
                " individual NBA player videos to "+NbaExportFolder+".";
            if(completed.Count==0||!NbaPublish)return;
            if(!PublishConsent){
                NbaStatus+=" Enable existing publishing authorization to auto-post.";
                return;
            }
            foreach(var old in originallyChecked)old.IsChecked=false;
            try{await RunTikTokBatchAsync();}
            finally{foreach(var old in originallyChecked)old.IsChecked=true;}
        }
        catch(OperationCanceledException){NbaStatus="Stopped. Resume saved NBA workflow.";NbaSave();throw;}
        catch(Exception ex){NbaStatus="NBAClipper: "+ex.Message;Log(NbaStatus);}
        finally{_nbaProcessing=false;NbaSave();}
    }
}
