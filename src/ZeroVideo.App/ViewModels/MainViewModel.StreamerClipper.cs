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

public sealed class StreamerClipperEntry : ObservableObject
{
    public int PlayerId { get; set; }
    public string Name { get; set; } = "";
    public string Concept { get; set; } = "";
    public string Mode { get; set; } = "Streamer Ratings";
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

public sealed class StreamerClipperHistoryItem
{
    public int PlayerId { get; set; }
    public string Name { get; set; } = "";
    public string Concept { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class StreamerClipperState
{
    public string Niche { get; set; } = "";
    public string Format { get; set; } = "Streamer Ratings";
    public string ChatUrl { get; set; } = "";
    public string SourceFolder { get; set; } = "";
    public string SoundFile { get; set; } = "";
    public string FirstSlot { get; set; } = "";
    public bool AutoDownload { get; set; } = true;
    public bool BrowserLogin { get; set; }
    public bool Publish { get; set; } = true;
    public double MusicLevel { get; set; } = .85;
    public List<StreamerClipperEntry> Ideas { get; set; } = [];
    public List<StreamerClipperHistoryItem> History { get; set; } = [];
}

public sealed partial class MainViewModel
{
    private static readonly string StreamerRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZeroVideo", "STREAMERClipper");
    private static readonly string StreamerStatePath = Path.Combine(StreamerRoot, "state.json");
    private string _streamerNiche = "Popular Twitch Kick and YouTube streamers";
    private string _streamerFormat = "Streamer Ratings";
    private int _streamerCount = 10;
    private string _streamerSourceFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "ZeroVideo", "STREAMERHighlights");
    private string _streamerSoundFile = "";
    private string _streamerStatus = "Ready: ChatGPT player ideas and scores → Twitch Kick and YouTube streamer moment videos → beat-synced edit → TikTok queue.";
    private string _streamerFirstSlot = DateTime.Now.AddHours(3).ToString("yyyy-MM-dd HH:00",CultureInfo.InvariantCulture);
    private bool _streamerPublish = true;
    private bool _streamerAutoDownload = true;
    private bool _streamerBrowserLogin;
    private bool _streamerNewChat;
    private bool _streamerProcessing;
    private bool _streamerRetryFailed;
    private double _streamerMusicLevel = 0.85;
    private VideoProject _streamerChat = new() { Title = "STREAMERClipper batch", Topic = "Individual streamer videos" };
    private readonly List<StreamerClipperHistoryItem> _streamerHistory = [];
    public ObservableCollection<StreamerClipperEntry> StreamerClipperIdeas { get; } = [];
    public ObservableCollection<StreamerClipperHistoryItem> StreamerHistory { get; } = [];
    public ObservableCollection<string> StreamerFormats { get; } = ["Streamer Ratings", "Funny Moments", "Viral Reactions"];
    public string StreamerNiche { get => _streamerNiche; set => Set(ref _streamerNiche,value); }
    public string StreamerFormat { get => _streamerFormat; set => Set(ref _streamerFormat,value); }
    public int StreamerCount { get => _streamerCount; set => Set(ref _streamerCount,Math.Clamp(value,1,20)); }
    public string StreamerSourceFolder { get => _streamerSourceFolder; set => Set(ref _streamerSourceFolder,value); }
    public string StreamerSoundFile { get => _streamerSoundFile; set => Set(ref _streamerSoundFile,value); }
    public string StreamerStatus { get => _streamerStatus; set => Set(ref _streamerStatus,value); }
    public string StreamerFirstSlot { get => _streamerFirstSlot; set => Set(ref _streamerFirstSlot,value); }
    public bool StreamerPublish { get => _streamerPublish; set => Set(ref _streamerPublish,value); }
    public bool StreamerAutoDownload { get => _streamerAutoDownload; set => Set(ref _streamerAutoDownload,value); }
    public bool StreamerBrowserLogin { get => _streamerBrowserLogin; set => Set(ref _streamerBrowserLogin,value); }
    public bool StreamerNewChat { get => _streamerNewChat; set => Set(ref _streamerNewChat,value); }
    public double StreamerMusicLevel { get => _streamerMusicLevel; set => Set(ref _streamerMusicLevel,Math.Clamp(value,0,1)); }
    public string StreamerHistorySummary => StreamerHistory.Count+" finished player concepts in STREAMER history";
    public string StreamerCheckpointStatus => StreamerClipperIdeas.Count+" saved player ideas; "+StreamerStatePath;
    public string StreamerExportFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "ZeroVideo","STREAMERClipper");
    public ICommand StreamerGenerateCommand { get; private set; } = null!;
    public ICommand StreamerStartCommand { get; private set; } = null!;
    public ICommand StreamerResumeCommand { get; private set; } = null!;
    public ICommand StreamerRetryFailedCommand { get; private set; } = null!;
    public ICommand StreamerBrowseSourceCommand { get; private set; } = null!;
    public ICommand StreamerBrowseSoundCommand { get; private set; } = null!;
    public ICommand StreamerFindHighlightsCommand { get; private set; } = null!;
    public ICommand StreamerOpenOutputCommand { get; private set; } = null!;
    public ICommand StreamerForceStopCommand => ForceStopJobCommand;

