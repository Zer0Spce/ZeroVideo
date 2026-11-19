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

public sealed class F1ClipperEntry : ObservableObject
{
    public int PlayerId { get; set; }
    public string Name { get; set; } = "";
    public string Concept { get; set; } = "";
    public string Mode { get; set; } = "Driver Ratings";
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

public sealed class F1ClipperHistoryItem
{
    public int PlayerId { get; set; }
    public string Name { get; set; } = "";
    public string Concept { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public DateTimeOffset CompletedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class F1ClipperState
{
    public string Niche { get; set; } = "";
    public string Format { get; set; } = "Driver Ratings";
    public string ChatUrl { get; set; } = "";
    public string SourceFolder { get; set; } = "";
    public string SoundFile { get; set; } = "";
    public string FirstSlot { get; set; } = "";
    public bool AutoDownload { get; set; } = true;
    public bool BrowserLogin { get; set; }
    public bool Publish { get; set; } = true;
    public double MusicLevel { get; set; } = .85;
    public List<F1ClipperEntry> Ideas { get; set; } = [];
    public List<F1ClipperHistoryItem> History { get; set; } = [];
}

public sealed partial class MainViewModel
{
    private static readonly string F1Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZeroVideo", "F1Clipper");
    private static readonly string F1StatePath = Path.Combine(F1Root, "state.json");
    private string _f1Niche = "Formula One individual drivers and legends";
    private string _f1Format = "Driver Ratings";
    private int _f1Count = 10;
    private string _f1SourceFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "ZeroVideo", "F1Highlights");
    private string _f1SoundFile = "";
    private string _f1Status = "Ready: ChatGPT player ideas and scores → single F1 driver racing highlight clips → beat-synced edit → TikTok queue.";
    private string _f1FirstSlot = DateTime.Now.AddHours(3).ToString("yyyy-MM-dd HH:00",CultureInfo.InvariantCulture);
    private bool _f1Publish = true;
    private bool _f1AutoDownload = true;
    private bool _f1BrowserLogin;
    private bool _f1NewChat;
    private bool _f1Processing;
    private bool _f1RetryFailed;
    private double _f1MusicLevel = 0.85;
    private VideoProject _f1Chat = new() { Title = "F1Clipper batch", Topic = "Individual Formula 1 racing driver videos" };
    private readonly List<F1ClipperHistoryItem> _f1History = [];
    public ObservableCollection<F1ClipperEntry> F1ClipperIdeas { get; } = [];
    public ObservableCollection<F1ClipperHistoryItem> F1History { get; } = [];
    public ObservableCollection<string> F1Formats { get; } = ["Driver Ratings", "Best Overtakes", "Driver Legacy"];
    public string F1Niche { get => _f1Niche; set => Set(ref _f1Niche,value); }
    public string F1Format { get => _f1Format; set => Set(ref _f1Format,value); }
    public int F1Count { get => _f1Count; set => Set(ref _f1Count,Math.Clamp(value,1,20)); }
    public string F1SourceFolder { get => _f1SourceFolder; set => Set(ref _f1SourceFolder,value); }
    public string F1SoundFile { get => _f1SoundFile; set => Set(ref _f1SoundFile,value); }
    public string F1Status { get => _f1Status; set => Set(ref _f1Status,value); }
    public string F1FirstSlot { get => _f1FirstSlot; set => Set(ref _f1FirstSlot,value); }
    public bool F1Publish { get => _f1Publish; set => Set(ref _f1Publish,value); }
    public bool F1AutoDownload { get => _f1AutoDownload; set => Set(ref _f1AutoDownload,value); }
    public bool F1BrowserLogin { get => _f1BrowserLogin; set => Set(ref _f1BrowserLogin,value); }
    public bool F1NewChat { get => _f1NewChat; set => Set(ref _f1NewChat,value); }
    public double F1MusicLevel { get => _f1MusicLevel; set => Set(ref _f1MusicLevel,Math.Clamp(value,0,1)); }
    public string F1HistorySummary => F1History.Count+" finished player concepts in F1 history";
    public string F1CheckpointStatus => F1ClipperIdeas.Count+" saved player ideas; "+F1StatePath;
    public string F1ExportFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "ZeroVideo","F1Clipper");
    public ICommand F1GenerateCommand { get; private set; } = null!;
    public ICommand F1StartCommand { get; private set; } = null!;
    public ICommand F1ResumeCommand { get; private set; } = null!;
    public ICommand F1RetryFailedCommand { get; private set; } = null!;
    public ICommand F1BrowseSourceCommand { get; private set; } = null!;
    public ICommand F1BrowseSoundCommand { get; private set; } = null!;
    public ICommand F1FindHighlightsCommand { get; private set; } = null!;
    public ICommand F1OpenOutputCommand { get; private set; } = null!;
    public ICommand F1ForceStopCommand => ForceStopJobCommand;

