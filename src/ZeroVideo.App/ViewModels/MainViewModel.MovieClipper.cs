using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using ZeroVideo.App.Infrastructure;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.ViewModels;

public sealed class MovieClipperEntry : ObservableObject
{
    public int TmdbId { get; set; }
    public string Title { get; set; } = "";
    public string Year { get; set; } = "";
    public string PosterPath { get; set; } = "";
    public string Overview { get; set; } = "";
    public double VoteAverage { get; set; }
    public int VoteCount { get; set; }
    private bool _checked = true;
    private string _status = "Ready";
    public bool IsChecked { get => _checked; set => Set(ref _checked, value); }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string Rating => VoteCount > 0 ? VoteAverage.ToString("0.0", CultureInfo.InvariantCulture) + "/10 TMDB" : "Not rated";
    public string Label => Title + (Year.Length == 4 ? " (" + Year + ")" : "");
}

public sealed partial class MainViewModel
{
    private readonly HttpClient _movieHttp = new() { Timeout = TimeSpan.FromSeconds(35) };
    private string _movieNiche = "Underrated thriller and mystery movies";
    private int _movieCount = 10;
    private string _movieSourceFolder = "";
    private string _movieSoundFile = "";
    private string _movieStatus = "Set an authorized trailer folder and optional 28-second audio, then run a batch.";
    private string _movieFirstSlot = DateTime.Now.AddHours(2).ToString("yyyy-MM-dd HH:00", CultureInfo.InvariantCulture);
    private bool _moviePublish = true;
    private bool _movieKeepDialogue;
    private bool _movieProcessing;
    private double _movieMusicLevel = 0.85;
    private string _movieToken = "";
    private const int MovieLengthSeconds = 30;
    private static readonly string MoviePrefsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeroVideo", "movieclipper.preferences.json");
    private void MovieSaveMediaPreferences()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MoviePrefsFile)!);
        File.WriteAllText(MoviePrefsFile, JsonSerializer.Serialize(new { source_folder = MovieSourceFolder, sound_file = MovieSoundFile }));
    }
    public ObservableCollection<MovieClipperEntry> MovieClipperIdeas { get; } = [];
    public string MovieNiche { get => _movieNiche; set => Set(ref _movieNiche, value); }
    public int MovieCount { get => _movieCount; set => Set(ref _movieCount, Math.Clamp(value, 1, 20)); }
    public string MovieSourceFolder { get => _movieSourceFolder; set => Set(ref _movieSourceFolder, value); }
    public string MovieSoundFile { get => _movieSoundFile; set => Set(ref _movieSoundFile, value); }
    public string MovieStatus { get => _movieStatus; set => Set(ref _movieStatus, value); }
    public string MovieFirstSlot { get => _movieFirstSlot; set => Set(ref _movieFirstSlot, value); }
    public bool MoviePublish { get => _moviePublish; set => Set(ref _moviePublish, value); }
    public bool MovieKeepDialogue { get => _movieKeepDialogue; set => Set(ref _movieKeepDialogue, value); }
    public bool MovieProcessing { get => _movieProcessing; private set => Set(ref _movieProcessing, value); }
    public double MovieMusicLevel { get => _movieMusicLevel; set => Set(ref _movieMusicLevel, Math.Clamp(value, 0, 1)); }
    public ICommand MovieGenerateCommand { get; private set; } = null!;
    public ICommand MovieStartCommand { get; private set; } = null!;
    public ICommand MovieBrowseSourceCommand { get; private set; } = null!;
    public ICommand MovieBrowseSoundCommand { get; private set; } = null!;
    public ICommand MovieFindTrailerCommand { get; private set; } = null!;

    private void InitializeMovieClipperCommands()
    {
        MovieGenerateCommand = new AsyncCommand(() => RunManagedJobAsync("MovieClipper: ChatGPT ideas", MovieGenerateAsync));
        MovieStartCommand = new AsyncCommand(() => RunManagedJobAsync("MovieClipper: 30s batch", MovieStartAsync));
        MovieBrowseSourceCommand = new RelayCommand(() =>
        {
            var picker = new OpenFolderDialog { Title = "Folder containing licensed movie trailer files" };
            if (picker.ShowDialog() == true) { MovieSourceFolder = picker.FolderName; MovieSaveMediaPreferences(); }
        });
        MovieBrowseSoundCommand = new RelayCommand(() =>
        {
            var picker = new OpenFileDialog { Filter = "Audio files|*.mp3;*.m4a;*.wav;*.aac;*.flac", CheckFileExists = true };
            if (picker.ShowDialog() == true) { MovieSoundFile = picker.FileName; MovieSaveMediaPreferences(); }
        });
        MovieFindTrailerCommand = new RelayCommand(() =>
        {
            var movie = MovieClipperIdeas.FirstOrDefault(x => x.IsChecked);
            if (movie is null) { MovieStatus = "Check a movie first."; return; }
            var url = "https://www.youtube.com/results?search_query=" + Uri.EscapeDataString(movie.Label + " official trailer");
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            MovieStatus = "Browser search opened. Use trailer footage you are authorized to download, edit and publish.";
        });
        MovieSourceFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ZeroVideo", "Trailers");
        // Local private config only. Never embed supplied TMDB credentials in this public repository.
        var cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZeroVideo", "movieclipper.local.json");
        try
        {
            if (File.Exists(cfg))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(cfg));
                var obj = doc.RootElement;
                if (obj.TryGetProperty("tmdb_read_access_token", out var tok))
                    _movieToken = tok.GetString() ?? "";
                if (obj.TryGetProperty("source_folder", out var folder))
                    MovieSourceFolder = folder.GetString() ?? MovieSourceFolder;
                if (obj.TryGetProperty("sound_file", out var music))
                    MovieSoundFile = music.GetString() ?? MovieSoundFile;
            }
        }
        catch (Exception ex) { MovieStatus = "Private MovieClipper configuration could not be read: " + ex.Message; }
        try
        {
            if (File.Exists(MoviePrefsFile))
            {
                using var prefs = JsonDocument.Parse(File.ReadAllText(MoviePrefsFile));
                MovieSourceFolder = MovieJson(prefs.RootElement, "source_folder") is var folder && !string.IsNullOrWhiteSpace(folder) ? folder : MovieSourceFolder;
                MovieSoundFile = MovieJson(prefs.RootElement, "sound_file") is var sound && !string.IsNullOrWhiteSpace(sound) ? sound : MovieSoundFile;
            }
        }
        catch (Exception ex) { MovieStatus = "MovieClipper media preferences: " + ex.Message; }
        if (string.IsNullOrWhiteSpace(_movieToken))
            _movieToken = Environment.GetEnvironmentVariable("ZEROVIDEO_TMDB_READ_TOKEN") ?? "";
    }

    private static string MovieClean(string name)
    {
        var value = new string(name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return value;
    }

    private static string MovieJson(JsonElement obj, string field)
        => obj.TryGetProperty(field, out var node) && node.ValueKind == JsonValueKind.String ? node.GetString() ?? "" : "";

    private async Task<JsonDocument> MovieTmdbAsync(string resource)
    {
        if (string.IsNullOrWhiteSpace(_movieToken))
            throw new InvalidOperationException("TMDB credential missing. Install the private movieclipper.local.json file once.");
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.themoviedb.org/3/" + resource);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _movieToken);
        using var reply = await _movieHttp.SendAsync(req, _cts.Token);
        reply.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await reply.Content.ReadAsStringAsync(_cts.Token));
    }

    private async Task MovieGenerateAsync()
    {
        try
        {
            MovieStatus = "ChatGPT is choosing niche-specific movies...";
            var prompt = $"List exactly {MovieCount} REAL feature movies suited to short-form TikTok film reviews in this niche: {MovieNiche}. " +
                "Make a diverse list of confirmed titles (no invented films), with release years. Do not include TV shows, duplicates or generic ideas. " +
                "Return ONLY a valid JSON object in a fenced json code block: " +
                "{\"response_type\":\"movieclipper_ideas_v1\",\"movies\":[{\"title\":\"Movie name\",\"year\":\"2024\"}]}";
            var raw = await _chat.GenerateTextAsync(
                new VideoProject { Title = "MovieClipper niche movie ideas", Topic = MovieNiche }, Settings, prompt, Log, _cts.Token);
            var open = raw.IndexOf('{');
            var close = raw.LastIndexOf('}');
            if (open < 0 || close <= open) throw new InvalidDataException("ChatGPT did not produce a JSON object.");
            using var json = JsonDocument.Parse(raw.Substring(open, close - open + 1));
            var items = json.RootElement.GetProperty("movies");
            MovieClipperIdeas.Clear();
            var seen = new HashSet<int>();
            foreach (var candidate in items.EnumerateArray().Take(MovieCount * 2))
            {
                var title = MovieJson(candidate, "title");
                var year = MovieJson(candidate, "year");
                if (title.Length == 0) continue;
                using var matches = await MovieTmdbAsync("search/movie?query=" +
                    Uri.EscapeDataString(title) + "&language=en-US&include_adult=false");
                var results = matches.RootElement.GetProperty("results").EnumerateArray();
                var expected = MovieClean(title);
                var selected = results.FirstOrDefault(x =>
                    MovieClean(MovieJson(x, "title")) == expected &&
                    (year.Length != 4 || MovieJson(x, "release_date").StartsWith(year, StringComparison.Ordinal)));
                if (selected.ValueKind != JsonValueKind.Object || !seen.Add(selected.GetProperty("id").GetInt32()))
                    continue;
                MovieClipperIdeas.Add(new MovieClipperEntry
                {
                    TmdbId = selected.GetProperty("id").GetInt32(),
                    Title = MovieJson(selected, "title"),
                    Year = MovieJson(selected, "release_date") is var date && date.Length >= 4 ? date[..4] : "",
                    Overview = MovieJson(selected, "overview"),
                    PosterPath = MovieJson(selected, "poster_path"),
                    VoteAverage = selected.GetProperty("vote_average").GetDouble(),
                    VoteCount = selected.GetProperty("vote_count").GetInt32()
                });
                if (MovieClipperIdeas.Count >= MovieCount) break;
            }
            MovieStatus = MovieClipperIdeas.Count + " movies verified in TMDB and ready for 30-second rendering.";
        }
        catch (Exception ex) { MovieStatus = "Movie ideas failed: " + ex.Message; Log(MovieStatus); }
    }

    private static string? MovieFindFile(string folder, MovieClipperEntry movie)
    {
        if (!Directory.Exists(folder)) return null;
        var expected = MovieClean(movie.Title);
        if (expected.Length == 0) return null;
        return Directory.EnumerateFiles(folder)
            .Where(x => new[] { ".mp4", ".mov", ".mkv", ".webm" }.Contains(Path.GetExtension(x).ToLowerInvariant()))
            .FirstOrDefault(x =>
            {
                var file = MovieClean(Path.GetFileNameWithoutExtension(x));
                return file.Contains(expected, StringComparison.Ordinal) &&
                    (movie.Year.Length != 4 || !file.Any(char.IsDigit) || file.Contains(movie.Year));
            });
    }

    private async Task MovieStartAsync()
    {
        if (MovieProcessing) return;
        MovieProcessing = true;
        try
        {
            if (MovieClipperIdeas.Count == 0) await MovieGenerateAsync(); // One click from niche to queue.
            var selected = MovieClipperIdeas.Where(x => x.IsChecked).ToList();
            if (selected.Count == 0) { MovieStatus = "No verified movies selected."; return; }
            if (!DateTime.TryParseExact(MovieFirstSlot, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var first) ||
                first <= DateTime.Now.AddMinutes(25) || first.Minute % 5 != 0)
                throw new InvalidDataException("Start time must be at least 25 minutes ahead and on a five-minute increment.");
            if (selected.Count > 20 * 24)
                throw new InvalidDataException("Limit hourly TikTok scheduling to 20 days per batch.");
            if (!string.IsNullOrWhiteSpace(MovieSoundFile) && !File.Exists(MovieSoundFile))
                throw new FileNotFoundException("Custom soundtrack is missing.", MovieSoundFile);
            var original = PublishingQueue.Where(x => x.IsChecked).ToList();
            var finished = new List<PublishQueueItem>();
            foreach (var movie in selected)
            {
                _cts.Token.ThrowIfCancellationRequested();
                var trailer = MovieFindFile(MovieSourceFolder, movie);
                if (trailer is null) { movie.Status = "Source needed: authorized trailer file not found"; continue; }
                if (string.IsNullOrWhiteSpace(movie.PosterPath)) { movie.Status = "No TMDB poster"; continue; }
                movie.Status = "Rendering";
                try
                {
                    var output = await MovieRenderAsync(movie, trailer);
                    if (PublishingQueue.Any(x => x.VideoPath.Equals(output.VideoPath, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var next = first.AddHours(finished.Count);
                    output.IsChecked = true;
                    output.IsScheduled = true;
                    output.ScheduleDateText = next.ToString("yyyy-MM-dd HH:mm");
                    output.ScheduledAt = new DateTimeOffset(next);
                    output.MetadataStatus = "Ready";
                    TrackPublishingItem(output);
                    PublishingQueue.Add(output);
                    finished.Add(output);
                    movie.Status = "Queued " + output.ScheduleDateText;
                    SavePublishingQueue();
                }
                catch (Exception ex) { movie.Status = "Failed: " + ex.Message; Log("MovieClipper: " + movie.Label + ": " + ex.Message); }
            }
            Raise(nameof(AllPublishItemsChecked));
            RefreshPublishingCalendar();
            RefreshVideoLibrary();
            MovieStatus = $"Rendered {finished.Count}/{selected.Count} movies, exactly {MovieLengthSeconds} seconds each. " +
                "Missing or failed sources remain visible in the list.";
            if (finished.Count == 0 || !MoviePublish) return;
            if (!PublishConsent)
            {
                MovieStatus += " For one-click TikTok submission, enable the existing Publish authorization first.";
                return;
            }
            // Submit only this new batch through the proven build #522 publisher.
            foreach (var item in original) item.IsChecked = false;
            try { await RunTikTokBatchAsync(); }
            finally { foreach (var item in original) item.IsChecked = true; }
        }
        catch (Exception ex) { MovieStatus = "MovieClipper: " + ex.Message; Log(MovieStatus); }
        finally { MovieProcessing = false; }
    }

    private async Task<PublishQueueItem> MovieRenderAsync(MovieClipperEntry movie, string trailer)
    {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZeroVideo", "MovieClipper", movie.TmdbId + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
        Directory.CreateDirectory(root);
        var poster = Path.Combine(root, "thumbnail.jpg");
        var output = Path.Combine(root, "movieclip.mp4");
        using (var reply = await _movieHttp.GetAsync("https://image.tmdb.org/t/p/original" + movie.PosterPath, _cts.Token))
        {
            reply.EnsureSuccessStatusCode();
            await using var stream = File.Create(poster);
            await reply.Content.CopyToAsync(stream, _cts.Token);
        }
        // Title is already on the TMDB official theatrical poster; add a factual TMDB score as an overlay.
        await File.WriteAllTextAsync(Path.Combine(root, "rating.txt"),
            movie.VoteCount > 0 ? "TMDB " + movie.Rating : "TMDB: NOT YET RATED", _cts.Token);
        var ffmpeg = !string.IsNullOrWhiteSpace(Settings.FfmpegPath) ? Settings.FfmpegPath : "ffmpeg";
        var args = new List<string> {
            "-y", "-loop", "1", "-framerate", "30", "-t", "2", "-i", poster,
            "-stream_loop", "-1", "-i", trailer
        };
        var withMusic = File.Exists(MovieSoundFile);
        if (withMusic) args.AddRange(["-stream_loop", "-1", "-i", MovieSoundFile]);
        const string frame = "scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1,fps=30,format=yuv420p";
        var filter = "[0:v]" + frame + ",trim=duration=2,setpts=PTS-STARTPTS[intro];" +
            "[1:v]" + frame +
            ",trim=duration=28,setpts=PTS-STARTPTS," +
            "drawtext=textfile=rating.txt:fontcolor=white:fontsize=50:box=1:boxcolor=black@0.70:boxborderw=18:x=(w-text_w)/2:y=h-260[scene];" +
            "[intro][scene]concat=n=2:v=1:a=0[v]";
        // Soundtrack follows the 2-second title card, running for 28 seconds.
        if (withMusic)
        {
            filter += ";[2:a]atrim=duration=28,asetpts=PTS-STARTPTS,volume=" +
                MovieMusicLevel.ToString(CultureInfo.InvariantCulture) +
                ",adelay=2000|2000[music]";
            if (MovieKeepDialogue)
                filter += ";[1:a]atrim=duration=28,asetpts=PTS-STARTPTS,volume=0.45,adelay=2000|2000[voice];" +
                    "[voice][music]amix=inputs=2:duration=longest:normalize=0[a]";
            else filter += ";[music]anull[a]";
        }
        else if (MovieKeepDialogue)
            filter += ";[1:a]atrim=duration=28,asetpts=PTS-STARTPTS,adelay=2000|2000[a]";
        args.AddRange(["-filter_complex", filter, "-map", "[v]"]);
        if (withMusic || MovieKeepDialogue)
            args.AddRange(["-map", "[a]", "-c:a", "aac", "-b:a", "160k"]);
        args.AddRange(["-t", "30", "-c:v", "libx264", "-preset", "medium", "-crf", "20",
            "-pix_fmt", "yuv420p", "-movflags", "+faststart", output]);
        var start = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardError = true, WorkingDirectory = root };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("FFmpeg could not launch.");
        var errorTask = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(_cts.Token);
        if (process.ExitCode != 0)
        {
            var full = await errorTask;
            throw new InvalidOperationException("FFmpeg: " + full[^Math.Min(600, full.Length)..]);
        }
        if (!File.Exists(output) || new FileInfo(output).Length < 1024)
            throw new IOException("Rendered MP4 is empty.");
        return new PublishQueueItem {
            VideoPath = output, ThumbnailPath = poster, Title = movie.Label,
            Description = movie.Overview,
            TikTokCaption = movie.Label + " 🎬 " +
                (movie.VoteCount > 0 ? movie.Rating + " — TMDB audience score. " : "Not yet rated on TMDB. ") +
                "Would you watch it? #MovieTok #MovieReview #FilmTok",
            YouTubeTitle = movie.Label + " #Shorts",
            YouTubeDescription = movie.Overview
        };
    }
}
