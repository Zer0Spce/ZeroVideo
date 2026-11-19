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

public sealed class AnimeClipperEntry : ObservableObject
{
    public int PlayerId { get; set; }
    public string Name { get; set; } = "";
    public string Concept { get; set; } = "";
    public string Mode { get; set; } = "Character Ratings";
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

public sealed class AnimeClipperHistoryItem
{
    public int PlayerId { get; set; }
    public string Name { get; set; } = "";
    public string Concept { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AnimeClipperState
{
    public string Niche { get; set; } = "";
    public string Format { get; set; } = "Character Ratings";
    public string ChatUrl { get; set; } = "";
    public string SourceFolder { get; set; } = "";
    public string SoundFile { get; set; } = "";
    public string FirstSlot { get; set; } = "";
    public bool AutoDownload { get; set; } = true;
    public bool BrowserLogin { get; set; }
    public bool Publish { get; set; } = true;
    public double MusicLevel { get; set; } = .85;
    public List<AnimeClipperEntry> Ideas { get; set; } = [];
    public List<AnimeClipperHistoryItem> History { get; set; } = [];
}

public sealed partial class MainViewModel
{
    private static readonly string AnimeRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZeroVideo", "ANIMEClipper");
    private static readonly string AnimeStatePath = Path.Combine(AnimeRoot, "state.json");
    private string _animeNiche = "Popular anime characters and iconic character arcs";
    private string _animeFormat = "Character Ratings";
    private int _animeCount = 10;
    private string _animeSourceFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "ZeroVideo", "ANIMEHighlights");
    private string _animeSoundFile = "";
    private string _animeStatus = "Ready: ChatGPT player ideas and scores → individual anime character animation scenes → beat-synced edit → TikTok queue.";
    private string _animeFirstSlot = DateTime.Now.AddHours(3).ToString("yyyy-MM-dd HH:00",CultureInfo.InvariantCulture);
    private bool _animePublish = true;
    private bool _animeAutoDownload = true;
    private bool _animeBrowserLogin;
    private bool _animeNewChat;
    private bool _animeProcessing;
    private bool _animeRetryFailed;
    private double _animeMusicLevel = 0.85;
    private VideoProject _animeChat = new() { Title = "ANIMEClipper batch", Topic = "Individual anime character videos" };
    private readonly List<AnimeClipperHistoryItem> _animeHistory = [];
    public ObservableCollection<AnimeClipperEntry> AnimeClipperIdeas { get; } = [];
    public ObservableCollection<AnimeClipperHistoryItem> AnimeHistory { get; } = [];
    public ObservableCollection<string> AnimeFormats { get; } = ["Character Ratings", "Epic Battles", "Character Legacy"];
    public string AnimeNiche { get => _animeNiche; set => Set(ref _animeNiche,value); }
    public string AnimeFormat { get => _animeFormat; set => Set(ref _animeFormat,value); }
    public int AnimeCount { get => _animeCount; set => Set(ref _animeCount,Math.Clamp(value,1,20)); }
    public string AnimeSourceFolder { get => _animeSourceFolder; set => Set(ref _animeSourceFolder,value); }
    public string AnimeSoundFile { get => _animeSoundFile; set => Set(ref _animeSoundFile,value); }
    public string AnimeStatus { get => _animeStatus; set => Set(ref _animeStatus,value); }
    public string AnimeFirstSlot { get => _animeFirstSlot; set => Set(ref _animeFirstSlot,value); }
    public bool AnimePublish { get => _animePublish; set => Set(ref _animePublish,value); }
    public bool AnimeAutoDownload { get => _animeAutoDownload; set => Set(ref _animeAutoDownload,value); }
    public bool AnimeBrowserLogin { get => _animeBrowserLogin; set => Set(ref _animeBrowserLogin,value); }
    public bool AnimeNewChat { get => _animeNewChat; set => Set(ref _animeNewChat,value); }
    public double AnimeMusicLevel { get => _animeMusicLevel; set => Set(ref _animeMusicLevel,Math.Clamp(value,0,1)); }
    public string AnimeHistorySummary => AnimeHistory.Count+" finished player concepts in ANIME history";
    public string AnimeCheckpointStatus => AnimeClipperIdeas.Count+" saved player ideas; "+AnimeStatePath;
    public string AnimeExportFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "ZeroVideo","ANIMEClipper");
    public ICommand AnimeGenerateCommand { get; private set; } = null!;
    public ICommand AnimeStartCommand { get; private set; } = null!;
    public ICommand AnimeResumeCommand { get; private set; } = null!;
    public ICommand AnimeRetryFailedCommand { get; private set; } = null!;
    public ICommand AnimeBrowseSourceCommand { get; private set; } = null!;
    public ICommand AnimeBrowseSoundCommand { get; private set; } = null!;
    public ICommand AnimeFindHighlightsCommand { get; private set; } = null!;
    public ICommand AnimeOpenOutputCommand { get; private set; } = null!;
    public ICommand AnimeForceStopCommand => ForceStopJobCommand;