    private void InitializeF1ClipperCommands()
    {
        F1Restore();
        F1GenerateCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "F1Clipper: individual player ideas and SEO",F1GenerateAsync));
        F1StartCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "F1Clipper: one-click single F1 driver racing highlight clips",F1StartAsync,publishing:F1Publish));
        F1ResumeCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "F1Clipper: resume",async()=>{
                F1Restore();
                _f1RetryFailed=false;
                await F1StartAsync();
            },publishing:F1Publish));
        F1RetryFailedCommand=new AsyncCommand(()=>RunManagedJobAsync(
            "F1Clipper: retry failures",async()=>{
                F1Restore();
                _f1RetryFailed=true;
                try{await F1StartAsync();}
                finally{_f1RetryFailed=false;}
            },publishing:F1Publish));
        F1BrowseSourceCommand=new RelayCommand(()=>{
            var picker=new OpenFolderDialog{Title="F1 player highlight source/cache folder"};
            if(picker.ShowDialog()==true){F1SourceFolder=picker.FolderName;F1Save();}
        });
        F1BrowseSoundCommand=new RelayCommand(()=>{
            var picker=new OpenFileDialog{
                Filter="Audio files|*.mp3;*.m4a;*.wav;*.aac;*.flac",
                CheckFileExists=true};
            if(picker.ShowDialog()==true){F1SoundFile=picker.FileName;F1Save();}
        });
        F1FindHighlightsCommand=new RelayCommand(()=>{
            var player=F1ClipperIdeas.FirstOrDefault(x=>x.IsChecked);
            if(player is null){F1Status="Select an F1 player first.";return;}
            var query=player.Name+" F1 individual highlights";
            Process.Start(new ProcessStartInfo(
                "https://www.youtube.com/results?search_query="+Uri.EscapeDataString(query))
                {UseShellExecute=true});
            F1Status="Opened player-specific highlights search. Use footage you are authorized to edit and post.";
        });
        F1OpenOutputCommand=new RelayCommand(()=>{
            Directory.CreateDirectory(F1ExportFolder);
            Process.Start(new ProcessStartInfo("explorer.exe","\""+F1ExportFolder+"\"")
                {UseShellExecute=true});
        });
    }

    private void F1Save()
    {
        Directory.CreateDirectory(F1Root);
        var snapshot=new F1ClipperState{
            Niche=F1Niche,Format=F1Format,
            ChatUrl=_f1Chat.ChatGptConversationUrl??"",
            SourceFolder=F1SourceFolder,SoundFile=F1SoundFile,
            FirstSlot=F1FirstSlot,AutoDownload=F1AutoDownload,
            BrowserLogin=F1BrowserLogin,Publish=F1Publish,
            MusicLevel=F1MusicLevel,
            Ideas=F1ClipperIdeas.ToList(),
            History=_f1History.ToList()
        };
        var path=F1StatePath+".tmp";
        File.WriteAllText(path,JsonSerializer.Serialize(snapshot,new JsonSerializerOptions{
            WriteIndented=true}));
        File.Move(path,F1StatePath,true);
        Raise(nameof(F1CheckpointStatus));
        Raise(nameof(F1HistorySummary));
    }

    private void F1Restore()
    {
        try
        {
            if(!File.Exists(F1StatePath))return;
            var state=JsonSerializer.Deserialize<F1ClipperState>(
                File.ReadAllText(F1StatePath));
            if(state is null)return;
            if(state.Niche.Length>0)F1Niche=state.Niche;
            if(F1Formats.Contains(state.Format))F1Format=state.Format;
            if(!string.IsNullOrWhiteSpace(state.SourceFolder))
                F1SourceFolder=state.SourceFolder;
            if(!string.IsNullOrWhiteSpace(state.SoundFile))
                F1SoundFile=state.SoundFile;
            // Keep the future default slot instead of restoring a time in
            // the past from a previous session.
            if(DateTime.TryParseExact(state.FirstSlot,"yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture,DateTimeStyles.AssumeLocal,
                out var restoredSlot)&&restoredSlot>DateTime.Now.AddMinutes(25))
                F1FirstSlot=state.FirstSlot;
            F1AutoDownload=state.AutoDownload;
            F1BrowserLogin=state.BrowserLogin;
            F1Publish=state.Publish;
            F1MusicLevel=state.MusicLevel;
            _f1Chat=new VideoProject{
                Title="F1Clipper batch",Topic=F1Niche,
                ChatGptConversationUrl=string.IsNullOrWhiteSpace(state.ChatUrl)?null:state.ChatUrl};
            F1ClipperIdeas.Clear();
            foreach(var item in state.Ideas)F1ClipperIdeas.Add(item);
            _f1History.Clear();
            _f1History.AddRange(state.History);
            F1History.Clear();
            foreach(var item in _f1History.OrderByDescending(x=>x.CompletedAt))
                F1History.Add(item);
            Raise(nameof(F1HistorySummary));
            Raise(nameof(F1CheckpointStatus));
        }
        catch(Exception ex){F1Status="F1 checkpoint load failed: "+ex.Message;}
    }

    private void F1PrepareChat()
    {
        if(F1NewChat)
        {
            _f1Chat=new VideoProject{Title="F1Clipper batch: "+F1Niche,Topic=F1Niche};
            _chat.RequestNewChat();
            F1NewChat=false;
            F1Save();
        }
    }

    private static string F1Json(JsonElement node,string key)=>
        node.ValueKind==JsonValueKind.Object &&
        node.TryGetProperty(key,out var field)&&field.ValueKind==JsonValueKind.String?
        field.GetString()??"":"";

    private static int F1Id(string name,string concept)
    {
        unchecked{
            uint h=2166136261;
            foreach(var c in ("F1|"+name+"|"+concept).ToUpperInvariant()){
                h^=c;h*=16777619;
            }
            return (int)(h&0x7fffffff);
        }
    }

    private async Task F1GenerateAsync()
    {
        try
        {
            F1Status="ChatGPT: generating individual F1 player concepts, six ratings and SEO in ONE request";
            F1PrepareChat();
            var completed=_f1History.Select(x=>x.Name+" — "+x.Concept)
                .Distinct(StringComparer.OrdinalIgnoreCase).Take(200);
            var prompt="Generate exactly "+F1Count+" REAL individual Formula 1 racing driver ideas, one subject per video, no teams, duos or comparisons. "+
                "Niche: "+F1Niche+". Video format: "+F1Format+". "+
                "Avoid completed name/concept pairs: "+string.Join("; ",completed)+". "+
                "For anime and gaming characters identify the canonical series or game in concept; for real people avoid invented achievements. "+
                "In the SAME response generate six SUBJECTIVE scores 0.0-10.0 for: pace, racecraft, qualifying, consistency, overtaking, strategy. "+
                "Those are editorial ratings, never official statistics or sourced review scores. "+
                "Write one accurate SEO title (40-85 chars), short caption (100-240 chars) and 3-5 hashtags. "+
                "Use only genuine known subjects; do not invent people, titles, games, broadcasts, stats or achievements. "+
                "Return ONLY a valid JSON object inside fenced json: "+
                "{\"response_type\":\"f1clipper_ideas_v1\",\"players\":[{\"name\":\"Lewis Hamilton\",\"concept\":\"Iconic individual highlight moments\",\"scores\":{\"pace\":8,\"racecraft\":8,\"qualifying\":8,\"consistency\":8,\"overtaking\":8,\"strategy\":8},\"seo\":{\"title\":\"Individual character or person highlights and ratings\",\"caption\":\"A short-form individual spotlight with subjective category scores.\",\"hashtags\":\"#Highlights #Clips #Edits\"}}]}";
            var raw=await _chat.GenerateTextAsync(_f1Chat,Settings,prompt,Log,_cts.Token);
            var first=raw.IndexOf('{');var last=raw.LastIndexOf('}');
            if(first<0||last<=first)throw new InvalidDataException("ChatGPT did not return JSON.");
            using var json=JsonDocument.Parse(raw.Substring(first,last-first+1));
            var players=json.RootElement.GetProperty("players");
            if(players.ValueKind!=JsonValueKind.Array)
                throw new InvalidDataException("Expected players JSON array.");
            var ready=new List<F1ClipperEntry>();
            var seen=new HashSet<int>();
            foreach(var candidate in players.EnumerateArray().Take(F1Count*3))
            {
                if(candidate.ValueKind!=JsonValueKind.Object)continue;
                var name=F1Json(candidate,"name").Trim();
                var concept=F1Json(candidate,"concept").Trim();
                if(name.Length is < 4 or > 100||concept.Length is < 4 or > 130||
                    name.Any(char.IsControl))continue;
                var id=F1Id(name,concept);
                if(!seen.Add(id)||_f1History.Any(x=>x.PlayerId==id)||
                    PublishingQueue.Any(x=>x.VideoPath.Contains(
                        id.ToString(CultureInfo.InvariantCulture)+"_",
                        StringComparison.OrdinalIgnoreCase)))continue;
                if(!candidate.TryGetProperty("scores",out var scores)||
                    scores.ValueKind!=JsonValueKind.Object)continue;
                string[] keys=["pace","racecraft","qualifying","consistency","overtaking","strategy"];
                var values=keys.Select(k=>scores.TryGetProperty(k,out var value)&&
                    value.ValueKind==JsonValueKind.Number?
                    value.GetDouble():double.NaN).ToArray();
                if(values.Any(x=>!double.IsFinite(x)||x<0||x>10))continue;
                var seo=candidate.TryGetProperty("seo",out var info)&&
                    info.ValueKind==JsonValueKind.Object?info:default;
                ready.Add(new F1ClipperEntry{
                    PlayerId=id,Name=name,Concept=concept,Mode=F1Format,
                    Scores=values.Select(x=>Math.Round(x,1)).ToArray(),
                    SeoTitle=F1Json(seo,"title"),SeoCaption=F1Json(seo,"caption"),
                    SeoHashtags=F1Json(seo,"hashtags")
                });
                if(ready.Count>=F1Count)break;
            }
            if(ready.Count==0)throw new InvalidDataException("No valid F1 player ideas received.");
            F1ClipperIdeas.Clear();
            foreach(var item in ready)F1ClipperIdeas.Add(item);
            F1Save();
            F1Status=ready.Count+" individual F1 player ideas ready. "+
                "Ratings are subjective; actual F1 statistics are not invented.";
        }
        catch(OperationCanceledException){F1Save();throw;}
        catch(Exception ex){F1Status="F1 idea generation failed: "+ex.Message;Log(F1Status);}
    }

    private static string? F1CachedSource(string folder,F1ClipperEntry player)
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

    private async Task<string> F1DownloadAsync(F1ClipperEntry player)
    {
        Directory.CreateDirectory(F1SourceFolder);
        var target=Path.Combine(F1SourceFolder,player.PlayerId+".mp4");
        var downloader=MovieDownloaderPath();
        var search=player.Name+" Formula 1 driver best overtakes race highlights";
        player.Status="Finding player-specific YouTube highlights";
        var args=new List<string>{"--no-playlist","--flat-playlist","--dump-single-json",
            "--skip-download","--no-warnings","ytsearch12:"+search};
        if(F1BrowserLogin)args.InsertRange(0,["--cookies-from-browser","edge"]);
        var result=await MovieRunProcessAsync(downloader,args,_cts.Token);
        if(result.Code!=0)throw new InvalidOperationException("YouTube highlight search: "+
            MovieDownloaderError(result.Error));
        using var json=JsonDocument.Parse(result.Output);
        if(!json.RootElement.TryGetProperty("entries",out var entries)||
            entries.ValueKind!=JsonValueKind.Array)
            throw new InvalidDataException("No single F1 driver racing highlight clips found on YouTube.");
        var names=player.Name.Split(' ',StringSplitOptions.RemoveEmptyEntries);
        var sources=new List<(string Url,int Priority)>();
        foreach(var e in entries.EnumerateArray())
        {
            if(e.ValueKind!=JsonValueKind.Object)continue;
            var title=F1Json(e,"title");
            var videoId=F1Json(e,"id");
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
            "No Formula 1 racing driver highlight source found. Import an authorized local clip.");
        var pattern=Path.Combine(F1SourceFolder,player.PlayerId+".%(ext)s");
        var failures=new List<string>();
        foreach(var url in sources.OrderByDescending(x=>x.Priority)
            .Select(x=>x.Url).Distinct().Take(4))
        {
            _cts.Token.ThrowIfCancellationRequested();
            var download=new List<string>{"--no-playlist","--no-progress",
                "--retries","2","-f","bv*+ba/b","-S","res:1080,ext:mp4",
                "--merge-output-format","mp4","--recode-video","mp4",
                "-o",pattern,url};
            if(F1BrowserLogin)download.InsertRange(0,["--cookies-from-browser","edge"]);
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

    private async Task<PublishQueueItem> F1RenderAsync(F1ClipperEntry player,string sourcePath)
    {
        var music=F1SoundFile;
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
        var work=Path.Combine(F1Root,player.PlayerId+"-"+
            DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(F1ExportFolder);
        var cover=Path.Combine(work,"thumbnail.jpg");
        var video=Path.Combine(F1ExportFolder,player.PlayerId+"_"+
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
        // Unlike MovieClipper, F1Clipper must never crop basketball
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
        var ratingsMode=player.Mode=="Driver Ratings";
        var labels=ratingsMode
            ?new[]{"PACE","RACECRAFT","QUALIFYING","CONSISTENCY","OVERTAKING","STRATEGY","OVERALL"}
            :player.Mode=="Best Overtakes"
                ?new[]{"PASS 01","PASS 02","PASS 03","PASS 04","PASS 05","PASS 06","OVERTAKES"}
                :new[]{"THE DRIVER","THE RACING","ICONIC OVERTAKES","THE JOURNEY","GREAT RACES","THE LEGEND","DRIVER LEGACY"};
        var values=player.Scores.Concat([Math.Round(player.Scores.Average(),1)]).ToArray();
        var intro=ratingsMode?"F1 DRIVER RATINGS":
            player.Mode=="Best Overtakes"?"BEST OVERTAKES":"DRIVER LEGACY";
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
            ",asetpts=PTS-STARTPTS,volume="+MovieNumber(F1MusicLevel)+"[a]");
        args.AddRange(["-filter_complex",string.Join(";",filters),
            "-map","[v]","-map","[a]","-t",MovieNumber(seconds),
            "-frames:v",frames.ToString(CultureInfo.InvariantCulture),
            "-c:v","libx264","-preset","veryfast","-crf","21",
            "-c:a","aac","-b:a","160k","-pix_fmt","yuv420p",
            "-movflags","+faststart",video]);
        player.Status="Rendering "+count+" beat-synced single F1 driver racing highlight clips";
        var result=await MovieRunProcessAsync(ffmpeg,args,_cts.Token,work);
        if(result.Code!=0)
            throw new InvalidOperationException("FFmpeg: "+
                result.Error[^Math.Min(result.Error.Length,900)..]);
        if(!File.Exists(video)||new FileInfo(video).Length<10000)
            throw new IOException("F1Clipper video file was not generated.");
        player.OutputPath=video;
        player.ThumbnailPath=cover;
        F1Save();
        return new PublishQueueItem{
            VideoPath=video,ThumbnailPath=cover,
            Title=player.Name+" — "+player.Concept,Description=player.Concept,
            TikTokCaption=player.Name+" F1 player ratings #F1 #Basketball",
            YouTubeTitle=player.Name+" — "+player.Concept+" #Shorts",
            YouTubeDescription=player.Concept
        };
    }

    private static void F1EnsureSeo(F1ClipperEntry player)
    {
        if(player.SeoTitle.Length is < 15 or > 120 ||
            !player.SeoTitle.Contains(player.Name,StringComparison.OrdinalIgnoreCase))
            player.SeoTitle=player.Name+" — "+player.Concept+" | F1 Player Review";
        if(player.SeoCaption.Length is < 25 or > 400)
            player.SeoCaption=player.Name+" F1 player spotlight: "+player.Concept+
                ". Shooting, finishing, handles, playmaking, defense and IQ scored out of 10.";
        var tags=Regex.Matches(player.SeoHashtags,
            @"(?<!\S)#[A-Za-z][A-Za-z0-9_]{1,24}(?=\s|$)")
            .Select(x=>x.Value).Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToList();
        if(tags.Count<3)tags=["#F1","#Basketball","#PlayerRatings"];
        player.SeoHashtags=string.Join(" ",tags);
    }

    private async Task F1StartAsync()
    {
        if(_f1Processing)return;
        _f1Processing=true;
        F1PrepareChat();
        try
        {
            var ideasWereEmpty=F1ClipperIdeas.Count==0;
            if(ideasWereEmpty)await F1GenerateAsync();
            var selected=F1ClipperIdeas.Where(x=>x.IsChecked&&
                (!_f1RetryFailed||ideasWereEmpty||x.Status.StartsWith("Failed",
                    StringComparison.OrdinalIgnoreCase))).ToList();
            if(selected.Count==0){
                F1Status="No pending Formula 1 racing driver ideas selected.";return;
            }
            if(!DateTime.TryParseExact(F1FirstSlot,"yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture,DateTimeStyles.AssumeLocal,out var first)||
                first<=DateTime.Now.AddMinutes(25)||first.Minute%5!=0)
                throw new InvalidDataException("First posting slot must be 25+ minutes away, on 5-minute increments.");
            if(!File.Exists(F1SoundFile))
                throw new FileNotFoundException("Select a background soundtrack first.",F1SoundFile);
            var originallyChecked=PublishingQueue.Where(x=>x.IsChecked).ToList();
            var completed=new List<PublishQueueItem>();
            foreach(var player in selected)
            {
                _cts.Token.ThrowIfCancellationRequested();
                try
                {
                    if(_f1History.Any(h=>h.PlayerId==player.PlayerId)||
                        (!string.IsNullOrWhiteSpace(player.OutputPath)&&
                         PublishingQueue.Any(q=>q.VideoPath.Equals(player.OutputPath,
                             StringComparison.OrdinalIgnoreCase))))
                    {
                        player.Status="Already queued (no duplicate)";
                        player.IsChecked=false;F1Save();continue;
                    }
                    var source=File.Exists(player.SourcePath)?player.SourcePath:
                        F1CachedSource(F1SourceFolder,player);
                    if(source is null&&F1AutoDownload)
                        source=await F1DownloadAsync(player);
                    if(source is null)
                    {
                        player.Status="Failed: player highlight source unavailable";
                        F1Save();continue;
                    }
                    player.SourcePath=source;player.Status="HighlightsReady";F1Save();
                    var output=File.Exists(player.OutputPath)?
                        new PublishQueueItem{VideoPath=player.OutputPath,
                            ThumbnailPath=player.ThumbnailPath}:
                        await F1RenderAsync(player,source);
                    F1EnsureSeo(player);
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
                    F1Save();SavePublishingQueue();
                    var history=new F1ClipperHistoryItem{
                        PlayerId=player.PlayerId,Name=player.Name,Concept=player.Concept,
                        OutputPath=output.VideoPath};
                    _f1History.Add(history);
                    F1History.Insert(0,history);
                    F1Save();
                }
                catch(OperationCanceledException){player.Status="Stopped";F1Save();throw;}
                catch(Exception ex){
                    player.Status="Failed: "+ex.Message;Log("F1Clipper "+player.Name+": "+ex.Message);
                    F1Save();
                }
            }
            Raise(nameof(AllPublishItemsChecked));
            RefreshPublishingCalendar();
            RefreshVideoLibrary();
            F1Status="Exported "+completed.Count+"/"+selected.Count+
                " individual F1 player videos to "+F1ExportFolder+".";
            if(completed.Count==0||!F1Publish)return;
            if(!PublishConsent){
                F1Status+=" Enable existing publishing authorization to auto-post.";
                return;
            }
            foreach(var old in originallyChecked)old.IsChecked=false;
            try{await RunTikTokBatchAsync();}
            finally{foreach(var old in originallyChecked)old.IsChecked=true;}
        }
        catch(OperationCanceledException){F1Status="Stopped. Resume saved F1 workflow.";F1Save();throw;}
        catch(Exception ex){F1Status="F1Clipper: "+ex.Message;Log(F1Status);}
        finally{_f1Processing=false;F1Save();}
    }
}
