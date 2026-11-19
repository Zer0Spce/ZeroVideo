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

public sealed class FootballClipperEntry : ObservableObject
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

public sealed class FootballClipperHistoryItem
{
    public int PlayerId { get; set; }
    public string Name { get; set; } = "";
    public string Concept { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class FootballClipperState
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
    public List<FootballClipperEntry> Ideas { get; set; } = [];
    public List<FootballClipperHistoryItem> History { get; set; } = [];
}

public sealed partial class MainViewModel
{
    private static readonly string FootballRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZeroVideo", "FOOTBALLClipper");
    private static readonly string FootballStatePath = Path.Combine(FootballRoot, "state.json");
    private string _footballNiche = "Famous soccer football players and legends";
    private string _footballFormat = "Player Ratings";
    private int _footballCount = 10;
    private string _footballSourceFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "ZeroVideo", "FOOTBALLHighlights");
    private string _footballSoundFile = "";
    private string _footballStatus = "Ready: ChatGPT player ideas and scores → individual football soccer player highlights → beat-synced edit → TikTok queue.";
    private string _footballFirstSlot = DateTime.Now.AddHours(3).ToString("yyyy-MM-dd HH:00",CultureInfo.InvariantCulture);
    private bool _footballPublish = true;
    private bool _footballAutoDownload = true;
    private bool _footballBrowserLogin;
    private bool _footballNewChat;
    private bool _footballProcessing;
    private bool _footballRetryFailed;
    private double _footballMusicLevel = 0.85;
    private VideoProject _footballChat = new() { Title = "FOOTBALLClipper batch", Topic = "Individual football/soccer player videos" };
    private readonly List<FootballClipperHistoryItem> _footballHistory = [];
    public ObservableCollection<FootballClipperEntry> FootballClipperIdeas { get; } = [];
    public ObservableCollection<FootballClipperHistoryItem> FootballHistory { get; } = [];
    public ObservableCollection<string> FootballFormats { get; } = ["Player Ratings", "Best Goals", "Football Legacy"];
    public string FootballNiche { get => _footballNiche; set => Set(ref _footballNiche,value); }
    public string FootballFormat { get => _footballFormat; set => Set(ref _footballFormat,value); }
    public int FootballCount { get => _footballCount; set => Set(ref _footballCount,Math.Clamp(value,1,20)); }
    public string FootballSourceFolder { get => _footballSourceFolder; set => Set(ref _footballSourceFolder,value); }
    public string FootballSoundFile { get => _footballSoundFile; set => Set(ref _footballSoundFile,value); }
    public string FootballStatus { get => _footballStatus; set => Set(ref _footballStatus,value); }
    public string FootballFirstSlot { get => _footballFirstSlot; set => Set(ref _footballFirstSlot,value); }
    public bool FootballPublish { get => _footballPublish; set => Set(ref _footballPublish,value); }
    public bool FootballAutoDownload { get => _footballAutoDownload; set => Set(ref _footballAutoDownload,value); }
    public bool FootballBrowserLogin { get => _footballBrowserLogin; set => Set(ref _footballBrowserLogin,value); }
    public bool FootballNewChat { get => _footballNewChat; set => Set(ref _footballNewChat,value); }
    public double FootballMusicLevel { get => _footballMusicLevel; set => Set(ref _footballMusicLevel,Math.Clamp(value,0,1)); }
    public string FootballHistorySummary => FootballHistory.Count+" finished player concepts in FOOTBALL history";
    public string FootballCheckpointStatus => FootballClipperIdeas.Count+" saved player ideas; "+FootballStatePath;
    public string FootballExportFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "ZeroVideo","FOOTBALLClipper");
    public ICommand FootballGenerateCommand { get; private set; } = null!;
    public ICommand FootballStartCommand { get; private set; } = null!;
    public ICommand FootballResumeCommand { get; private set; } = null!;
    public ICommand FootballRetryFailedCommand { get; private set; } = null!;
    public ICommand FootballBrowseSourceCommand { get; private set; } = null!;
    public ICommand FootballBrowseSoundCommand { get; private set; } = null!;
    public ICommand FootballFindHighlightsCommand { get; private set; } = null!;
    public ICommand FootballOpenOutputCommand { get; private set; } = null!;
    public ICommand FootballForceStopCommand => ForceStopJobCommand;

    private void InitializeFootballClipperCommands()
    {
        FootballRestore();
        FootballGenerateCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "FOOTBALLClipper: individual player ideas and SEO",FootballGenerateAsync));
        FootballStartCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "FOOTBALLClipper: one-click individual football soccer player highlights",FootballStartAsync,publishing:FootballPublish));
        FootballResumeCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "FOOTBALLClipper: resume",async()=>{
                FootballRestore();
                _footballRetryFailed=false;
                await FootballStartAsync();
            },publishing:FootballPublish));
        FootballRetryFailedCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "FOOTBALLClipper: retry failures",async()=>{
                FootballRestore();
                _footballRetryFailed=true;
                try{await FootballStartAsync();}
                finally{_footballRetryFailed=false;}
            },publishing:FootballPublish));
        FootballBrowseSourceCommand=new RelayCommand(()=>{
            var picker=new OpenFolderDialog{Title="FOOTBALL player highlight source/cache folder"};
            if(picker.ShowDialog()==true){FootballSourceFolder=picker.FolderName;FootballSave();}
        });
        FootballBrowseSoundCommand=new RelayCommand(()=>{
            var picker=new OpenFileDialog{
                Filter="Audio files|*.mp3;*.m4a;*.wav;*.aac;*.flac",
                CheckFileExists=true};
            if(picker.ShowDialog()==true){FootballSoundFile=picker.FileName;FootballSave();}
        });
        FootballFindHighlightsCommand=new RelayCommand(()=>{
            var player=FootballClipperIdeas.FirstOrDefault(x=>x.IsChecked);
            if(player is null){FootballStatus="Select an FOOTBALL player first.";return;}
            var query=player.Name+" FOOTBALL individual highlights";
            Process.Start(new ProcessStartInfo(
                "https://www.youtube.com/results?search_query="+Uri.EscapeDataString(query))
                {UseShellExecute=true});
            FootballStatus="Opened player-specific highlights search. Use footage you are authorized to edit and post.";
        });
        FootballOpenOutputCommand=new RelayCommand(()=>{
            Directory.CreateDirectory(FootballExportFolder);
            Process.Start(new ProcessStartInfo("explorer.exe","\""+FootballExportFolder+"\"")
                {UseShellExecute=true});
        });
    }

    private void FootballSave()
    {
        Directory.CreateDirectory(FootballRoot);
        var snapshot=new FootballClipperState{
            Niche=FootballNiche,Format=FootballFormat,
            ChatUrl=_footballChat.ChatGptConversationUrl??"",
            SourceFolder=FootballSourceFolder,SoundFile=FootballSoundFile,
            FirstSlot=FootballFirstSlot,AutoDownload=FootballAutoDownload,
            BrowserLogin=FootballBrowserLogin,Publish=FootballPublish,
            MusicLevel=FootballMusicLevel,
            Ideas=FootballClipperIdeas.ToList(),
            History=_footballHistory.ToList()
        };
        var path=FootballStatePath+".tmp";
        File.WriteAllText(path,JsonSerializer.Serialize(snapshot,new JsonSerializerOptions{
            WriteIndented=true}));
        File.Move(path,FootballStatePath,true);
        Raise(nameof(FootballCheckpointStatus));
        Raise(nameof(FootballHistorySummary));
    }

    private void FootballRestore()
    {
        try
        {
            if(!File.Exists(FootballStatePath))return;
            var state=JsonSerializer.Deserialize<FootballClipperState>(
                File.ReadAllText(FootballStatePath));
            if(state is null)return;
            if(state.Niche.Length>0)FootballNiche=state.Niche;
            if(FootballFormats.Contains(state.Format))FootballFormat=state.Format;
            if(!string.IsNullOrWhiteSpace(state.SourceFolder))
                FootballSourceFolder=state.SourceFolder;
            if(!string.IsNullOrWhiteSpace(state.SoundFile))
                FootballSoundFile=state.SoundFile;
            // Keep the future default slot instead of restoring a time in
            // the past from a previous session.
            if(DateTime.TryParseExact(state.FirstSlot,"yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture,DateTimeStyles.AssumeLocal,
                out var restoredSlot)&&restoredSlot>DateTime.Now.AddMinutes(25))
                FootballFirstSlot=state.FirstSlot;
            FootballAutoDownload=state.AutoDownload;
            FootballBrowserLogin=state.BrowserLogin;
            FootballPublish=state.Publish;
            FootballMusicLevel=state.MusicLevel;
            _footballChat=new VideoProject{
                Title="FOOTBALLClipper batch",Topic=FootballNiche,
                ChatGptConversationUrl=string.IsNullOrWhiteSpace(state.ChatUrl)?null:state.ChatUrl};
            FootballClipperIdeas.Clear();
            foreach(var item in state.Ideas)FootballClipperIdeas.Add(item);
            _footballHistory.Clear();
            _footballHistory.AddRange(state.History);
            FootballHistory.Clear();
            foreach(var item in _footballHistory.OrderByDescending(x=>x.CompletedAt))
                FootballHistory.Add(item);
            Raise(nameof(FootballHistorySummary));
            Raise(nameof(FootballCheckpointStatus));
        }
        catch(Exception ex){FootballStatus="FOOTBALL checkpoint load failed: "+ex.Message;}
    }

    private void FootballPrepareChat()
    {
        if(FootballNewChat)
        {
            _footballChat=new VideoProject{Title="FOOTBALLClipper batch: "+FootballNiche,Topic=FootballNiche};
            _chat.RequestNewChat();
            FootballNewChat=false;
            FootballSave();
        }
    }

    private static string FootballJson(JsonElement node,string key)=>
        node.ValueKind==JsonValueKind.Object &&
        node.TryGetProperty(key,out var field)&&field.ValueKind==JsonValueKind.String?
        field.GetString()??"":"";

    private static int FootballId(string name,string concept)
    {
        unchecked{
            uint h=2166136261;
            foreach(var c in ("Football|"+name+"|"+concept).ToUpperInvariant()){
                h^=c;h*=16777619;
            }
            return (int)(h&0x7fffffff);
        }
    }

    private async Task FootballGenerateAsync()
    {
        try
        {
            FootballStatus="ChatGPT: generating individual FOOTBALL player concepts, six ratings and SEO in ONE request";
            FootballPrepareChat();
            var completed=_footballHistory.Select(x=>x.Name+" — "+x.Concept)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(200);
            var prompt="Generate exactly "+FootballCount+" REAL individual football/soccer player ideas, one subject per video, no teams, duos or comparisons. "+
                "Niche: "+FootballNiche+". Video format: "+FootballFormat+". "+
                "Avoid completed name/concept pairs: "+string.Join("; ",completed)+". "+
                "For anime and gaming characters identify the canonical series or game in concept; for real people avoid invented achievements. "+
                "In the SAME response generate six SUBJECTIVE scores 0.0-10.0 for: shooting, dribbling, passing, pace, defense, vision. "+
                "Those are editorial ratings, never official statistics or sourced review scores. "+
                "Write one accurate SEO title (40-85 chars), short caption (100-240 chars) and 3-5 hashtags. "+
                "Use only genuine known subjects; do not invent people, titles, games, broadcasts, stats or achievements. "+
                "Return ONLY a valid JSON object inside fenced json: "+
                "{\"response_type\":\"footballclipper_ideas_v1\",\"players\":[{\"name\":\"Lionel Messi\",\"concept\":\"Iconic individual highlight moments\",\"scores\":{\"shooting\":8,\"dribbling\":8,\"passing\":8,\"pace\":8,\"defense\":8,\"vision\":8},\"seo\":{\"title\":\"Individual character or person highlights and ratings\",\"caption\":\"A short-form individual spotlight with subjective category scores.\",\"hashtags\":\"#Highlights #Clips #Edits\"}}]}";
            var raw=await _chat.GenerateTextAsync(_footballChat,Settings,prompt,Log,_cts.Token);
            var first=raw.IndexOf('{');var last=raw.LastIndexOf('}');
            if(first<0||last<=first)throw new InvalidDataException("ChatGPT did not return JSON.");
            using var json=JsonDocument.Parse(raw.Substring(first,last-first+1));
            var players=json.RootElement.GetProperty("players");
            if(players.ValueKind!=JsonValueKind.Array)
                throw new InvalidDataException("Expected players JSON array.");
            var ready=new List<FootballClipperEntry>();
            var seen=new HashSet<int>();
            foreach(var candidate in players.EnumerateArray().Take(FootballCount*3))
            {
                if(candidate.ValueKind!=JsonValueKind.Object)continue;
                var name=FootballJson(candidate,"name").Trim();
                var concept=FootballJson(candidate,"concept").Trim();
                if(name.Length is < 4 or > 100||concept.Length is < 4 or > 130||
                    name.Any(char.IsControl))continue;
                var id=FootballId(name,concept);
                if(!seen.Add(id)||_footballHistory.Any(x=>x.PlayerId==id)||
                    PublishingQueue.Any(x=>x.VideoPath.Contains(
                        id.ToString(CultureInfo.InvariantCulture)+"_",
                        StringComparison.OrdinalIgnoreCase)))continue;
                if(!candidate.TryGetProperty("scores",out var scores)||
                    scores.ValueKind!=JsonValueKind.Object)continue;
                string[] keys=["shooting","dribbling","passing","pace","defense","vision"];
                var values=keys.Select(k=>scores.TryGetProperty(k,out var value)&&
                    value.ValueKind==JsonValueKind.Number?
                    value.GetDouble():double.NaN).ToArray();
                if(values.Any(x=>!double.IsFinite(x)||x<0||x>10))continue;
                var seo=candidate.TryGetProperty("seo",out var info)&&
                    info.ValueKind==JsonValueKind.Object?info:default;
                ready.Add(new FootballClipperEntry{
                    PlayerId=id,Name=name,Concept=concept,Mode=FootballFormat,
                    Scores=values.Select(x=>Math.Round(x,1)).ToArray(),
                    SeoTitle=FootballJson(seo,"title"),SeoCaption=FootballJson(seo,"caption"),
                    SeoHashtags=FootballJson(seo,"hashtags")
                });
                if(ready.Count>=FootballCount)break;
            }
            if(ready.Count==0)throw new InvalidDataException("No valid FOOTBALL player ideas received.");
            FootballClipperIdeas.Clear();
            foreach(var item in ready)FootballClipperIdeas.Add(item);
            FootballSave();
            FootballStatus=ready.Count+" individual FOOTBALL player ideas ready. "+
                "Ratings are subjective; actual FOOTBALL statistics are not invented.";
        }
        catch(OperationCanceledException){FootballSave();throw;}
        catch(Exception ex){FootballStatus="FOOTBALL idea generation failed: "+ex.Message;Log(FootballStatus);}
    }

    private static string? FootballCachedSource(string folder,FootballClipperEntry player)
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

    private async Task<string> FootballDownloadAsync(FootballClipperEntry player)
    {
        Directory.CreateDirectory(FootballSourceFolder);
        var target=Path.Combine(FootballSourceFolder,player.PlayerId+".mp4");
        var downloader=MovieDownloaderPath();
        var search=player.Name+" football soccer skills goals highlights";
        player.Status="Finding player-specific YouTube highlights";
        var args=new List<string>{"--no-playlist","--flat-playlist","--dump-single-json",
            "--skip-download","--no-warnings","ytsearch12:"+search};
        if(FootballBrowserLogin)args.InsertRange(0,["--cookies-from-browser","edge"]);
        var result=await MovieRunProcessAsync(downloader,args,_cts.Token);
        if(result.Code!=0)throw new InvalidOperationException("YouTube highlight search: "+
            MovieDownloaderError(result.Error));
        using var json=JsonDocument.Parse(result.Output);
        if(!json.RootElement.TryGetProperty("entries",out var entries)||
            entries.ValueKind!=JsonValueKind.Array)
            throw new InvalidDataException("No individual football soccer player highlights found on YouTube.");
        var names=player.Name.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        var sources=new List<(string Url,int Priority)>();
        foreach(var e in entries.EnumerateArray())
        {
            if(e.ValueKind!=JsonValueKind.Object)continue;
            var title=FootballJson(e,"title");
            var videoId=FootballJson(e,"id");
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
            "No football/soccer player highlight source found. Import an authorized local clip.");
        var pattern=Path.Combine(FootballSourceFolder,player.PlayerId+".%(ext)s");
        var failures=new List<string>();
        foreach(var url in sources.OrderByDescending(x=>x.Priority)
            .Select(x=>x.Url).Distinct().Take(4))
        {
            _cts.Token.ThrowIfCancellationRequested();
            var download=new List<string>{"--no-playlist","--no-progress",
                "--retries","2","-f","bv*+ba/b","-S","res:1080,ext:mp4",
                "--merge-output-format","mp4","--recode-video","mp4",
                "-o",pattern,url};
            if(FootballBrowserLogin)download.InsertRange(0,["--cookies-from-browser","edge"]);
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

    private async Task<PublishQueueItem> FootballRenderAsync(FootballClipperEntry player,string sourcePath)
    {
        var music=FootballSoundFile;
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
        var work=Path.Combine(FootballRoot,player.PlayerId+"-"+
            DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(FootballExportFolder);
        var cover=Path.Combine(work,"thumbnail.jpg");
        var video=Path.Combine(FootballExportFolder,player.PlayerId+"_"+
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
        // Unlike MovieClipper, FOOTBALLClipper must never crop basketball
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
            ?new[]{"SHOOTING","DRIBBLING","PASSING","PACE","DEFENSE","VISION","OVERALL"}
            :player.Mode=="Best Goals"
                ?new[]{"GOAL 01","GOAL 02","GOAL 03","GOAL 04","GOAL 05","GOAL 06","BEST GOALS"}
                :new[]{"THE PLAYER","CAREER HIGHLIGHTS","ICONIC GOALS","THE JOURNEY","GREAT MOMENTS","THE LEGEND","FOOTBALL LEGACY"};
        var values=player.Scores.Concat([Math.Round(player.Scores.Average(),1)]).ToArray();
        var intro=ratingsMode?"FOOTBALL RATINGS":
            player.Mode=="Best Goals"?"BEST GOALS":"FOOTBALL LEGACY";
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
            ",asetpts=PTS-STARTPTS,volume="+MovieNumber(FootballMusicLevel)+"[a]");
        args.AddRange(["-filter_complex",string.Join(";",filters),
            "-map","[v]","-map","[a]","-t",MovieNumber(seconds),
            "-frames:v",frames.ToString(CultureInfo.InvariantCulture),
            "-c:v","libx264","-preset","veryfast","-crf","21",
            "-c:a","aac","-b:a","160k","-pix_fmt","yuv420p",
            "-movflags","+faststart",video]);
        player.Status="Rendering "+count+" beat-synced individual football soccer player highlights";
        var result=await MovieRunProcessAsync(ffmpeg,args,_cts.Token,work);
        if(result.Code!=0)
            throw new InvalidOperationException("FFmpeg: "+
                result.Error[^Math.Min(result.Error.Length,900)..]);
        if(!File.Exists(video)||new FileInfo(video).Length<10000)
            throw new IOException("FOOTBALLClipper video file was not generated.");
        player.OutputPath=video;
        player.ThumbnailPath=cover;
        FootballSave();
        return new PublishQueueItem{
            VideoPath=video,ThumbnailPath=cover,
            Title=player.Name+" — "+player.Concept,Description=player.Concept,
            TikTokCaption=player.Name+" FOOTBALL player ratings #FOOTBALL #Basketball",
            YouTubeTitle=player.Name+" — "+player.Concept+" #Shorts",
            YouTubeDescription=player.Concept
        };
    }

    private static void FootballEnsureSeo(FootballClipperEntry player)
    {
        if(player.SeoTitle.Length is < 15 or > 120 ||
            !player.SeoTitle.Contains(player.Name,StringComparison.OrdinalIgnoreCase))
            player.SeoTitle=player.Name+" — "+player.Concept+" | FOOTBALL Player Review";
        if(player.SeoCaption.Length is < 25 or > 400)
            player.SeoCaption=player.Name+" FOOTBALL player spotlight: "+player.Concept+
                ". Shooting, finishing, handles, playmaking, defense and IQ scored out of 10.";
        var tags=Regex.Matches(player.SeoHashtags,
            @"(?<!\S)#[A-Za-z][A-Za-z0-9_]{1,24}(?=\s|$)")
            .Select(x=>x.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        if(tags.Count<3)tags=["#FOOTBALL","#Basketball","#PlayerRatings"];
        player.SeoHashtags=string.Join(" ",tags);
    }

    private async Task FootballStartAsync()
    {
        if(_footballProcessing)return;
        _footballProcessing=true;
        FootballPrepareChat();
        try
        {
            var ideasWereEmpty=FootballClipperIdeas.Count==0;
            if(ideasWereEmpty)await FootballGenerateAsync();
            var selected=FootballClipperIdeas.Where(x=>x.IsChecked&&
                (!_footballRetryFailed||ideasWereEmpty||x.Status.StartsWith("Failed",
                    StringComparison.OrdinalIgnoreCase))).ToList();
            if(selected.Count==0){
                FootballStatus="No pending football/soccer player ideas selected.";return;
            }
            if(!DateTime.TryParseExact(FootballFirstSlot,"yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture,DateTimeStyles.AssumeLocal,out var first)||
                first<=DateTime.Now.AddMinutes(25)||first.Minute%5!=0)
                throw new InvalidDataException("First posting slot must be 25+ minutes away, on 5-minute increments.");
            if(!File.Exists(FootballSoundFile))
                throw new FileNotFoundException("Select a background soundtrack first.",FootballSoundFile);
            var originallyChecked=PublishingQueue.Where(x=>x.IsChecked).ToList();
            var completed=new List<PublishQueueItem>();
            foreach(var player in selected)
            {
                _cts.Token.ThrowIfCancellationRequested();
                try
                {
                    if(_footballHistory.Any(h=>h.PlayerId==player.PlayerId)||
                        (!string.IsNullOrWhiteSpace(player.OutputPath)&&
                         PublishingQueue.Any(q=>q.VideoPath.Equals(player.OutputPath,
                             StringComparison.OrdinalIgnoreCase))))
                    {
                        player.Status="Already queued (no duplicate)";
                        player.IsChecked=false;FootballSave();continue;
                    }
                    var source=File.Exists(player.SourcePath)?player.SourcePath:
                        FootballCachedSource(FootballSourceFolder,player);
                    if(source is null&&FootballAutoDownload)
                        source=await FootballDownloadAsync(player);
                    if(source is null)
                    {
                        player.Status="Failed: player highlight source unavailable";
                        FootballSave();continue;
                    }
                    player.SourcePath=source;player.Status="HighlightsReady";FootballSave();
                    var output=File.Exists(player.OutputPath)?
                        new PublishQueueItem{VideoPath=player.OutputPath,
                            ThumbnailPath=player.ThumbnailPath}:
                        await FootballRenderAsync(player,source);
                    FootballEnsureSeo(player);
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
                    FootballSave();SavePublishingQueue();
                    var history=new FootballClipperHistoryItem{
                        PlayerId=player.PlayerId,Name=player.Name,Concept=player.Concept,
                        OutputPath=output.VideoPath};
                    _footballHistory.Add(history);
                    FootballHistory.Insert(0,history);
                    FootballSave();
                }
                catch(OperationCanceledException){player.Status="Stopped";FootballSave();throw;}
                catch(Exception ex){
                    player.Status="Failed: "+ex.Message;Log("FOOTBALLClipper "+player.Name+": "+ex.Message);
                    FootballSave();
                }
            }
            Raise(nameof(AllPublishItemsChecked));
            RefreshPublishingCalendar();
            RefreshVideoLibrary();
            FootballStatus="Exported "+completed.Count+"/"+selected.Count+
                " individual FOOTBALL player videos to "+FootballExportFolder+".";
            if(completed.Count==0||!FootballPublish)return;
            if(!PublishConsent){
                FootballStatus+=" Enable existing publishing authorization to auto-post.";
                return;
            }
            foreach(var old in originallyChecked)old.IsChecked=false;
            try{await RunTikTokBatchAsync();}
            finally{foreach(var old in originallyChecked)old.IsChecked=true;}
        }
        catch(OperationCanceledException){FootballStatus="Stopped. Resume saved FOOTBALL workflow.";FootballSave();throw;}
        catch(Exception ex){FootballStatus="FOOTBALLClipper: "+ex.Message;Log(FootballStatus);}
        finally{_footballProcessing=false;FootballSave();}
    }
}
