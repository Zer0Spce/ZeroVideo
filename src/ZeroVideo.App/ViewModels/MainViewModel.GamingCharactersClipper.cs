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

public sealed class GamingCharactersClipperEntry : ObservableObject
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

public sealed class GamingCharactersClipperHistoryItem
{
    public int PlayerId { get; set; }
    public string Name { get; set; } = "";
    public string Concept { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class GamingCharactersClipperState
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
    public List<GamingCharactersClipperEntry> Ideas { get; set; } = [];
    public List<GamingCharactersClipperHistoryItem> History { get; set; } = [];
}

public sealed partial class MainViewModel
{
    private static readonly string GamingCharactersRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZeroVideo", "GAMING CHARACTERSClipper");
    private static readonly string GamingCharactersStatePath = Path.Combine(GamingCharactersRoot, "state.json");
    private string _gamingcharactersNiche = "Iconic named video-game characters and protagonists";
    private string _gamingcharactersFormat = "Character Ratings";
    private int _gamingcharactersCount = 10;
    private string _gamingcharactersSourceFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "ZeroVideo", "GAMING CHARACTERSHighlights");
    private string _gamingcharactersSoundFile = "";
    private string _gamingcharactersStatus = "Ready: ChatGPT player ideas and scores → individual video game character gameplay moments → beat-synced edit → TikTok queue.";
    private string _gamingcharactersFirstSlot = DateTime.Now.AddHours(3).ToString("yyyy-MM-dd HH:00",CultureInfo.InvariantCulture);
    private bool _gamingcharactersPublish = true;
    private bool _gamingcharactersAutoDownload = true;
    private bool _gamingcharactersBrowserLogin;
    private bool _gamingcharactersNewChat;
    private bool _gamingcharactersProcessing;
    private bool _gamingcharactersRetryFailed;
    private double _gamingcharactersMusicLevel = 0.85;
    private VideoProject _gamingcharactersChat = new() { Title = "GAMING CHARACTERSClipper batch", Topic = "Individual video game character videos" };
    private readonly List<GamingCharactersClipperHistoryItem> _gamingcharactersHistory = [];
    public ObservableCollection<GamingCharactersClipperEntry> GamingCharactersClipperIdeas { get; } = [];
    public ObservableCollection<GamingCharactersClipperHistoryItem> GamingCharactersHistory { get; } = [];
    public ObservableCollection<string> GamingCharactersFormats { get; } = ["Character Ratings", "Best Moments", "Character Legacy"];
    public string GamingCharactersNiche { get => _gamingcharactersNiche; set => Set(ref _gamingcharactersNiche,value); }
    public string GamingCharactersFormat { get => _gamingcharactersFormat; set => Set(ref _gamingcharactersFormat,value); }
    public int GamingCharactersCount { get => _gamingcharactersCount; set => Set(ref _gamingcharactersCount,Math.Clamp(value,1,20)); }
    public string GamingCharactersSourceFolder { get => _gamingcharactersSourceFolder; set => Set(ref _gamingcharactersSourceFolder,value); }
    public string GamingCharactersSoundFile { get => _gamingcharactersSoundFile; set => Set(ref _gamingcharactersSoundFile,value); }
    public string GamingCharactersStatus { get => _gamingcharactersStatus; set => Set(ref _gamingcharactersStatus,value); }
    public string GamingCharactersFirstSlot { get => _gamingcharactersFirstSlot; set => Set(ref _gamingcharactersFirstSlot,value); }
    public bool GamingCharactersPublish { get => _gamingcharactersPublish; set => Set(ref _gamingcharactersPublish,value); }
    public bool GamingCharactersAutoDownload { get => _gamingcharactersAutoDownload; set => Set(ref _gamingcharactersAutoDownload,value); }
    public bool GamingCharactersBrowserLogin { get => _gamingcharactersBrowserLogin; set => Set(ref _gamingcharactersBrowserLogin,value); }
    public bool GamingCharactersNewChat { get => _gamingcharactersNewChat; set => Set(ref _gamingcharactersNewChat,value); }
    public double GamingCharactersMusicLevel { get => _gamingcharactersMusicLevel; set => Set(ref _gamingcharactersMusicLevel,Math.Clamp(value,0,1)); }
    public string GamingCharactersHistorySummary => GamingCharactersHistory.Count+" finished player concepts in GAMING CHARACTERS history";
    public string GamingCharactersCheckpointStatus => GamingCharactersClipperIdeas.Count+" saved player ideas; "+GamingCharactersStatePath;
    public string GamingCharactersExportFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "ZeroVideo","GAMING CHARACTERSClipper");
    public ICommand GamingCharactersGenerateCommand { get; private set; } = null!;
    public ICommand GamingCharactersStartCommand { get; private set; } = null!;
    public ICommand GamingCharactersResumeCommand { get; private set; } = null!;
    public ICommand GamingCharactersRetryFailedCommand { get; private set; } = null!;
    public ICommand GamingCharactersBrowseSourceCommand { get; private set; } = null!;
    public ICommand GamingCharactersBrowseSoundCommand { get; private set; } = null!;
    public ICommand GamingCharactersFindHighlightsCommand { get; private set; } = null!;
    public ICommand GamingCharactersOpenOutputCommand { get; private set; } = null!;
    public ICommand GamingCharactersForceStopCommand => ForceStopJobCommand;

    private void InitializeGamingCharactersClipperCommands()
    {
        GamingCharactersRestore();
        GamingCharactersGenerateCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "GAMING CHARACTERSClipper: individual player ideas and SEO",GamingCharactersGenerateAsync));
        GamingCharactersStartCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "GAMING CHARACTERSClipper: one-click individual video game character gameplay moments",GamingCharactersStartAsync,publishing:GamingCharactersPublish));
        GamingCharactersResumeCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "GAMING CHARACTERSClipper: resume",async()=>{
                GamingCharactersRestore();
                _gamingcharactersRetryFailed=false;
                await GamingCharactersStartAsync();
            },publishing:GamingCharactersPublish));
        GamingCharactersRetryFailedCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "GAMING CHARACTERSClipper: retry failures",async()=>{
                GamingCharactersRestore();
                _gamingcharactersRetryFailed=true;
                try{await GamingCharactersStartAsync();}
                finally{_gamingcharactersRetryFailed=false;}
            },publishing:GamingCharactersPublish));
        GamingCharactersBrowseSourceCommand=new RelayCommand(()=>{
            var picker=new OpenFolderDialog{Title="GAMING CHARACTERS player highlight source/cache folder"};
            if(picker.ShowDialog()==true){GamingCharactersSourceFolder=picker.FolderName;GamingCharactersSave();}
        });
        GamingCharactersBrowseSoundCommand=new RelayCommand(()=>{
            var picker=new OpenFileDialog{
                Filter="Audio files|*.mp3;*.m4a;*.wav;*.aac;*.flac",
                CheckFileExists=true};
            if(picker.ShowDialog()==true){GamingCharactersSoundFile=picker.FileName;GamingCharactersSave();}
        });
        GamingCharactersFindHighlightsCommand=new RelayCommand(()=>{
            var player=GamingCharactersClipperIdeas.FirstOrDefault(x=>x.IsChecked);
            if(player is null){GamingCharactersStatus="Select an GAMING CHARACTERS player first.";return;}
            var query=player.Name+" GAMING CHARACTERS individual highlights";
            Process.Start(new ProcessStartInfo(
                "https://www.youtube.com/results?search_query="+Uri.EscapeDataString(query))
                {UseShellExecute=true});
            GamingCharactersStatus="Opened player-specific highlights search. Use footage you are authorized to edit and post.";
        });
        GamingCharactersOpenOutputCommand=new RelayCommand(()=>{
            Directory.CreateDirectory(GamingCharactersExportFolder);
            Process.Start(new ProcessStartInfo("explorer.exe","\""+GamingCharactersExportFolder+"\"")
                {UseShellExecute=true});
        });
    }

    private void GamingCharactersSave()
    {
        Directory.CreateDirectory(GamingCharactersRoot);
        var snapshot=new GamingCharactersClipperState{
            Niche=GamingCharactersNiche,Format=GamingCharactersFormat,
            ChatUrl=_gamingcharactersChat.ChatGptConversationUrl??"",
            SourceFolder=GamingCharactersSourceFolder,SoundFile=GamingCharactersSoundFile,
            FirstSlot=GamingCharactersFirstSlot,AutoDownload=GamingCharactersAutoDownload,
            BrowserLogin=GamingCharactersBrowserLogin,Publish=GamingCharactersPublish,
            MusicLevel=GamingCharactersMusicLevel,
            Ideas=GamingCharactersClipperIdeas.ToList(),
            History=_gamingcharactersHistory.ToList()
        };
        var path=GamingCharactersStatePath+".tmp";
        File.WriteAllText(path,JsonSerializer.Serialize(snapshot,new JsonSerializerOptions{
            WriteIndented=true}));
        File.Move(path,GamingCharactersStatePath,true);
        Raise(nameof(GamingCharactersCheckpointStatus));
        Raise(nameof(GamingCharactersHistorySummary));
    }

    private void GamingCharactersRestore()
    {
        try
        {
            if(!File.Exists(GamingCharactersStatePath))return;
            var state=JsonSerializer.Deserialize<GamingCharactersClipperState>(
                File.ReadAllText(GamingCharactersStatePath));
            if(state is null)return;
            if(state.Niche.Length>0)GamingCharactersNiche=state.Niche;
            if(GamingCharactersFormats.Contains(state.Format))GamingCharactersFormat=state.Format;
            if(!string.IsNullOrWhiteSpace(state.SourceFolder))
                GamingCharactersSourceFolder=state.SourceFolder;
            if(!string.IsNullOrWhiteSpace(state.SoundFile))
                GamingCharactersSoundFile=state.SoundFile;
            // Keep the future default slot instead of restoring a time in
            // the past from a previous session.
            if(DateTime.TryParseExact(state.FirstSlot,"yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture,DateTimeStyles.AssumeLocal,
                out var restoredSlot)&&restoredSlot>DateTime.Now.AddMinutes(25))
                GamingCharactersFirstSlot=state.FirstSlot;
            GamingCharactersAutoDownload=state.AutoDownload;
            GamingCharactersBrowserLogin=state.BrowserLogin;
            GamingCharactersPublish=state.Publish;
            GamingCharactersMusicLevel=state.MusicLevel;
            _gamingcharactersChat=new VideoProject{
                Title="GAMING CHARACTERSClipper batch",Topic=GamingCharactersNiche,
                ChatGptConversationUrl=string.IsNullOrWhiteSpace(state.ChatUrl)?null:state.ChatUrl};
            GamingCharactersClipperIdeas.Clear();
            foreach(var item in state.Ideas)GamingCharactersClipperIdeas.Add(item);
            _gamingcharactersHistory.Clear();
            _gamingcharactersHistory.AddRange(state.History);
            GamingCharactersHistory.Clear();
            foreach(var item in _gamingcharactersHistory.OrderByDescending(x=>x.CompletedAt))
                GamingCharactersHistory.Add(item);
            Raise(nameof(GamingCharactersHistorySummary));
            Raise(nameof(GamingCharactersCheckpointStatus));
        }
        catch(Exception ex){GamingCharactersStatus="GAMING CHARACTERS checkpoint load failed: "+ex.Message;}
    }

    private void GamingCharactersPrepareChat()
    {
        if(GamingCharactersNewChat)
        {
            _gamingcharactersChat=new VideoProject{Title="GAMING CHARACTERSClipper batch: "+GamingCharactersNiche,Topic=GamingCharactersNiche};
            _chat.RequestNewChat();
            GamingCharactersNewChat=false;
            GamingCharactersSave();
        }
    }

    private static string GamingCharactersJson(JsonElement node,string key)=>
        node.ValueKind==JsonValueKind.Object &&
        node.TryGetProperty(key,out var field)&&field.ValueKind==JsonValueKind.String?
        field.GetString()??"":"";

    private static int GamingCharactersId(string name,string concept)
    {
        unchecked{
            uint h=2166136261;
            foreach(var c in ("GamingCharacters|"+name+"|"+concept).ToUpperInvariant()){
                h^=c;h*=16777619;
            }
            return (int)(h&0x7fffffff);
        }
    }

    private async Task GamingCharactersGenerateAsync()
    {
        try
        {
            GamingCharactersStatus="ChatGPT: generating individual GAMING CHARACTERS player concepts, six ratings and SEO in ONE request";
            GamingCharactersPrepareChat();
            var completed=_gamingcharactersHistory.Select(x=>x.Name+" — "+x.Concept)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(200);
            var prompt="Generate exactly "+GamingCharactersCount+" REAL individual video game character ideas, one subject per video, no teams, duos or comparisons. "+
                "Niche: "+GamingCharactersNiche+". Video format: "+GamingCharactersFormat+". "+
                "Avoid completed name/concept pairs: "+string.Join("; ",completed)+". "+
                "For anime and gaming characters identify the canonical series or game in concept; for real people avoid invented achievements. "+
                "In the SAME response generate six SUBJECTIVE scores 0.0-10.0 for: power, skill, speed, strategy, durability, impact. "+
                "Those are editorial ratings, never official statistics or sourced review scores. "+
                "Write one accurate SEO title (40-85 chars), short caption (100-240 chars) and 3-5 hashtags. "+
                "Use only genuine known subjects; do not invent people, titles, games, broadcasts, stats or achievements. "+
                "Return ONLY a valid JSON object inside fenced json: "+
                "{\"response_type\":\"gamingcharactersclipper_ideas_v1\",\"players\":[{\"name\":\"Lara Croft\",\"concept\":\"Iconic individual highlight moments\",\"scores\":{\"power\":8,\"skill\":8,\"speed\":8,\"strategy\":8,\"durability\":8,\"impact\":8},\"seo\":{\"title\":\"Individual character or person highlights and ratings\",\"caption\":\"A short-form individual spotlight with subjective category scores.\",\"hashtags\":\"#Highlights #Clips #Edits\"}}]}";
            var raw=await _chat.GenerateTextAsync(_gamingcharactersChat,Settings,prompt,Log,_cts.Token);
            var first=raw.IndexOf('{');var last=raw.LastIndexOf('}');
            if(first<0||last<=first)throw new InvalidDataException("ChatGPT did not return JSON.");
            using var json=JsonDocument.Parse(raw.Substring(first,last-first+1));
            var players=json.RootElement.GetProperty("players");
            if(players.ValueKind!=JsonValueKind.Array)
                throw new InvalidDataException("Expected players JSON array.");
            var ready=new List<GamingCharactersClipperEntry>();
            var seen=new HashSet<int>();
            foreach(var candidate in players.EnumerateArray().Take(GamingCharactersCount*3))
            {
                if(candidate.ValueKind!=JsonValueKind.Object)continue;
                var name=GamingCharactersJson(candidate,"name").Trim();
                var concept=GamingCharactersJson(candidate,"concept").Trim();
                if(name.Length is < 4 or > 100||concept.Length is < 4 or > 130||
                    name.Any(char.IsControl))continue;
                var id=GamingCharactersId(name,concept);
                if(!seen.Add(id)||_gamingcharactersHistory.Any(x=>x.PlayerId==id)||
                    PublishingQueue.Any(x=>x.VideoPath.Contains(
                        id.ToString(CultureInfo.InvariantCulture)+"_",
                        StringComparison.OrdinalIgnoreCase)))continue;
                if(!candidate.TryGetProperty("scores",out var scores)||
                    scores.ValueKind!=JsonValueKind.Object)continue;
                string[] keys=["power","skill","speed","strategy","durability","impact"];
                var values=keys.Select(k=>scores.TryGetProperty(k,out var value)&&
                    value.ValueKind==JsonValueKind.Number?
                    value.GetDouble():double.NaN).ToArray();
                if(values.Any(x=>!double.IsFinite(x)||x<0||x>10))continue;
                var seo=candidate.TryGetProperty("seo",out var info)&&
                    info.ValueKind==JsonValueKind.Object?info:default;
                ready.Add(new GamingCharactersClipperEntry{
                    PlayerId=id,Name=name,Concept=concept,Mode=GamingCharactersFormat,
                    Scores=values.Select(x=>Math.Round(x,1)).ToArray(),
                    SeoTitle=GamingCharactersJson(seo,"title"),SeoCaption=GamingCharactersJson(seo,"caption"),
                    SeoHashtags=GamingCharactersJson(seo,"hashtags")
                });
                if(ready.Count>=GamingCharactersCount)break;
            }
            if(ready.Count==0)throw new InvalidDataException("No valid GAMING CHARACTERS player ideas received.");
            GamingCharactersClipperIdeas.Clear();
            foreach(var item in ready)GamingCharactersClipperIdeas.Add(item);
            GamingCharactersSave();
            GamingCharactersStatus=ready.Count+" individual GAMING CHARACTERS player ideas ready. "+
                "Ratings are subjective; actual GAMING CHARACTERS statistics are not invented.";
        }
        catch(OperationCanceledException){GamingCharactersSave();throw;}
        catch(Exception ex){GamingCharactersStatus="GAMING CHARACTERS idea generation failed: "+ex.Message;Log(GamingCharactersStatus);}
    }

    private static string? GamingCharactersCachedSource(string folder,GamingCharactersClipperEntry player)
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

    private async Task<string> GamingCharactersDownloadAsync(GamingCharactersClipperEntry player)
    {
        Directory.CreateDirectory(GamingCharactersSourceFolder);
        var target=Path.Combine(GamingCharactersSourceFolder,player.PlayerId+".mp4");
        var downloader=MovieDownloaderPath();
        var search=player.Name+" video game character cinematic best moments gameplay";
        player.Status="Finding player-specific YouTube highlights";
        var args=new List<string>{"--no-playlist","--flat-playlist","--dump-single-json",
            "--skip-download","--no-warnings","ytsearch12:"+search};
        if(GamingCharactersBrowserLogin)args.InsertRange(0,["--cookies-from-browser","edge"]);
        var result=await MovieRunProcessAsync(downloader,args,_cts.Token);
        if(result.Code!=0)throw new InvalidOperationException("YouTube highlight search: "+
            MovieDownloaderError(result.Error));
        using var json=JsonDocument.Parse(result.Output);
        if(!json.RootElement.TryGetProperty("entries",out var entries)||
            entries.ValueKind!=JsonValueKind.Array)
            throw new InvalidDataException("No individual video game character gameplay moments found on YouTube.");
        var names=player.Name.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        var sources=new List<(string Url,int Priority)>();
        foreach(var e in entries.EnumerateArray())
        {
            if(e.ValueKind!=JsonValueKind.Object)continue;
            var title=GamingCharactersJson(e,"title");
            var videoId=GamingCharactersJson(e,"id");
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
            "No video game character highlight source found. Import an authorized local clip.");
        var pattern=Path.Combine(GamingCharactersSourceFolder,player.PlayerId+".%(ext)s");
        var failures=new List<string>();
        foreach(var url in sources.OrderByDescending(x=>x.Priority)
            .Select(x=>x.Url).Distinct().Take(4))
        {
            _cts.Token.ThrowIfCancellationRequested();
            var download=new List<string>{"--no-playlist","--no-progress",
                "--retries","2","-f","bv*+ba/b","-S","res:1080,ext:mp4",
                "--merge-output-format","mp4","--recode-video","mp4",
                "-o",pattern,url};
            if(GamingCharactersBrowserLogin)download.InsertRange(0,["--cookies-from-browser","edge"]);
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

    private async Task<PublishQueueItem> GamingCharactersRenderAsync(GamingCharactersClipperEntry player,string sourcePath)
    {
        var music=GamingCharactersSoundFile;
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
        var work=Path.Combine(GamingCharactersRoot,player.PlayerId+"-"+
            DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(GamingCharactersExportFolder);
        var cover=Path.Combine(work,"thumbnail.jpg");
        var video=Path.Combine(GamingCharactersExportFolder,player.PlayerId+"_"+
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
        // Unlike MovieClipper, GAMING CHARACTERSClipper must never crop basketball
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
            ?new[]{"POWER","SKILL","SPEED","STRATEGY","DURABILITY","IMPACT","OVERALL"}
            :player.Mode=="Best Moments"
                ?new[]{"SCENE 01","SCENE 02","SCENE 03","SCENE 04","SCENE 05","SCENE 06","BEST MOMENTS"}
                :new[]{"THE CHARACTER","ICONIC SCENES","SIGNATURE SKILLS","THE GAME","THE STORY","THE LEGEND","GAME LEGACY"};
        var values=player.Scores.Concat([Math.Round(player.Scores.Average(),1)]).ToArray();
        var intro=ratingsMode?"GAME CHARACTER RATINGS":
            player.Mode=="Best Moments"?"BEST MOMENTS":"CHARACTER LEGACY";
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
            ",asetpts=PTS-STARTPTS,volume="+MovieNumber(GamingCharactersMusicLevel)+"[a]");
        args.AddRange(["-filter_complex",string.Join(";",filters),
            "-map","[v]","-map","[a]","-t",MovieNumber(seconds),
            "-frames:v",frames.ToString(CultureInfo.InvariantCulture),
            "-c:v","libx264","-preset","veryfast","-crf","21",
            "-c:a","aac","-b:a","160k","-pix_fmt","yuv420p",
            "-movflags","+faststart",video]);
        player.Status="Rendering "+count+" beat-synced individual video game character gameplay moments";
        var result=await MovieRunProcessAsync(ffmpeg,args,_cts.Token,work);
        if(result.Code!=0)
            throw new InvalidOperationException("FFmpeg: "+
                result.Error[^Math.Min(result.Error.Length,900)..]);
        if(!File.Exists(video)||new FileInfo(video).Length<10000)
            throw new IOException("GAMING CHARACTERSClipper video file was not generated.");
        player.OutputPath=video;
        player.ThumbnailPath=cover;
        GamingCharactersSave();
        return new PublishQueueItem{
            VideoPath=video,ThumbnailPath=cover,
            Title=player.Name+" — "+player.Concept,Description=player.Concept,
            TikTokCaption=player.Name+" GAMING CHARACTERS player ratings #GAMING CHARACTERS #Basketball",
            YouTubeTitle=player.Name+" — "+player.Concept+" #Shorts",
            YouTubeDescription=player.Concept
        };
    }

    private static void GamingCharactersEnsureSeo(GamingCharactersClipperEntry player)
    {
        if(player.SeoTitle.Length is < 15 or > 120 ||
            !player.SeoTitle.Contains(player.Name,StringComparison.OrdinalIgnoreCase))
            player.SeoTitle=player.Name+" — "+player.Concept+" | GAMING CHARACTERS Player Review";
        if(player.SeoCaption.Length is < 25 or > 400)
            player.SeoCaption=player.Name+" GAMING CHARACTERS player spotlight: "+player.Concept+
                ". Shooting, finishing, handles, playmaking, defense and IQ scored out of 10.";
        var tags=Regex.Matches(player.SeoHashtags,
            @"(?<!\S)#[A-Za-z][A-Za-z0-9_]{1,24}(?=\s|$)")
            .Select(x=>x.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        if(tags.Count<3)tags=["#GAMING CHARACTERS","#Basketball","#PlayerRatings"];
        player.SeoHashtags=string.Join(" ",tags);
    }

    private async Task GamingCharactersStartAsync()
    {
        if(_gamingcharactersProcessing)return;
        _gamingcharactersProcessing=true;
        GamingCharactersPrepareChat();
        try
        {
            var ideasWereEmpty=GamingCharactersClipperIdeas.Count==0;
            if(ideasWereEmpty)await GamingCharactersGenerateAsync();
            var selected=GamingCharactersClipperIdeas.Where(x=>x.IsChecked&&
                (!_gamingcharactersRetryFailed||ideasWereEmpty||x.Status.StartsWith("Failed",
                    StringComparison.OrdinalIgnoreCase))).ToList();
            if(selected.Count==0){
                GamingCharactersStatus="No pending video game character ideas selected.";return;
            }
            if(!DateTime.TryParseExact(GamingCharactersFirstSlot,"yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture,DateTimeStyles.AssumeLocal,out var first)||
                first<=DateTime.Now.AddMinutes(25)||first.Minute%5!=0)
                throw new InvalidDataException("First posting slot must be 25+ minutes away, on 5-minute increments.");
            if(!File.Exists(GamingCharactersSoundFile))
                throw new FileNotFoundException("Select a background soundtrack first.",GamingCharactersSoundFile);
            var originallyChecked=PublishingQueue.Where(x=>x.IsChecked).ToList();
            var completed=new List<PublishQueueItem>();
            foreach(var player in selected)
            {
                _cts.Token.ThrowIfCancellationRequested();
                try
                {
                    if(_gamingcharactersHistory.Any(h=>h.PlayerId==player.PlayerId)||
                        (!string.IsNullOrWhiteSpace(player.OutputPath)&&
                         PublishingQueue.Any(q=>q.VideoPath.Equals(player.OutputPath,
                             StringComparison.OrdinalIgnoreCase))))
                    {
                        player.Status="Already queued (no duplicate)";
                        player.IsChecked=false;GamingCharactersSave();continue;
                    }
                    var source=File.Exists(player.SourcePath)?player.SourcePath:
                        GamingCharactersCachedSource(GamingCharactersSourceFolder,player);
                    if(source is null&&GamingCharactersAutoDownload)
                        source=await GamingCharactersDownloadAsync(player);
                    if(source is null)
                    {
                        player.Status="Failed: player highlight source unavailable";
                        GamingCharactersSave();continue;
                    }
                    player.SourcePath=source;player.Status="HighlightsReady";GamingCharactersSave();
                    var output=File.Exists(player.OutputPath)?
                        new PublishQueueItem{VideoPath=player.OutputPath,
                            ThumbnailPath=player.ThumbnailPath}:
                        await GamingCharactersRenderAsync(player,source);
                    GamingCharactersEnsureSeo(player);
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
                    GamingCharactersSave();SavePublishingQueue();
                    var history=new GamingCharactersClipperHistoryItem{
                        PlayerId=player.PlayerId,Name=player.Name,Concept=player.Concept,
                        OutputPath=output.VideoPath};
                    _gamingcharactersHistory.Add(history);
                    GamingCharactersHistory.Insert(0,history);
                    GamingCharactersSave();
                }
                catch(OperationCanceledException){player.Status="Stopped";GamingCharactersSave();throw;}
                catch(Exception ex){
                    player.Status="Failed: "+ex.Message;Log("GAMING CHARACTERSClipper "+player.Name+": "+ex.Message);
                    GamingCharactersSave();
                }
            }
            Raise(nameof(AllPublishItemsChecked));
            RefreshPublishingCalendar();
            RefreshVideoLibrary();
            GamingCharactersStatus="Exported "+completed.Count+"/"+selected.Count+
                " individual GAMING CHARACTERS player videos to "+GamingCharactersExportFolder+".";
            if(completed.Count==0||!GamingCharactersPublish)return;
            if(!PublishConsent){
                GamingCharactersStatus+=" Enable existing publishing authorization to auto-post.";
                return;
            }
            foreach(var old in originallyChecked)old.IsChecked=false;
            try{await RunTikTokBatchAsync();}
            finally{foreach(var old in originallyChecked)old.IsChecked=true;}
        }
        catch(OperationCanceledException){GamingCharactersStatus="Stopped. Resume saved GAMING CHARACTERS workflow.";GamingCharactersSave();throw;}
        catch(Exception ex){GamingCharactersStatus="GAMING CHARACTERSClipper: "+ex.Message;Log(GamingCharactersStatus);}
        finally{_gamingcharactersProcessing=false;GamingCharactersSave();}
    }
}