    private void InitializeAnimeClipperCommands()
    {
        AnimeRestore();
        AnimeGenerateCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "ANIMEClipper: individual player ideas and SEO",AnimeGenerateAsync));
        AnimeStartCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "ANIMEClipper: one-click individual anime character animation scenes",AnimeStartAsync,publishing:AnimePublish));
        AnimeResumeCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "ANIMEClipper: resume",async()=>{
                AnimeRestore();
                _animeRetryFailed=false;
                await AnimeStartAsync();
            },publishing:AnimePublish));
        AnimeRetryFailedCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "ANIMEClipper: retry failures",async()=>{
                AnimeRestore();
                _animeRetryFailed=true;
                try{await AnimeStartAsync();}
                finally{_animeRetryFailed=false;}
            },publishing:AnimePublish));
        AnimeBrowseSourceCommand=new RelayCommand(()=>{
            var picker=new OpenFolderDialog{Title="ANIME player highlight source/cache folder"};
            if(picker.ShowDialog()==true){AnimeSourceFolder=picker.FolderName;AnimeSave();}
        });
        AnimeBrowseSoundCommand=new RelayCommand(()=>{
            var picker=new OpenFileDialog{
                Filter="Audio files|*.mp3;*.m4a;*.wav;*.aac;*.flac",
                CheckFileExists=true};
            if(picker.ShowDialog()==true){AnimeSoundFile=picker.FileName;AnimeSave();}
        });
        AnimeFindHighlightsCommand=new RelayCommand(()=>{
            var player=AnimeClipperIdeas.FirstOrDefault(x=>x.IsChecked);
            if(player is null){AnimeStatus="Select an ANIME player first.";return;}
            var query=player.Name+" ANIME individual highlights";
            Process.Start(new ProcessStartInfo(
                "https://www.youtube.com/results?search_query="+Uri.EscapeDataString(query))
                {UseShellExecute=true});
            AnimeStatus="Opened player-specific highlights search. Use footage you are authorized to edit and post.";
        });
        AnimeOpenOutputCommand=new RelayCommand(()=>{
            Directory.CreateDirectory(AnimeExportFolder);
            Process.Start(new ProcessStartInfo("explorer.exe","\""+AnimeExportFolder+"\"")
                {UseShellExecute=true});
        });
    }

    private void AnimeSave()
    {
        Directory.CreateDirectory(AnimeRoot);
        var snapshot=new AnimeClipperState{
            Niche=AnimeNiche,Format=AnimeFormat,
            ChatUrl=_animeChat.ChatGptConversationUrl??"",
            SourceFolder=AnimeSourceFolder,SoundFile=AnimeSoundFile,
            FirstSlot=AnimeFirstSlot,AutoDownload=AnimeAutoDownload,
            BrowserLogin=AnimeBrowserLogin,Publish=AnimePublish,
            MusicLevel=AnimeMusicLevel,
            Ideas=AnimeClipperIdeas.ToList(),
            History=_animeHistory.ToList()
        };
        var path=AnimeStatePath+".tmp";
        File.WriteAllText(path,JsonSerializer.Serialize(snapshot,new JsonSerializerOptions{
            WriteIndented=true}));
        File.Move(path,AnimeStatePath,true);
        Raise(nameof(AnimeCheckpointStatus));
        Raise(nameof(AnimeHistorySummary));
    }

    private void AnimeRestore()
    {
        try
        {
            if(!File.Exists(AnimeStatePath))return;
            var state=JsonSerializer.Deserialize<AnimeClipperState>(
                File.ReadAllText(AnimeStatePath));
            if(state is null)return;
            if(state.Niche.Length>0)AnimeNiche=state.Niche;
            if(AnimeFormats.Contains(state.Format))AnimeFormat=state.Format;
            if(!string.IsNullOrWhiteSpace(state.SourceFolder))
                AnimeSourceFolder=state.SourceFolder;
            if(!string.IsNullOrWhiteSpace(state.SoundFile))
                AnimeSoundFile=state.SoundFile;
            // Keep the future default slot instead of restoring a time in
            // the past from a previous session.
            if(DateTime.TryParseExact(state.FirstSlot,"yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture,DateTimeStyles.AssumeLocal,
                out var restoredSlot)&&restoredSlot>DateTime.Now.AddMinutes(25))
                AnimeFirstSlot=state.FirstSlot;
            AnimeAutoDownload=state.AutoDownload;
            AnimeBrowserLogin=state.BrowserLogin;
            AnimePublish=state.Publish;
            AnimeMusicLevel=state.MusicLevel;
            _animeChat=new VideoProject{
                Title="ANIMEClipper batch",Topic=AnimeNiche,
                ChatGptConversationUrl=string.IsNullOrWhiteSpace(state.ChatUrl)?null:state.ChatUrl};
            AnimeClipperIdeas.Clear();
            foreach(var item in state.Ideas)AnimeClipperIdeas.Add(item);
            _animeHistory.Clear();
            _animeHistory.AddRange(state.History);
            AnimeHistory.Clear();
            foreach(var item in _animeHistory.OrderByDescending(x=>x.CompletedAt))
                AnimeHistory.Add(item);
            Raise(nameof(AnimeHistorySummary));
            Raise(nameof(AnimeCheckpointStatus));
        }
        catch(Exception ex){AnimeStatus="ANIME checkpoint load failed: "+ex.Message;}
    }

    private void AnimePrepareChat()
    {
        if(AnimeNewChat)
        {
            _animeChat=new VideoProject{Title="ANIMEClipper batch: "+AnimeNiche,Topic=AnimeNiche};
            _chat.RequestNewChat();
            AnimeNewChat=false;
            AnimeSave();
        }
    }

    private static string AnimeJson(JsonElement node,string key)=>
        node.ValueKind==JsonValueKind.Object &&
        node.TryGetProperty(key,out var field)&&field.ValueKind==JsonValueKind.String?
        field.GetString()??"":"";

    private static int AnimeId(string name,string concept)
    {
        unchecked{
            uint h=2166136261;
            foreach(var c in ("Anime|"+name+"|"+concept).ToUpperInvariant()){
                h^=c;h*=16777619;
            }
            return (int)(h&0x7fffffff);
        }
    }

    private async Task AnimeGenerateAsync()
    {
        try
        {
            AnimeStatus="ChatGPT: generating individual ANIME player concepts, six ratings and SEO in ONE request";
            AnimePrepareChat();
            var completed=_animeHistory.Select(x=>x.Name+" — "+x.Concept)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(200);
            var prompt="Generate exactly "+AnimeCount+" REAL individual anime character ideas, one subject per video, no teams, duos or comparisons. "+
                "Niche: "+AnimeNiche+". Video format: "+AnimeFormat+". "+
                "Avoid completed name/concept pairs: "+string.Join("; ",completed)+". "+
                "For anime and gaming characters identify the canonical series or game in concept; for real people avoid invented achievements. "+
                "In the SAME response generate six SUBJECTIVE scores 0.0-10.0 for: power, speed, technique, intelligence, durability, impact. "+
                "Those are editorial ratings, never official statistics or sourced review scores. "+
                "Write one accurate SEO title (40-85 chars), short caption (100-240 chars) and 3-5 hashtags. "+
                "Use only genuine known subjects; do not invent people, titles, games, broadcasts, stats or achievements. "+
                "Return ONLY a valid JSON object inside fenced json: "+
                "{\"response_type\":\"animeclipper_ideas_v1\",\"players\":[{\"name\":\"Naruto Uzumaki\",\"concept\":\"Iconic individual highlight moments\",\"scores\":{\"power\":8,\"speed\":8,\"technique\":8,\"intelligence\":8,\"durability\":8,\"impact\":8},\"seo\":{\"title\":\"Individual character or person highlights and ratings\",\"caption\":\"A short-form individual spotlight with subjective category scores.\",\"hashtags\":\"#Highlights #Clips #Edits\"}}]}";
            var raw=await _chat.GenerateTextAsync(_animeChat,Settings,prompt,Log,_cts.Token);
            var first=raw.IndexOf('{');var last=raw.LastIndexOf('}');
            if(first<0||last<=first)throw new InvalidDataException("ChatGPT did not return JSON.");
            using var json=JsonDocument.Parse(raw.Substring(first,last-first+1));
            var players=json.RootElement.GetProperty("players");
            if(players.ValueKind!=JsonValueKind.Array)
                throw new InvalidDataException("Expected players JSON array.");
            var ready=new List<AnimeClipperEntry>();
            var seen=new HashSet<int>();
            foreach(var candidate in players.EnumerateArray().Take(AnimeCount*3))
            {
                if(candidate.ValueKind!=JsonValueKind.Object)continue;
                var name=AnimeJson(candidate,"name").Trim();
                var concept=AnimeJson(candidate,"concept").Trim();
                if(name.Length is < 4 or > 100||concept.Length is < 4 or > 130||
                    name.Any(char.IsControl))continue;
                var id=AnimeId(name,concept);
                if(!seen.Add(id)||_animeHistory.Any(x=>x.PlayerId==id)||
                    PublishingQueue.Any(x=>x.VideoPath.Contains(
                        id.ToString(CultureInfo.InvariantCulture)+"_",
                        StringComparison.OrdinalIgnoreCase)))continue;
                if(!candidate.TryGetProperty("scores",out var scores)||
                    scores.ValueKind!=JsonValueKind.Object)continue;
                string[] keys=["power","speed","technique","intelligence","durability","impact"];
                var values=keys.Select(k=>scores.TryGetProperty(k,out var value)&&
                    value.ValueKind==JsonValueKind.Number?
                    value.GetDouble():double.NaN).ToArray();
                if(values.Any(x=>!double.IsFinite(x)||x<0||x>10))continue;
                var seo=candidate.TryGetProperty("seo",out var info)&&
                    info.ValueKind==JsonValueKind.Object?info:default;
                ready.Add(new AnimeClipperEntry{
                    PlayerId=id,Name=name,Concept=concept,Mode=AnimeFormat,
                    Scores=values.Select(x=>Math.Round(x,1)).ToArray(),
                    SeoTitle=AnimeJson(seo,"title"),SeoCaption=AnimeJson(seo,"caption"),
                    SeoHashtags=AnimeJson(seo,"hashtags")
                });
                if(ready.Count>=AnimeCount)break;
            }
            if(ready.Count==0)throw new InvalidDataException("No valid ANIME player ideas received.");
            AnimeClipperIdeas.Clear();
            foreach(var item in ready)AnimeClipperIdeas.Add(item);
            AnimeSave();
            AnimeStatus=ready.Count+" individual ANIME player ideas ready. "+
                "Ratings are subjective; actual ANIME statistics are not invented.";
        }
        catch(OperationCanceledException){AnimeSave();throw;}
        catch(Exception ex){AnimeStatus="ANIME idea generation failed: "+ex.Message;Log(AnimeStatus);}
    }

    private static string? AnimeCachedSource(string folder,AnimeClipperEntry player)
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

    private async Task<string> AnimeDownloadAsync(AnimeClipperEntry player)
    {
        Directory.CreateDirectory(AnimeSourceFolder);
        var target=Path.Combine(AnimeSourceFolder,player.PlayerId+".mp4");
        var downloader=MovieDownloaderPath();
        var search=player.Name+" anime character epic battles best moments edit";
        player.Status="Finding player-specific YouTube highlights";
        var args=new List<string>{"--no-playlist","--flat-playlist","--dump-single-json",
            "--skip-download","--no-warnings","ytsearch12:"+search};
        if(AnimeBrowserLogin)args.InsertRange(0,["--cookies-from-browser","edge"]);
        var result=await MovieRunProcessAsync(downloader,args,_cts.Token);
        if(result.Code!=0)throw new InvalidOperationException("YouTube highlight search: "+
            MovieDownloaderError(result.Error));
        using var json=JsonDocument.Parse(result.Output);
        if(!json.RootElement.TryGetProperty("entries",out var entries)||
            entries.ValueKind!=JsonValueKind.Array)
            throw new InvalidDataException("No individual anime character animation scenes found on YouTube.");
        var names=player.Name.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        var sources=new List<(string Url,int Priority)>();
        foreach(var e in entries.EnumerateArray())
        {
            if(e.ValueKind!=JsonValueKind.Object)continue;
            var title=AnimeJson(e,"title");
            var videoId=AnimeJson(e,"id");
            var lower=title.ToLowerInvariant();
            if(videoId.Length!=11||
                !videoId.All(c=>char.IsAsciiLetterOrDigit(c)||c=='-'||c=='_')||
                !names.All(n=>title.Contains(n,StringComparison.OrdinalIgnoreCase))||
                !new[]{"highlight","best moments","epic","battle","fight","scenes","compilation"}
                    .Any(lower.Contains)||
                new[]{"interview","reaction","podcast","breakdown","2k","gameplay","simulation","fan made","fanmade"}
                    .Any(lower.Contains))continue;
            sources.Add(("https://www.youtube.com/watch?v="+videoId,
                (lower.Contains("nba")?10:0)+(lower.Contains("official")?5:0)));
        }
        if(sources.Count==0)throw new InvalidOperationException(
            "No anime character highlight source found. Import an authorized local clip.");
        var pattern=Path.Combine(AnimeSourceFolder,player.PlayerId+".%(ext)s");
        var failures=new List<string>();
        foreach(var url in sources.OrderByDescending(x=>x.Priority)
            .Select(x=>x.Url).Distinct().Take(4))
        {
            _cts.Token.ThrowIfCancellationRequested();
            var download=new List<string>{"--no-playlist","--no-progress",
                "--retries","2","-f","bv*+ba/b","-S","res:1080,ext:mp4",
                "--merge-output-format","mp4","--recode-video","mp4",
                "-o",pattern,url};
            if(AnimeBrowserLogin)download.InsertRange(0,["--cookies-from-browser","edge"]);
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

    private async Task<PublishQueueItem> AnimeRenderAsync(AnimeClipperEntry player,string sourcePath)
    {
        var music=AnimeSoundFile;
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
        var work=Path.Combine(AnimeRoot,player.PlayerId+"-"+
            DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(AnimeExportFolder);
        var cover=Path.Combine(work,"thumbnail.jpg");
        var video=Path.Combine(AnimeExportFolder,player.PlayerId+"_"+
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
        // Unlike MovieClipper, ANIMEClipper must never crop basketball
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
        var ratingsMode=player.Mode=="Character Ratings";
        var labels=ratingsMode
            ?new[]{"POWER","SPEED","TECHNIQUE","INTELLIGENCE","DURABILITY","IMPACT","OVERALL"}
            :player.Mode=="Epic Battles"
                ?new[]{"SCENE 01","SCENE 02","SCENE 03","SCENE 04","SCENE 05","SCENE 06","EPIC BATTLES"}
                :new[]{"THE CHARACTER","ICONIC MOMENTS","SIGNATURE POWER","THE STORY","THE JOURNEY","THE LEGACY","ANIME LEGACY"};
        var values=player.Scores.Concat([Math.Round(player.Scores.Average(),1)]).ToArray();
        var intro=ratingsMode?"ANIME RATINGS":
            player.Mode=="Epic Battles"?"EPIC BATTLES":"CHARACTER LEGACY";
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
            ",asetpts=PTS-STARTPTS,volume="+MovieNumber(AnimeMusicLevel)+"[a]");
        args.AddRange(["-filter_complex",string.Join(";",filters),
            "-map","[v]","-map","[a]","-t",MovieNumber(seconds),
            "-frames:v",frames.ToString(CultureInfo.InvariantCulture),
            "-c:v","libx264","-preset","veryfast","-crf","21",
            "-c:a","aac","-b:a","160k","-pix_fmt","yuv420p",
            "-movflags","+faststart",video]);
        player.Status="Rendering "+count+" beat-synced individual anime character animation scenes";
        var result=await MovieRunProcessAsync(ffmpeg,args,_cts.Token,work);
        if(result.Code!=0)
            throw new InvalidOperationException("FFmpeg: "+
                result.Error[^Math.Min(result.Error.Length,900)..]);
        if(!File.Exists(video)||new FileInfo(video).Length<10000)
            throw new IOException("ANIMEClipper video file was not generated.");
        player.OutputPath=video;
        player.ThumbnailPath=cover;
        AnimeSave();
        return new PublishQueueItem{
            VideoPath=video,ThumbnailPath=cover,
            Title=player.Name+" — "+player.Concept,Description=player.Concept,
            TikTokCaption=player.Name+" ANIME player ratings #ANIME #Basketball",
            YouTubeTitle=player.Name+" — "+player.Concept+" #Shorts",
            YouTubeDescription=player.Concept
        };
    }

    private static void AnimeEnsureSeo(AnimeClipperEntry player)
    {
        if(player.SeoTitle.Length is < 15 or > 120 ||
            !player.SeoTitle.Contains(player.Name,StringComparison.OrdinalIgnoreCase))
            player.SeoTitle=player.Name+" — "+player.Concept+" | ANIME Player Review";
        if(player.SeoCaption.Length is < 25 or > 400)
            player.SeoCaption=player.Name+" ANIME player spotlight: "+player.Concept+
                ". Shooting, finishing, handles, playmaking, defense and IQ scored out of 10.";
        var tags=Regex.Matches(player.SeoHashtags,
            @"(?<!\S)#[A-Za-z][A-Za-z0-9_]{1,24}(?=\s|$)")
            .Select(x=>x.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        if(tags.Count<3)tags=["#ANIME","#Basketball","#PlayerRatings"];
        player.SeoHashtags=string.Join(" ",tags);
    }

    private async Task AnimeStartAsync()
    {
        if(_animeProcessing)return;
        _animeProcessing=true;
        AnimePrepareChat();
        try
        {
            var ideasWereEmpty=AnimeClipperIdeas.Count==0;
            if(ideasWereEmpty)await AnimeGenerateAsync();
            var selected=AnimeClipperIdeas.Where(x=>x.IsChecked&&
                (!_animeRetryFailed||ideasWereEmpty||x.Status.StartsWith("Failed",
                    StringComparison.OrdinalIgnoreCase))).ToList();
            if(selected.Count==0){
                AnimeStatus="No pending anime character ideas selected.";return;
            }
            if(!DateTime.TryParseExact(AnimeFirstSlot,"yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture,DateTimeStyles.AssumeLocal,out var first)||
                first<=DateTime.Now.AddMinutes(25)||first.Minute%5!=0)
                throw new InvalidDataException("First posting slot must be 25+ minutes away, on 5-minute increments.");
            if(!File.Exists(AnimeSoundFile))
                throw new FileNotFoundException("Select a background soundtrack first.",AnimeSoundFile);
            var originallyChecked=PublishingQueue.Where(x=>x.IsChecked).ToList();
            var completed=new List<PublishQueueItem>();
            foreach(var player in selected)
            {
                _cts.Token.ThrowIfCancellationRequested();
                try
                {
                    if(_animeHistory.Any(h=>h.PlayerId==player.PlayerId)||
                        (!string.IsNullOrWhiteSpace(player.OutputPath)&&
                         PublishingQueue.Any(q=>q.VideoPath.Equals(player.OutputPath,
                             StringComparison.OrdinalIgnoreCase))))
                    {
                        player.Status="Already queued (no duplicate)";
                        player.IsChecked=false;AnimeSave();continue;
                    }
                    var source=File.Exists(player.SourcePath)?player.SourcePath:
                        AnimeCachedSource(AnimeSourceFolder,player);
                    if(source is null&&AnimeAutoDownload)
                        source=await AnimeDownloadAsync(player);
                    if(source is null)
                    {
                        player.Status="Failed: player highlight source unavailable";
                        AnimeSave();continue;
                    }
                    player.SourcePath=source;player.Status="HighlightsReady";AnimeSave();
                    var output=File.Exists(player.OutputPath)?
                        new PublishQueueItem{VideoPath=player.OutputPath,
                            ThumbnailPath=player.ThumbnailPath}:
                        await AnimeRenderAsync(player,source);
                    AnimeEnsureSeo(player);
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
                    AnimeSave();SavePublishingQueue();
                    var history=new AnimeClipperHistoryItem{
                        PlayerId=player.PlayerId,Name=player.Name,Concept=player.Concept,
                        OutputPath=output.VideoPath};
                    _animeHistory.Add(history);
                    AnimeHistory.Insert(0,history);
                    AnimeSave();
                }
                catch(OperationCanceledException){player.Status="Stopped";AnimeSave();throw;}
                catch(Exception ex){
                    player.Status="Failed: "+ex.Message;Log("ANIMEClipper "+player.Name+": "+ex.Message);
                    AnimeSave();
                }
            }
            Raise(nameof(AllPublishItemsChecked));
            RefreshPublishingCalendar();
            RefreshVideoLibrary();
            AnimeStatus="Exported "+completed.Count+"/"+selected.Count+
                " individual ANIME player videos to "+AnimeExportFolder+".";
            if(completed.Count==0||!AnimePublish)return;
            if(!PublishConsent){
                AnimeStatus+=" Enable existing publishing authorization to auto-post.";
                return;
            }
            foreach(var old in originallyChecked)old.IsChecked=false;
            try{await RunTikTokBatchAsync();}
            finally{foreach(var old in originallyChecked)old.IsChecked=true;}
        }
        catch(OperationCanceledException){AnimeStatus="Stopped. Resume saved ANIME workflow.";AnimeSave();throw;}
        catch(Exception ex){AnimeStatus="ANIMEClipper: "+ex.Message;Log(AnimeStatus);}
        finally{_animeProcessing=false;AnimeSave();}
    }
}