    private void InitializeStreamerClipperCommands()
    {
        StreamerRestore();
        StreamerGenerateCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "STREAMERClipper: individual player ideas and SEO",StreamerGenerateAsync));
        StreamerStartCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "STREAMERClipper: one-click Twitch Kick and YouTube streamer moment videos",StreamerStartAsync,publishing:StreamerPublish));
        StreamerResumeCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "STREAMERClipper: resume",async()=>{
                StreamerRestore();
                _streamerRetryFailed=false;
                await StreamerStartAsync();
            },publishing:StreamerPublish));
        StreamerRetryFailedCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "STREAMERClipper: retry failures",async()=>{
                StreamerRestore();
                _streamerRetryFailed=true;
                try{await StreamerStartAsync();}
                finally{_streamerRetryFailed=false;}
            },publishing:StreamerPublish));
        StreamerBrowseSourceCommand=new RelayCommand(()=>{
            var picker=new OpenFolderDialog{Title="STREAMER player highlight source/cache folder"};
            if(picker.ShowDialog()==true){StreamerSourceFolder=picker.FolderName;StreamerSave();}
        });
        StreamerBrowseSoundCommand=new RelayCommand(()=>{
            var picker=new OpenFileDialog{
                Filter="Audio files|*.mp3;*.m4a;*.wav;*.aac;*.flac",
                CheckFileExists=true};
            if(picker.ShowDialog()==true){StreamerSoundFile=picker.FileName;StreamerSave();}
        });
        StreamerFindHighlightsCommand=new RelayCommand(()=>{
            var player=StreamerClipperIdeas.FirstOrDefault(x=>x.IsChecked);
            if(player is null){StreamerStatus="Select an STREAMER player first.";return;}
            var query=player.Name+" STREAMER individual highlights";
            Process.Start(new ProcessStartInfo(
                "https://www.youtube.com/results?search_query="+Uri.EscapeDataString(query))
                {UseShellExecute=true});
            StreamerStatus="Opened player-specific highlights search. Use footage you are authorized to edit and post.";
        });
        StreamerOpenOutputCommand=new RelayCommand(()=>{
            Directory.CreateDirectory(StreamerExportFolder);
            Process.Start(new ProcessStartInfo("explorer.exe","\""+StreamerExportFolder+"\"")
                {UseShellExecute=true});
        });
    }

    private void StreamerSave()
    {
        Directory.CreateDirectory(StreamerRoot);
        var snapshot=new StreamerClipperState{
            Niche=StreamerNiche,Format=StreamerFormat,
            ChatUrl=_streamerChat.ChatGptConversationUrl??"",
            SourceFolder=StreamerSourceFolder,SoundFile=StreamerSoundFile,
            FirstSlot=StreamerFirstSlot,AutoDownload=StreamerAutoDownload,
            BrowserLogin=StreamerBrowserLogin,Publish=StreamerPublish,
            MusicLevel=StreamerMusicLevel,
            Ideas=StreamerClipperIdeas.ToList(),
            History=_streamerHistory.ToList()
        };
        var path=StreamerStatePath+".tmp";
        File.WriteAllText(path,JsonSerializer.Serialize(snapshot,new JsonSerializerOptions{
            WriteIndented=true}));
        File.Move(path,StreamerStatePath,true);
        Raise(nameof(StreamerCheckpointStatus));
        Raise(nameof(StreamerHistorySummary));
    }

    private void StreamerRestore()
    {
        try
        {
            if(!File.Exists(StreamerStatePath))return;
            var state=JsonSerializer.Deserialize<StreamerClipperState>(
                File.ReadAllText(StreamerStatePath));
            if(state is null)return;
            if(state.Niche.Length>0)StreamerNiche=state.Niche;
            if(StreamerFormats.Contains(state.Format))StreamerFormat=state.Format;
            if(!string.IsNullOrWhiteSpace(state.SourceFolder))
                StreamerSourceFolder=state.SourceFolder;
            if(!string.IsNullOrWhiteSpace(state.SoundFile))
                StreamerSoundFile=state.SoundFile;
            // Keep the future default slot instead of restoring a time in
            // the past from a previous session.
            if(DateTime.TryParseExact(state.FirstSlot,"yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture,DateTimeStyles.AssumeLocal,
                out var restoredSlot)&&restoredSlot>DateTime.Now.AddMinutes(25))
                StreamerFirstSlot=state.FirstSlot;
            StreamerAutoDownload=state.AutoDownload;
            StreamerBrowserLogin=state.BrowserLogin;
            StreamerPublish=state.Publish;
            StreamerMusicLevel=state.MusicLevel;
            _streamerChat=new VideoProject{
                Title="STREAMERClipper batch",Topic=StreamerNiche,
                ChatGptConversationUrl=string.IsNullOrWhiteSpace(state.ChatUrl)?null:state.ChatUrl};
            StreamerClipperIdeas.Clear();
            foreach(var item in state.Ideas)StreamerClipperIdeas.Add(item);
            _streamerHistory.Clear();
            _streamerHistory.AddRange(state.History);
            StreamerHistory.Clear();
            foreach(var item in _streamerHistory.OrderByDescending(x=>x.CompletedAt))
                StreamerHistory.Add(item);
            Raise(nameof(StreamerHistorySummary));
            Raise(nameof(StreamerCheckpointStatus));
        }
        catch(Exception ex){StreamerStatus="STREAMER checkpoint load failed: "+ex.Message;}
    }

    private void StreamerPrepareChat()
    {
        if(StreamerNewChat)
        {
            _streamerChat=new VideoProject{Title="STREAMERClipper batch: "+StreamerNiche,Topic=StreamerNiche};
            _chat.RequestNewChat();
            StreamerNewChat=false;
            StreamerSave();
        }
    }

    private static string StreamerJson(JsonElement node,string key)=>
        node.ValueKind==JsonValueKind.Object &&
        node.TryGetProperty(key,out var field)&&field.ValueKind==JsonValueKind.String?
        field.GetString()??"":"";

    private static int StreamerId(string name,string concept)
    {
        unchecked{
            uint h=2166136261;
            foreach(var c in ("Streamer|"+name+"|"+concept).ToUpperInvariant()){
                h^=c;h*=16777619;
            }
            return (int)(h&0x7fffffff);
        }
    }

    private async Task StreamerGenerateAsync()
    {
        try
        {
            StreamerStatus="ChatGPT: generating individual STREAMER player concepts, six ratings and SEO in ONE request";
            StreamerPrepareChat();
            var completed=_streamerHistory.Select(x=>x.Name+" — "+x.Concept)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(200);
            var prompt="Generate exactly "+StreamerCount+" REAL individual streamer ideas, one subject per video, no teams, duos or comparisons. "+
                "Niche: "+StreamerNiche+". Video format: "+StreamerFormat+". "+
                "Avoid completed name/concept pairs: "+string.Join("; ",completed)+". "+
                "For anime and gaming characters identify the canonical series or game in concept; for real people avoid invented achievements. "+
                "In the SAME response generate six SUBJECTIVE scores 0.0-10.0 for: humor, reactions, gameplay, entertainment, creativity, presence. "+
                "Those are editorial ratings, never official statistics or sourced review scores. "+
                "Write one accurate SEO title (40-85 chars), short caption (100-240 chars) and 3-5 hashtags. "+
                "Use only genuine known subjects; do not invent people, titles, games, broadcasts, stats or achievements. "+
                "Return ONLY a valid JSON object inside fenced json: "+
                "{\"response_type\":\"streamerclipper_ideas_v1\",\"players\":[{\"name\":\"Kai Cenat\",\"concept\":\"Iconic individual highlight moments\",\"scores\":{\"humor\":8,\"reactions\":8,\"gameplay\":8,\"entertainment\":8,\"creativity\":8,\"presence\":8},\"seo\":{\"title\":\"Individual character or person highlights and ratings\",\"caption\":\"A short-form individual spotlight with subjective category scores.\",\"hashtags\":\"#Highlights #Clips #Edits\"}}]}";
            var raw=await _chat.GenerateTextAsync(_streamerChat,Settings,prompt,Log,_cts.Token);
            var first=raw.IndexOf('{');var last=raw.LastIndexOf('}');
            if(first<0||last<=first)throw new InvalidDataException("ChatGPT did not return JSON.");
            using var json=JsonDocument.Parse(raw.Substring(first,last-first+1));
            var players=json.RootElement.GetProperty("players");
            if(players.ValueKind!=JsonValueKind.Array)
                throw new InvalidDataException("Expected players JSON array.");
            var ready=new List<StreamerClipperEntry>();
            var seen=new HashSet<int>();
            foreach(var candidate in players.EnumerateArray().Take(StreamerCount*3))
            {
                if(candidate.ValueKind!=JsonValueKind.Object)continue;
                var name=StreamerJson(candidate,"name").Trim();
                var concept=StreamerJson(candidate,"concept").Trim();
                if(name.Length is < 4 or > 100||concept.Length is < 4 or > 130||
                    name.Any(char.IsControl))continue;
                var id=StreamerId(name,concept);
                if(!seen.Add(id)||_streamerHistory.Any(x=>x.PlayerId==id)||
                    PublishingQueue.Any(x=>x.VideoPath.Contains(
                        id.ToString(CultureInfo.InvariantCulture)+"_",
                        StringComparison.OrdinalIgnoreCase)))continue;
                if(!candidate.TryGetProperty("scores",out var scores)||
                    scores.ValueKind!=JsonValueKind.Object)continue;
                string[] keys=["humor","reactions","gameplay","entertainment","creativity","presence"];
                var values=keys.Select(k=>scores.TryGetProperty(k,out var value)&&
                    value.ValueKind==JsonValueKind.Number?
                    value.GetDouble():double.NaN).ToArray();
                if(values.Any(x=>!double.IsFinite(x)||x<0||x>10))continue;
                var seo=candidate.TryGetProperty("seo",out var info)&&
                    info.ValueKind==JsonValueKind.Object?info:default;
                ready.Add(new StreamerClipperEntry{
                    PlayerId=id,Name=name,Concept=concept,Mode=StreamerFormat,
                    Scores=values.Select(x=>Math.Round(x,1)).ToArray(),
                    SeoTitle=StreamerJson(seo,"title"),SeoCaption=StreamerJson(seo,"caption"),
                    SeoHashtags=StreamerJson(seo,"hashtags")
                });
                if(ready.Count>=StreamerCount)break;
            }
            if(ready.Count==0)throw new InvalidDataException("No valid STREAMER player ideas received.");
            StreamerClipperIdeas.Clear();
            foreach(var item in ready)StreamerClipperIdeas.Add(item);
            StreamerSave();
            StreamerStatus=ready.Count+" individual STREAMER player ideas ready. "+
                "Ratings are subjective; actual STREAMER statistics are not invented.";
        }
        catch(OperationCanceledException){StreamerSave();throw;}
        catch(Exception ex){StreamerStatus="STREAMER idea generation failed: "+ex.Message;Log(StreamerStatus);}
    }

    private static string? StreamerCachedSource(string folder,StreamerClipperEntry player)
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

    private async Task<string> StreamerDownloadAsync(StreamerClipperEntry player)
    {
        Directory.CreateDirectory(StreamerSourceFolder);
        var target=Path.Combine(StreamerSourceFolder,player.PlayerId+".mp4");
        var downloader=MovieDownloaderPath();
        var search=player.Name+" funny moments best clips stream highlights";
        player.Status="Finding player-specific YouTube highlights";
        var args=new List<string>{"--no-playlist","--flat-playlist","--dump-single-json",
            "--skip-download","--no-warnings","ytsearch12:"+search};
        if(StreamerBrowserLogin)args.InsertRange(0,["--cookies-from-browser","edge"]);
        var result=await MovieRunProcessAsync(downloader,args,_cts.Token);
        if(result.Code!=0)throw new InvalidOperationException("YouTube highlight search: "+
            MovieDownloaderError(result.Error));
        using var json=JsonDocument.Parse(result.Output);
        if(!json.RootElement.TryGetProperty("entries",out var entries)||
            entries.ValueKind!=JsonValueKind.Array)
            throw new InvalidDataException("No Twitch Kick and YouTube streamer moment videos found on YouTube.");
        var names=player.Name.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        var sources=new List<(string Url,int Priority)>();
        foreach(var e in entries.EnumerateArray())
        {
            if(e.ValueKind!=JsonValueKind.Object)continue;
            var title=StreamerJson(e,"title");
            var videoId=StreamerJson(e,"id");
            var lower=title.ToLowerInvariant();
            if(videoId.Length!=11||
                !videoId.All(c=>char.IsAsciiLetterOrDigit(c)||c=='-'||c=='_')||
                !names.All(n=>title.Contains(n,StringComparison.OrdinalIgnoreCase))||
                !new[]{"highlights","funny","moments","clips","rage","react","win"}
                    .Any(lower.Contains)||
                new[]{"interview","podcast","breakdown","fan made","fanmade","simulation"}
                    .Any(lower.Contains))continue;
            sources.Add(("https://www.youtube.com/watch?v="+videoId,
                (lower.Contains("nba")?10:0)+(lower.Contains("official")?5:0)));
        }
        if(sources.Count==0)throw new InvalidOperationException(
            "No streamer highlight source found. Import an authorized local clip.");
        var pattern=Path.Combine(StreamerSourceFolder,player.PlayerId+".%(ext)s");
        var failures=new List<string>();
        foreach(var url in sources.OrderByDescending(x=>x.Priority)
            .Select(x=>x.Url).Distinct().Take(4))
        {
            _cts.Token.ThrowIfCancellationRequested();
            var download=new List<string>{"--no-playlist","--no-progress",
                "--retries","2","-f","bv*+ba/b","-S","res:1080,ext:mp4",
                "--merge-output-format","mp4","--recode-video","mp4",
                "-o",pattern,url};
            if(StreamerBrowserLogin)download.InsertRange(0,["--cookies-from-browser","edge"]);
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

    private async Task<PublishQueueItem> StreamerRenderAsync(StreamerClipperEntry player,string sourcePath)
    {
        var music=StreamerSoundFile;
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
        var work=Path.Combine(StreamerRoot,player.PlayerId+"-"+
            DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(StreamerExportFolder);
        var cover=Path.Combine(work,"thumbnail.jpg");
        var video=Path.Combine(StreamerExportFolder,player.PlayerId+"_"+
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
        // Unlike MovieClipper, STREAMERClipper must never crop basketball
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
        var ratingsMode=player.Mode=="Streamer Ratings";
        var labels=ratingsMode
            ?new[]{"HUMOR","REACTIONS","GAMEPLAY","ENTERTAINMENT","CREATIVITY","PRESENCE","OVERALL"}
            :player.Mode=="Funny Moments"
                ?new[]{"CLIP 01","CLIP 02","CLIP 03","CLIP 04","CLIP 05","CLIP 06","FUNNIEST MOMENTS"}
                :new[]{"THE STREAMER","THE REACTION","ICONIC MOMENTS","THE STREAM","VIRAL CLIPS","THE HIGHLIGHT","VIRAL REACTIONS"};
        var values=player.Scores.Concat([Math.Round(player.Scores.Average(),1)]).ToArray();
        var intro=ratingsMode?"STREAMER RATINGS":
            player.Mode=="Funny Moments"?"FUNNY MOMENTS":"VIRAL REACTIONS";
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
            ",asetpts=PTS-STARTPTS,volume="+MovieNumber(StreamerMusicLevel)+"[a]");
        args.AddRange(["-filter_complex",string.Join(";",filters),
            "-map","[v]","-map","[a]","-t",MovieNumber(seconds),
            "-frames:v",frames.ToString(CultureInfo.InvariantCulture),
            "-c:v","libx264","-preset","veryfast","-crf","21",
            "-c:a","aac","-b:a","160k","-pix_fmt","yuv420p",
            "-movflags","+faststart",video]);
        player.Status="Rendering "+count+" beat-synced Twitch Kick and YouTube streamer moment videos";
        var result=await MovieRunProcessAsync(ffmpeg,args,_cts.Token,work);
        if(result.Code!=0)
            throw new InvalidOperationException("FFmpeg: "+
                result.Error[^Math.Min(result.Error.Length,900)..]);
        if(!File.Exists(video)||new FileInfo(video).Length<10000)
            throw new IOException("STREAMERClipper video file was not generated.");
        player.OutputPath=video;
        player.ThumbnailPath=cover;
        StreamerSave();
        return new PublishQueueItem{
            VideoPath=video,ThumbnailPath=cover,
            Title=player.Name+" — "+player.Concept,Description=player.Concept,
            TikTokCaption=player.Name+" STREAMER player ratings #STREAMER #Basketball",
            YouTubeTitle=player.Name+" — "+player.Concept+" #Shorts",
            YouTubeDescription=player.Concept
        };
    }

    private static void StreamerEnsureSeo(StreamerClipperEntry player)
    {
        if(player.SeoTitle.Length is < 15 or > 120 ||
            !player.SeoTitle.Contains(player.Name,StringComparison.OrdinalIgnoreCase))
            player.SeoTitle=player.Name+" — "+player.Concept+" | STREAMER Player Review";
        if(player.SeoCaption.Length is < 25 or > 400)
            player.SeoCaption=player.Name+" STREAMER player spotlight: "+player.Concept+
                ". Shooting, finishing, handles, playmaking, defense and IQ scored out of 10.";
        var tags=Regex.Matches(player.SeoHashtags,
            @"(?<!\S)#[A-Za-z][A-Za-z0-9_]{1,24}(?=\s|$)")
            .Select(x=>x.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        if(tags.Count<3)tags=["#STREAMER","#Basketball","#PlayerRatings"];
        player.SeoHashtags=string.Join(" ",tags);
    }

    private async Task StreamerStartAsync()
    {
        if(_streamerProcessing)return;
        _streamerProcessing=true;
        StreamerPrepareChat();
        try
        {
            var ideasWereEmpty=StreamerClipperIdeas.Count==0;
            if(ideasWereEmpty)await StreamerGenerateAsync();
            var selected=StreamerClipperIdeas.Where(x=>x.IsChecked&&
                (!_streamerRetryFailed||ideasWereEmpty||x.Status.StartsWith("Failed",
                    StringComparison.OrdinalIgnoreCase))).ToList();
            if(selected.Count==0){
                StreamerStatus="No pending streamer ideas selected.";return;
            }
            if(!DateTime.TryParseExact(StreamerFirstSlot,"yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture,DateTimeStyles.AssumeLocal,out var first)||
                first<=DateTime.Now.AddMinutes(25)||first.Minute%5!=0)
                throw new InvalidDataException("First posting slot must be 25+ minutes away, on 5-minute increments.");
            if(!File.Exists(StreamerSoundFile))
                throw new FileNotFoundException("Select a background soundtrack first.",StreamerSoundFile);
            var originallyChecked=PublishingQueue.Where(x=>x.IsChecked).ToList();
            var completed=new List<PublishQueueItem>();
            foreach(var player in selected)
            {
                _cts.Token.ThrowIfCancellationRequested();
                try
                {
                    if(_streamerHistory.Any(h=>h.PlayerId==player.PlayerId)||
                        (!string.IsNullOrWhiteSpace(player.OutputPath)&&
                         PublishingQueue.Any(q=>q.VideoPath.Equals(player.OutputPath,
                             StringComparison.OrdinalIgnoreCase))))
                    {
                        player.Status="Already queued (no duplicate)";
                        player.IsChecked=false;StreamerSave();continue;
                    }
                    var source=File.Exists(player.SourcePath)?player.SourcePath:
                        StreamerCachedSource(StreamerSourceFolder,player);
                    if(source is null&&StreamerAutoDownload)
                        source=await StreamerDownloadAsync(player);
                    if(source is null)
                    {
                        player.Status="Failed: player highlight source unavailable";
                        StreamerSave();continue;
                    }
                    player.SourcePath=source;player.Status="HighlightsReady";StreamerSave();
                    var output=File.Exists(player.OutputPath)?
                        new PublishQueueItem{VideoPath=player.OutputPath,
                            ThumbnailPath=player.ThumbnailPath}:
                        await StreamerRenderAsync(player,source);
                    StreamerEnsureSeo(player);
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
                    StreamerSave();SavePublishingQueue();
                    var history=new StreamerClipperHistoryItem{
                        PlayerId=player.PlayerId,Name=player.Name,Concept=player.Concept,
                        OutputPath=output.VideoPath};
                    _streamerHistory.Add(history);
                    StreamerHistory.Insert(0,history);
                    StreamerSave();
                }
                catch(OperationCanceledException){player.Status="Stopped";StreamerSave();throw;}
                catch(Exception ex){
                    player.Status="Failed: "+ex.Message;Log("STREAMERClipper "+player.Name+": "+ex.Message);
                    StreamerSave();
                }
            }
            Raise(nameof(AllPublishItemsChecked));
            RefreshPublishingCalendar();
            RefreshVideoLibrary();
            StreamerStatus="Exported "+completed.Count+"/"+selected.Count+
                " individual STREAMER player videos to "+StreamerExportFolder+".";
            if(completed.Count==0||!StreamerPublish)return;
            if(!PublishConsent){
                StreamerStatus+=" Enable existing publishing authorization to auto-post.";
                return;
            }
            foreach(var old in originallyChecked)old.IsChecked=false;
            try{await RunTikTokBatchAsync();}
            finally{foreach(var old in originallyChecked)old.IsChecked=true;}
        }
        catch(OperationCanceledException){StreamerStatus="Stopped. Resume saved STREAMER workflow.";StreamerSave();throw;}
        catch(Exception ex){StreamerStatus="STREAMERClipper: "+ex.Message;Log(StreamerStatus);}
        finally{_streamerProcessing=false;StreamerSave();}
    }
}
