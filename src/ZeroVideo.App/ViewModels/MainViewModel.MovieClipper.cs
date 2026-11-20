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
    private string _movieStatus = "Ready: ChatGPT ideas → TMDB artwork → automatic YouTube trailer → soundtrack-length edit → TikTok queue.";
    private string _movieFirstSlot = DateTime.Now.AddHours(2).ToString("yyyy-MM-dd HH:00", CultureInfo.InvariantCulture);
    private bool _moviePublish = true;
    private bool _movieKeepDialogue;
    private bool _movieAutoDownload = true;
    private bool _movieUseBrowserYouTubeLogin;
    private bool _movieProcessing;
    private bool _movieNewChat;
    private bool _movieRetryFailedOnly;
    private VideoProject _movieChatProject = new() { Title = "MovieClipper batch", Topic = "Movie reviews" };
    private MovieCheckpoint _movieCheckpoint = new();
    private double _movieMusicLevel = 0.85;
    private string _movieToken = "";
    // Duration is read from the selected soundtrack at render time.
    private static readonly string MoviePrefsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZeroVideo", "movieclipper.preferences.json");
    private void MovieSaveMediaPreferences()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(MoviePrefsFile)!);
        File.WriteAllText(MoviePrefsFile, JsonSerializer.Serialize(new { source_folder = MovieSourceFolder, sound_file = MovieSoundFile, use_browser_youtube_login = MovieUseBrowserYouTubeLogin }));
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
    public bool MovieAutoDownload { get => _movieAutoDownload; set => Set(ref _movieAutoDownload, value); }
    public bool MovieUseBrowserYouTubeLogin { get => _movieUseBrowserYouTubeLogin; set { if (Set(ref _movieUseBrowserYouTubeLogin, value)) MovieSaveMediaPreferences(); } }
    public bool MovieProcessing { get => _movieProcessing; private set => Set(ref _movieProcessing, value); }
    public bool MovieNewChat { get => _movieNewChat; set => Set(ref _movieNewChat, value); }
    public string MovieCheckpointStatus => _movieCheckpoint.Items.Count + " checkpointed movies; saved at " + MovieCheckpointPath;
    public double MovieMusicLevel { get => _movieMusicLevel; set => Set(ref _movieMusicLevel, Math.Clamp(value, 0, 1)); }
    public ICommand MovieGenerateCommand { get; private set; } = null!;
    public ICommand MovieResumeCommand { get; private set; } = null!;
    public ICommand MovieRetryFailedCommand { get; private set; } = null!;
    public ICommand MovieForceStopCommand => ForceStopJobCommand;
    public ICommand MovieStartCommand { get; private set; } = null!;
    public ICommand MovieBrowseSourceCommand { get; private set; } = null!;
    public ICommand MovieBrowseSoundCommand { get; private set; } = null!;
    public ICommand MovieFindTrailerCommand { get; private set; } = null!;
    public ICommand MovieOpenOutputCommand { get; private set; } = null!;
    public string MovieExportFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "ZeroVideo", "MovieClipper");

    private void InitializeMovieClipperCommands()
    {
        MovieOpenOutputCommand = new RelayCommand(() => { Directory.CreateDirectory(MovieExportFolder); Process.Start(new ProcessStartInfo("explorer.exe", "\"" + MovieExportFolder + "\"") { UseShellExecute = true }); });
        MovieGenerateCommand = new AsyncCommand(() => RunManagedJobAsync("MovieClipper: ChatGPT ideas", MovieGenerateAsync));
        MovieStartCommand = new AsyncCommand(() => RunManagedJobAsync(
            "MovieClipper: music-length batch", MovieStartAsync, publishing: MoviePublish));
        MovieResumeCommand = new AsyncCommand(() => RunManagedJobAsync("MovieClipper: resume checkpoint", async () =>
        {
            MovieLoadHistory();
        MovieRestoreCheckpoint();
        MovieImportPreviousExports();
            _movieRetryFailedOnly = false;
            await MovieStartAsync();
        }));
        MovieRetryFailedCommand = new AsyncCommand(() => RunManagedJobAsync("MovieClipper: retry failed", async () =>
        {
            MovieRestoreCheckpoint();
            _movieRetryFailedOnly = true;
            try { await MovieStartAsync(); }
            finally { _movieRetryFailedOnly = false; }
        }));
        MovieBrowseSourceCommand = new RelayCommand(() =>
        {
            var picker = new OpenFolderDialog { Title = "Trailer download/cache folder" };
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
        // Load private config from local user settings, or a private portable bundle next to the EXE.
        // Never embed supplied TMDB credentials in the public repository.
        var cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZeroVideo", "movieclipper.local.json");
        try
        {
            var portableCfg = Path.Combine(AppContext.BaseDirectory, "MovieClipper_Private_Setup", "movieclipper.local.json");
            if (!File.Exists(cfg) && File.Exists(portableCfg))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
                File.Copy(portableCfg, cfg, overwrite: false);
            }
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
                if (prefs.RootElement.TryGetProperty("use_browser_youtube_login", out var useBrowser) && useBrowser.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    _movieUseBrowserYouTubeLogin = useBrowser.GetBoolean();
            }
        }
        catch (Exception ex) { MovieStatus = "MovieClipper media preferences: " + ex.Message; }
        if (string.IsNullOrWhiteSpace(_movieToken))
            _movieToken = Environment.GetEnvironmentVariable("ZEROVIDEO_TMDB_READ_TOKEN") ?? "";
        MovieRestoreCheckpoint();
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
            if (string.IsNullOrWhiteSpace(_movieToken))
                throw new InvalidOperationException(
                    "TMDB credential not loaded. Launch the private portable package or install movieclipper.local.json in %LOCALAPPDATA%\\ZeroVideo.");
            MovieStatus = "ChatGPT is choosing niche-specific movies...";
            var excluded = _movieHistory.Select(x => x.Title + " (" + x.Year + ")")
                .Concat(PublishingQueue.Where(x => File.Exists(x.VideoPath))
                    .Select(x => x.Title)).Distinct(StringComparer.OrdinalIgnoreCase).Take(200);
            var prompt = $"List exactly {MovieCount} REAL feature movies suited to short-form TikTok film reviews in this niche: {MovieNiche}. " +
                "Return confirmed feature films, no TV shows, and NO movies from this completed history: " +
                string.Join("; ", excluded) + ". " +
                "In ONE RESPONSE generate the idea list, six subjective 0.0-10.0 ratings for each film (plot, acting, pacing, cinematography, writing, ending), " +
                "plus its engaging, accurate SEO title (40-85 chars), TikTok caption (100-240 chars) and 3-5 relevant hashtags. " +
                "Do not fabricate story details, critical consensus, or TMDB category scores. " +
                "Return ONLY one JSON object inside a fenced json code block, schema: " +
                "{\"response_type\":\"movieclipper_ideas_v2\",\"movies\":[{\"title\":\"Film title\",\"year\":\"2024\"," +
                "\"scores\":{\"plot\":8.0,\"acting\":8.0,\"pacing\":8.0,\"cinematography\":8.0,\"writing\":8.0,\"ending\":8.0}," +
                "\"seo\":{\"title\":\"Film review hook\",\"caption\":\"Accurate review caption\",\"hashtags\":\"#MovieReview #FilmTok #MovieTok\"}}]}";
            MoviePrepareChat();
            var raw = await _chat.GenerateTextAsync(
                _movieChatProject, Settings, prompt, Log, _cts.Token);
            MovieSaveCheckpoint();
            var open = raw.IndexOf('{');
            var close = raw.LastIndexOf('}');
            if (open < 0 || close <= open) throw new InvalidDataException("ChatGPT did not produce a JSON object.");
            using var json = JsonDocument.Parse(raw.Substring(open, close - open + 1));
            var items = json.RootElement.GetProperty("movies");
            // Keep verified checkpointed ideas until replacement generation is valid.
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
                if (selected.ValueKind != JsonValueKind.Object ||
                    !seen.Add(selected.GetProperty("id").GetInt32()) ||
                    _movieHistory.Any(x => x.TmdbId == selected.GetProperty("id").GetInt32()) ||
                    PublishingQueue.Any(x => x.VideoPath.Contains(
                        selected.GetProperty("id").GetInt32().ToString(CultureInfo.InvariantCulture) + "_",
                        StringComparison.OrdinalIgnoreCase)))
                    continue;
                var entry = new MovieClipperEntry
                {
                    TmdbId = selected.GetProperty("id").GetInt32(),
                    Title = MovieJson(selected, "title"),
                    Year = MovieJson(selected, "release_date") is var date && date.Length >= 4 ? date[..4] : "",
                    Overview = MovieJson(selected, "overview"),
                    PosterPath = MovieJson(selected, "poster_path"),
                    VoteAverage = selected.GetProperty("vote_average").GetDouble(),
                    VoteCount = selected.GetProperty("vote_count").GetInt32()
                };
                MovieClipperIdeas.Add(entry);
                var savedIdea = MovieCheckpointItem(entry);
                if (candidate.TryGetProperty("scores", out var categoryScores) &&
                    categoryScores.ValueKind == JsonValueKind.Object)
                {
                    string[] categories = ["plot", "acting", "pacing", "cinematography", "writing", "ending"];
                    var values = categories.Select(k => categoryScores.TryGetProperty(k, out var scoreItem) &&
                        scoreItem.ValueKind == JsonValueKind.Number ? scoreItem.GetDouble() : double.NaN).ToArray();
                    if (values.All(v => double.IsFinite(v) && v >= 0 && v <= 10))
                    {
                        savedIdea.Scores = values.Select(v => Math.Round(v, 1)).ToArray();
                        savedIdea.ScoringMethod = "ChatGPT subjective review score, generated with movie ideas";
                    }
                }
                if (candidate.TryGetProperty("seo", out var seo) && seo.ValueKind == JsonValueKind.Object)
                {
                    savedIdea.SeoTitle = MovieJson(seo, "title");
                    savedIdea.SeoCaption = MovieJson(seo, "caption");
                    savedIdea.SeoHashtags = MovieJson(seo, "hashtags");
                }
                MovieCheckpointStage(entry, "IdeasReady");
                if (MovieClipperIdeas.Count >= MovieCount) break;
            }
            MovieCheckpointIdeas();
            MovieStatus = MovieClipperIdeas.Count + " movies verified in TMDB and ready for soundtrack-length rendering.";
        }
        catch (OperationCanceledException) { MovieSaveCheckpoint(); throw; }
        catch (Exception ex) { MovieSaveCheckpoint(); MovieStatus = "Movie ideas failed: " + ex.Message; Log(MovieStatus); }
    }

    private static string? MovieFindFile(string folder, MovieClipperEntry movie)
    {
        if (!Directory.Exists(folder)) return null;
        var exactCache = Path.Combine(folder, movie.TmdbId + ".mp4");
        if (File.Exists(exactCache) && new FileInfo(exactCache).Length > 10_000) return exactCache;
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


    private static async Task<(int Code, string Output, string Error)> MovieRunProcessAsync(
        string executable, IEnumerable<string> arguments, CancellationToken ct, string? workingDirectory = null)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (!string.IsNullOrWhiteSpace(workingDirectory)) start.WorkingDirectory = workingDirectory;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var proc = Process.Start(start) ??
            throw new InvalidOperationException("Unable to launch " + Path.GetFileName(executable));
        using var stop = ct.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch (Exception) { }
        });
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        await proc.WaitForExitAsync(ct);
        return (proc.ExitCode, await stdout, await stderr);
    }

    private static string MovieDownloaderPath()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
        if (File.Exists(bundled)) return bundled;
        // This is an optional fallback for developer builds.
        return "yt-dlp";
    }

    private async Task<string> MovieDownloadTrailerAsync(MovieClipperEntry movie)
    {
        Directory.CreateDirectory(MovieSourceFolder);
        var downloader = MovieDownloaderPath();
        var target = Path.Combine(MovieSourceFolder, movie.TmdbId + ".mp4");
        if (File.Exists(target) && new FileInfo(target).Length > 10_000)
            return target;

        // Preferred source: TMDB's verified movie page includes YouTube trailer
        // IDs. This avoids relying on yt-dlp YouTube Search (frequently login gated).
        var candidates = new List<(string Url, int Score)>();
        try
        {
            using var videos = await MovieTmdbAsync(
                "movie/" + movie.TmdbId + "/videos?language=en-US");
            if (videos.RootElement.TryGetProperty("results", out var results))
            {
                foreach (var video in results.EnumerateArray())
                {
                    if (!string.Equals(MovieJson(video, "site"), "YouTube",
                        StringComparison.OrdinalIgnoreCase)) continue;
                    var id = MovieJson(video, "key");
                    if (id.Length != 11 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                        continue;
                    var type = MovieJson(video, "type");
                    if (!type.Equals("Trailer", StringComparison.OrdinalIgnoreCase) &&
                        !type.Equals("Teaser", StringComparison.OrdinalIgnoreCase)) continue;
                    var name = MovieJson(video, "name").ToLowerInvariant();
                    if (name.Contains("fanmade") || name.Contains("fan made") ||
                        name.Contains("reaction") || name.Contains("concept")) continue;
                    var official = video.TryGetProperty("official", out var o) &&
                        o.ValueKind == JsonValueKind.True;
                    candidates.Add(("https://www.youtube.com/watch?v=" + id,
                        (official ? 50 : 0) + (type == "Trailer" ? 20 : 0) +
                        (name.Contains("official") ? 10 : 0)));
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log("MovieClipper: TMDB trailer lookup unavailable: " + ex.Message); }

        var errors = new List<string>();
        if (candidates.Count == 0)
        {
            // Fallback only: yt-dlp search can fail with sign-in or bot checks.
            var searchText = movie.Label + " official movie trailer";
            var searchArgs = new List<string> { "--no-playlist", "--flat-playlist",
                "--dump-single-json", "--skip-download", "--no-warnings",
                "ytsearch8:" + searchText };
            if (MovieUseBrowserYouTubeLogin)
                searchArgs.InsertRange(0, ["--cookies-from-browser", "edge"]);
            var search = await MovieRunProcessAsync(downloader, searchArgs, _cts.Token);
            if (search.Code != 0)
            {
                errors.Add("YouTube search: " + MovieDownloaderError(search.Error));
            }
            else
            {
                try
                {
                    using var listing = JsonDocument.Parse(search.Output);
                    if (listing.RootElement.TryGetProperty("entries", out var entries) &&
                        entries.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in entries.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.Object) continue;
                            var title = MovieJson(item, "title");
                            var lower = title.ToLowerInvariant();
                            var id = MovieJson(item, "id");
                            if (!title.Contains(movie.Title, StringComparison.OrdinalIgnoreCase) ||
                                !lower.Contains("trailer") ||
                                lower.Contains("review") || lower.Contains("fanmade") ||
                                lower.Contains("fan made") || lower.Contains("reaction") ||
                                lower.Contains("concept") || id.Length != 11) continue;
                            candidates.Add(("https://www.youtube.com/watch?v=" + id,
                                lower.Contains("official") ? 10 : 0));
                        }
                    }
                }
                catch (JsonException ex) { errors.Add("YouTube search JSON: " + ex.Message); }
            }
        }

        if (candidates.Count == 0)
            throw new InvalidOperationException(
                "No official trailer could be discovered for " + movie.Label +
                ". " + string.Join(" | ", errors) +
                ". Use the trailer cache folder for a local source if YouTube requires sign-in.");

        var pattern = Path.Combine(MovieSourceFolder, movie.TmdbId + ".%(ext)s");
        foreach (var candidate in candidates.OrderByDescending(x => x.Score)
            .Select(x => x.Url).Distinct().Take(4))
        {
            _cts.Token.ThrowIfCancellationRequested();
            movie.Status = "Downloading TMDB-linked official YouTube trailer";
            var arguments = new List<string> {
                "--no-playlist", "--no-warnings", "--no-progress",
                "--retries", "2", "--fragment-retries", "2",
                "-f", "bv*+ba/b", "-S", "res:1080,ext:mp4",
                "--merge-output-format", "mp4", "--recode-video", "mp4",
                "-o", pattern, candidate
            };
            if (!string.IsNullOrWhiteSpace(Settings.FfmpegPath) &&
                File.Exists(Settings.FfmpegPath))
                arguments.InsertRange(0, ["--ffmpeg-location", Settings.FfmpegPath]);
            if (MovieUseBrowserYouTubeLogin)
                arguments.InsertRange(0, ["--cookies-from-browser", "edge"]);
            var download = await MovieRunProcessAsync(downloader, arguments, _cts.Token);
            if (download.Code == 0 && File.Exists(target) &&
                new FileInfo(target).Length > 10_000)
                return target;
            errors.Add(MovieDownloaderError(download.Error));
        }
        var last = errors.LastOrDefault() ?? "No playable trailer could be downloaded.";
        throw new InvalidOperationException(
            "Trailer download unavailable for " + movie.Label + ": " + last +
            ". TMDB trailer lookup succeeded, but YouTube may require account verification. " +
            "You can place an authorized MP4 in the trailer cache folder and press Retry Failed.");
    }

    private static string MovieDownloaderError(string message)
    {
        var lines = (message ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim()).Where(x => x.Contains("ERROR:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (lines.Any(x => x.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase) ||
                           x.Contains("cookies", StringComparison.OrdinalIgnoreCase) ||
                           x.Contains("authentication", StringComparison.OrdinalIgnoreCase)))
            return "YouTube requires sign-in/account verification; the download was not authorized.";
        return (lines.LastOrDefault() ?? (message ?? "").Trim()) is var line
            ? line[^Math.Min(350, line.Length)..]
            : "Unknown yt-dlp error";
    }

    private async Task MovieStartAsync()
    {
        if (MovieProcessing) return;
        MovieProcessing = true;
        MoviePrepareChat();
        try
        {
            var regeneratedIdeas = MovieClipperIdeas.Count == 0;
            if (regeneratedIdeas) await MovieGenerateAsync(); // Retry a failed idea generation too.
            var selected = MovieClipperIdeas.Where(x => x.IsChecked &&
                (!_movieRetryFailedOnly || regeneratedIdeas ||
                 MovieCheckpointItem(x).Stage == "Failed")).ToList();
            if (selected.Count == 0) { MovieStatus = "No pending movies selected. Generate ideas for a new batch."; return; }
            if (!DateTime.TryParseExact(MovieFirstSlot, "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var first) ||
                first <= DateTime.Now.AddMinutes(25) || first.Minute % 5 != 0)
                throw new InvalidDataException("Start time must be at least 25 minutes ahead and on a five-minute increment.");
            if (selected.Count > 20 * 24)
                throw new InvalidDataException("Limit hourly TikTok scheduling to 20 days per batch.");
            if (string.IsNullOrWhiteSpace(MovieSoundFile) || !File.Exists(MovieSoundFile))
                throw new FileNotFoundException(
                    "Select a background-music file. MovieClipper now matches video length to that audio.", MovieSoundFile);
            var original = PublishingQueue.Where(x => x.IsChecked).ToList();
            var finished = new List<PublishQueueItem>();
            foreach (var movie in selected)
            {
                _cts.Token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(movie.PosterPath)) { MovieCheckpointStage(movie, "Failed", "No TMDB poster"); continue; }
                try
                {
                    var saved = MovieCheckpointItem(movie);
                    if (!string.IsNullOrWhiteSpace(saved.OutputPath) && File.Exists(saved.OutputPath) &&
                        PublishingQueue.Any(x => x.VideoPath.Equals(saved.OutputPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        MovieRecordHistory(movie, saved.OutputPath);
                        movie.Status = "Already queued (resumed safely)";
                        continue;
                    }
                    var trailer = File.Exists(saved.TrailerPath) ? saved.TrailerPath : MovieFindFile(MovieSourceFolder, movie);
                    if (trailer is null && MovieAutoDownload)
                    {
                        movie.Status = "Searching and downloading YouTube official trailer";
                        trailer = await MovieDownloadTrailerAsync(movie);
                    }
                    if (trailer is null) { MovieCheckpointStage(movie, "Failed", "Trailer not available"); continue; }
                    saved.TrailerPath = trailer;
                    MovieCheckpointStage(movie, "TrailerReady");
                    movie.Status = "Rendering";
                    var output = !string.IsNullOrWhiteSpace(saved.OutputPath) && File.Exists(saved.OutputPath)
                        ? MovieQueuedItemFromCheckpoint(movie, saved) : await MovieRenderAsync(movie, trailer);
                    saved.OutputPath = output.VideoPath;
                    saved.ThumbnailPath = output.ThumbnailPath;
                    MovieCheckpointStage(movie, "Rendered");
                    // The one idea-generation request already includes scores and SEO.
                    MovieEnsureSeoFallback(movie, saved);
                    output.Title = saved.SeoTitle;
                    output.Description = saved.SeoCaption;
                    output.TikTokCaption = MovieAssembleCaption(saved, movie);
                    output.YouTubeTitle = saved.SeoTitle;
                    output.YouTubeDescription = saved.SeoCaption;
                    MovieCheckpointStage(movie, "MetadataReady");
                    if (PublishingQueue.Any(x => x.VideoPath.Equals(output.VideoPath, StringComparison.OrdinalIgnoreCase)))
                        continue;
                    var next = first.AddMinutes(SelectedPublishIntervalMinutes * finished.Count);
                    output.IsChecked = true;
                    output.IsScheduled = true;
                    output.ScheduleDateText = next.ToString("yyyy-MM-dd HH:mm");
                    output.ScheduledAt = new DateTimeOffset(next);
                    output.MetadataStatus = "Ready";
                    TrackPublishingItem(output);
                    PublishingQueue.Add(output);
                    finished.Add(output);
                    MovieCheckpointStage(movie, "Queued");
                    movie.Status = "Queued " + output.ScheduleDateText;
                    SavePublishingQueue();
                    MovieRecordHistory(movie, output.VideoPath);
                }
                catch (OperationCanceledException) { MovieCheckpointStage(movie, "Stopped"); throw; }
                catch (Exception ex) { MovieCheckpointStage(movie, "Failed", ex.Message); Log("MovieClipper: " + movie.Label + ": " + ex.Message); }
            }
            Raise(nameof(AllPublishItemsChecked));
            RefreshPublishingCalendar();
            RefreshVideoLibrary();
            MovieStatus = $"Rendered {finished.Count}/{selected.Count} movies, matched to selected background-music length. " +
                "MP4 files: " + MovieExportFolder + ". Click Open Exported Videos. " +
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
        catch (OperationCanceledException) { MovieStatus = "Stopped. Resume from saved checkpoints."; MovieSaveCheckpoint(); throw; }
        catch (Exception ex) { MovieStatus = "MovieClipper: " + ex.Message; Log(MovieStatus); }
        finally { MovieProcessing = false; MovieSaveCheckpoint(); }
    }


    private static string MovieNumber(double n) => n.ToString("0.###", CultureInfo.InvariantCulture);

    private sealed record MovieReviewScores(double Plot, double Acting, double Pacing,
        double Cinematography, double Writing, double Ending)
    {
        public double[] Parts => [Plot, Acting, Pacing, Cinematography, Writing, Ending];
        public double Overall => Math.Round(Parts.Average(), 1);
    }

    private static MovieReviewScores MovieFallbackScores(MovieClipperEntry movie)
    {
        // Stable fallback if the ChatGPT scoring step is unavailable. Never label these TMDB.
        var baseScore = movie.VoteCount > 0 ? movie.VoteAverage : 7.0;
        double S(int i) => Math.Round(Math.Clamp(
            baseScore + ((movie.TmdbId * (i + 7L) + 19L * i) % 15 - 7) * 0.1,
            3.0, 9.8), 1);
        return new MovieReviewScores(S(0), S(1), S(2), S(3), S(4), S(5));
    }

    private Task<MovieReviewScores> MovieGenerateScoresAsync(MovieClipperEntry movie)
    {
        var checkpoint = MovieCheckpointItem(movie);
        if (checkpoint.Scores is { Length: 6 } values &&
            values.All(v => double.IsFinite(v) && v >= 0 && v <= 10))
            return Task.FromResult(new MovieReviewScores(values[0], values[1], values[2],
                values[3], values[4], values[5]));
        // Legacy checkpoints: no second ChatGPT generation.
        var fallback = MovieFallbackScores(movie);
        checkpoint.Scores = fallback.Parts;
        checkpoint.ScoringMethod = "Deterministic fallback for legacy idea";
        MovieSaveCheckpoint();
        return Task.FromResult(fallback);
    }

    private async Task<(double Seconds, bool HasAudio)> MovieProbeAsync(string path)
    {
        var ffprobe = !string.IsNullOrWhiteSpace(Settings.FfprobePath) &&
            File.Exists(Settings.FfprobePath) ? Settings.FfprobePath : "ffprobe";
        var result = await MovieRunProcessAsync(ffprobe,
            ["-v", "quiet", "-show_format", "-show_streams", "-of", "json", path], _cts.Token);
        if (result.Code != 0) throw new InvalidDataException("ffprobe could not inspect the source video.");
        using var doc = JsonDocument.Parse(result.Output);
        var duration = MovieJson(doc.RootElement.GetProperty("format"), "duration");
        if (!double.TryParse(duration, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || seconds < 5) throw new InvalidDataException("Trailer is too short for a movie montage.");
        var audioStreams = doc.RootElement.GetProperty("streams").EnumerateArray()
            .Where(x => MovieJson(x, "codec_type") == "audio").ToList();
        if (audioStreams.Count > 0 &&
            double.TryParse(MovieJson(audioStreams[0], "duration"), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var streamSeconds) && streamSeconds > 0)
            seconds = streamSeconds;
        return (seconds, audioStreams.Count > 0);
    }

    private async Task<List<double>> MovieSceneCutsAsync(string ffmpeg, string path, double duration)
    {
        var scan = await MovieRunProcessAsync(ffmpeg,
            ["-hide_banner", "-nostats", "-i", path, "-vf",
             "scale=320:-2,select=gt(scene\\,0.20),showinfo",
             "-an", "-f", "null", "-"], _cts.Token);
        var cuts = new SortedSet<double>();
        foreach (System.Text.RegularExpressions.Match m in
            System.Text.RegularExpressions.Regex.Matches(scan.Error, @"pts_time:\s*(\d+(?:\.\d+)?)"))
        {
            if (double.TryParse(m.Groups[1].Value, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var t) && t > 1 && t < duration - 3)
                cuts.Add(t);
        }
        return cuts.ToList();
    }

    // Decode the chosen soundtrack to 8kHz mono PCM and find sudden increases
    // in short-window energy. Snap shot changes to those onsets rather than to
    // a constant interval. If no soundtrack is supplied, use trailer audio.
    private async Task<List<double>> MovieBeatTimelineAsync(string ffmpeg, string sound, double totalSeconds)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var a in new[] { "-hide_banner", "-loglevel", "error",
            "-i", sound, "-t", MovieNumber(totalSeconds), "-vn", "-ac", "1", "-ar", "8000",
            "-f", "s16le", "pipe:1" }) start.ArgumentList.Add(a);
        using var proc = Process.Start(start) ?? throw new InvalidOperationException("Cannot decode soundtrack.");
        using var stop = _cts.Token.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
            catch (Exception) { }
        });
        using var stream = new MemoryStream();
        var errors = proc.StandardError.ReadToEndAsync(_cts.Token);
        await proc.StandardOutput.BaseStream.CopyToAsync(stream, _cts.Token);
        await proc.WaitForExitAsync(_cts.Token);
        var bytes = stream.ToArray();
        if (proc.ExitCode != 0 || bytes.Length < 16000)
            throw new InvalidDataException("Soundtrack analysis failed: " + await errors);
        const int window = 400; // 50 ms
        var levels = new List<double>();
        for (var offset = 0; offset + window * 2 <= bytes.Length; offset += window * 2)
        {
            double sum = 0;
            for (var j = 0; j < window; j++)
            {
                var v = BitConverter.ToInt16(bytes, offset + j * 2) / 32768.0;
                sum += v * v;
            }
            levels.Add(Math.Sqrt(sum / window));
        }
        var peaks = new List<(double Time, double Strength)>();
        for (var i = 3; i < levels.Count - 2; i++)
        {
            var rise = levels[i] - (levels[i - 2] + levels[i - 3]) * 0.5;
            if (rise > 0.012 && rise > levels[i + 1] - levels[i - 1])
                peaks.Add((i * 0.05, rise));
        }
        // Aim for ~15 different shots but allow music to shift boundaries.
        var timeline = new List<double> { 0 };
        for (var target = 2.0; target < totalSeconds - 1.1; target += 2.0)
        {
            var nearest = peaks.Where(p => Math.Abs(p.Time - target) <= 0.60)
                .OrderByDescending(p => p.Strength).FirstOrDefault();
            var position = nearest.Time > 0 ? nearest.Time : target;
            position = Math.Clamp(position, timeline[^1] + 1.25, Math.Min(target + 0.70, totalSeconds - 0.65));
            if (position < totalSeconds - 0.65 && position - timeline[^1] >= 1.25)
                timeline.Add(Math.Round(position, 2));
        }
        timeline.Add(totalSeconds);
        return timeline;
    }

    private async Task<string?> MovieDownloadLogoAsync(MovieClipperEntry movie, string destination)
    {
        try
        {
            using var images = await MovieTmdbAsync(
                "movie/" + movie.TmdbId + "/images?include_image_language=en,null");
            if (!images.RootElement.TryGetProperty("logos", out var logos) ||
                logos.ValueKind != JsonValueKind.Array) return null;
            var found = logos.EnumerateArray().Where(x =>
                MovieJson(x, "file_path").EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(x => MovieJson(x, "iso_639_1") == "en")
                .ThenByDescending(x => x.TryGetProperty("vote_average", out var n) ? n.GetDouble() : 0)
                .FirstOrDefault();
            if (found.ValueKind != JsonValueKind.Object) return null;
            using var response = await _movieHttp.GetAsync(
                "https://image.tmdb.org/t/p/original" + MovieJson(found, "file_path"), _cts.Token);
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(destination);
            await response.Content.CopyToAsync(file, _cts.Token);
            return destination;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { Log("TMDB movie title logo unavailable: " + ex.Message); return null; }
    }

    private async Task<PublishQueueItem> MovieRenderAsync(MovieClipperEntry movie, string trailer)
    {
        if (string.IsNullOrWhiteSpace(MovieSoundFile) || !File.Exists(MovieSoundFile))
            throw new InvalidOperationException("Choose a background-music file first. The movie duration will match that audio file.");
        var musicInfo = await MovieProbeAsync(MovieSoundFile);
        var totalSeconds = musicInfo.Seconds;
        if (!musicInfo.HasAudio || totalSeconds is < 8 or > 120)
            throw new InvalidDataException("The selected soundtrack needs an audio stream lasting 8–120 seconds.");
        var frameCount = Math.Max(1, (int)Math.Round(totalSeconds * 30, MidpointRounding.AwayFromZero));
        var videoSeconds = frameCount / 30.0;
        var folder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZeroVideo", "MovieClipper", movie.TmdbId + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
        Directory.CreateDirectory(folder);
        Directory.CreateDirectory(MovieExportFolder);
        var poster = Path.Combine(folder, "thumbnail.jpg");
        var output = Path.Combine(MovieExportFolder,
            movie.TmdbId + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmssfff") + ".mp4");
        using (var response = await _movieHttp.GetAsync(
            "https://image.tmdb.org/t/p/original" + movie.PosterPath, _cts.Token))
        {
            response.EnsureSuccessStatusCode();
            await using var file = File.Create(poster);
            await response.Content.CopyToAsync(file, _cts.Token);
        }
        var ffmpeg = !string.IsNullOrWhiteSpace(Settings.FfmpegPath) &&
            File.Exists(Settings.FfmpegPath) ? Settings.FfmpegPath : "ffmpeg";
        var source = await MovieProbeAsync(trailer);
        var score = await MovieGenerateScoresAsync(movie);
        movie.Status = "Analyzing musical beats and scene changes";
        var soundtrack = MovieSoundFile;
        var beatTimeline = await MovieBeatTimelineAsync(ffmpeg, soundtrack, videoSeconds);
        var changes = await MovieSceneCutsAsync(ffmpeg, trailer, source.Seconds);
        var count = beatTimeline.Count - 1;
        var starts = new List<double>();
        // Each segment comes from a different section of the trailer, using
        // nearby genuine scene boundaries instead of sequential playback.
        for (var i = 0; i < count; i++)
        {
            var duration = beatTimeline[i + 1] - beatTimeline[i];
            var limit = Math.Max(0, source.Seconds - duration - 0.1);
            var target = limit * (i + 0.35) / count;
            var candidate = changes.Where(x => Math.Abs(x - target) < Math.Max(3, source.Seconds / (count * 3.0)))
                .OrderBy(x => Math.Abs(x - target)).FirstOrDefault();
            starts.Add(Math.Clamp(candidate > 0 ? candidate : target, 0, limit));
        }
        await File.WriteAllTextAsync(Path.Combine(folder, "edit-plan.json"),
            JsonSerializer.Serialize(new
            {
                movie = movie.Label, trailer, beatTimeline, sourceShots = starts,
                sceneChanges = changes.Count, scores = score.Parts, overall = score.Overall,
                scoring = "AI-generated subjective category scores; TMDB is an audience aggregate"
            }), _cts.Token);
        // Poster carries the authentic title treatment; no second logo needed.
        var args = new List<string> { "-y" };
        for (var i = 0; i < count; i++)
            args.AddRange(["-ss", MovieNumber(starts[i]),
                "-t", MovieNumber(beatTimeline[i + 1] - beatTimeline[i]), "-i", trailer]);
        // Introduce the actual film using its TMDB poster/title artwork, not a generic label.
        // The selected soundtrack remains the only audio input, with its original timing.
        var posterIndex = count;
        args.AddRange(["-loop", "1", "-framerate", "30", "-t", MovieNumber(videoSeconds), "-i", poster]);
        var audioIndex = count + 1;
        var customMusic = true;
        args.AddRange(["-i", soundtrack]);
        // Film presentation: preserve the complete widescreen image. A softened
        // copy of that same scene fills the vertical space above and below.
        // Never crop source movie footage to 9:16.
        var filters = new List<string>();
        for (var i = 0; i < count; i++)
        {
            var duration = MovieNumber(beatTimeline[i + 1] - beatTimeline[i]);
            filters.Add("[" + i + ":v]fps=30,split=2[bgraw" + i + "][fgraw" + i + "]");
            filters.Add("[bgraw" + i + "]scale=540:960:force_original_aspect_ratio=increase," +
                "crop=540:960,boxblur=18:2,scale=1080:1920,setsar=1[bg" + i + "]");
            filters.Add("[fgraw" + i + "]scale=1080:608:force_original_aspect_ratio=decrease," +
                "format=rgba,setsar=1[fg" + i + "]");
            filters.Add("[bg" + i + "][fg" + i + "]overlay=x=(W-w)/2:y=(H-h)/2," +
                "trim=duration=" + duration + ",setpts=PTS-STARTPTS[v" + i + "]");
        }
        filters.Add(string.Concat(Enumerable.Range(0, count).Select(i => "[v" + i + "]")) +
            "concat=n=" + count + ":v=1:a=0[montage]");
        // Match the audio duration, rounded to the nearest 30-fps video frame.
        filters.Add("[montage]tpad=stop_mode=clone:stop_duration=1," +
            "trim=duration=" + MovieNumber(videoSeconds) + ",setpts=PTS-STARTPTS[exact]");
        // Visually distinct movie-specific introduction using the movie's own poster.
        // Poster occupies the first seconds; subsequent shots still follow the
        // existing soundtrack/scene-cut timeline without replacing the audio.
        const double titleEnd = 2.75;
        // Original movie poster is shown only once, with no duplicated logo.
        // Preserve the artwork aspect ratio, with a blurred version behind it.
        filters.Add("[" + posterIndex + ":v]fps=30,split=2[posterblur][posterfront]");
        filters.Add("[posterblur]scale=540:960:force_original_aspect_ratio=increase," +
            "crop=540:960,boxblur=18:2,scale=1080:1920[posterbg]");
        filters.Add("[posterfront]scale=1020:1600:force_original_aspect_ratio=decrease," +
            "format=rgba[posterfg]");
        filters.Add("[posterbg][posterfg]overlay=x=(W-w)/2:y=(H-h)/2[posterart]");
        filters.Add("[exact][posterart]overlay=x=0:y=0:enable='between(t,0," +
            MovieNumber(titleEnd) + ")'[opening]");
        var baseInput = "[opening]";
        // The poster already includes the movie title. Do not draw another TMDB
        // logo or fallback text on top: the duplicate made the intro unreadable.
        // Keep the original poster with a single visible title treatment.
        var captions = new List<string>();
        // Typography-only treatment: no boxes, translucent cards or ribbons.
        // Condensed Impact headline, crisp white scores, soft shadow, gold stars.
        // Positioned in the blurred lower area, outside the 16:9 movie frame.
        captions.Add("drawtext=font='Impact':text='FILMSIGNAL':fontsize=38:" +
            "fontcolor=white:shadowcolor=black@0.85:shadowx=2:shadowy=3:" +
            "x=80:y=150:enable='between(t,0.2," +
            MovieNumber(titleEnd - 0.1) + ")'");
        var labels = new[] { "PLOT", "ACTING", "PACING", "CINEMATOGRAPHY",
            "WRITING", "ENDING", "OVERALL" };
        var values = score.Parts.Concat([score.Overall]).ToArray();
        var available = Math.Max(0.5, videoSeconds - titleEnd);
        for (var i = 0; i < labels.Length; i++)
        {
            var from = titleEnd + available * i / labels.Length;
            var to = i < labels.Length - 1
                ? titleEnd + available * (i + 1) / labels.Length - 0.05
                : videoSeconds - 0.05;
            var isFinal = i == labels.Length - 1;
            var fromText = MovieNumber(from);
            var toText = MovieNumber(to);
            var show = "enable='between(t," + fromText + "," + toText + ")'";
            var delayed = "enable='between(t," +
                MovieNumber(from + Math.Min(0.22, (to - from) * 0.12)) +
                "," + toText + ")'";
            var scoreText = values[i].ToString("0.0", CultureInfo.InvariantCulture) + "/10";
            var stars = new string('★', Math.Clamp(
                (int)Math.Round(values[i] / 2.0, MidpointRounding.AwayFromZero), 0, 5));
            if (!isFinal)
            {
                captions.Add("drawtext=font='Impact':text='" + labels[i] +
                    "':fontsize=68:fontcolor=white:" +
                    "shadowcolor=black@0.95:shadowx=3:shadowy=4:" +
                    "x=(w-text_w)/2:y=1335:" + show);
                captions.Add("drawtext=font='Impact':text='" + scoreText +
                    "':fontsize=100:fontcolor=white:" +
                    "shadowcolor=black@0.95:shadowx=3:shadowy=4:" +
                    "x=(w-text_w)/2:y=1425:" + delayed);
                captions.Add("drawtext=font='Segoe UI Symbol':text='" + stars +
                    "':fontsize=65:fontcolor=0xE5C16E:" +
                    "shadowcolor=black@0.8:shadowx=2:shadowy=3:" +
                    "x=(w-text_w)/2:y=1560:" + delayed);
            }
            else
            {
                captions.Add("drawtext=font='Impact':text='FILMSIGNAL VERDICT':" +
                    "fontsize=65:fontcolor=white:" +
                    "shadowcolor=black@0.95:shadowx=3:shadowy=4:" +
                    "x=(w-text_w)/2:y=1305:" + show);
                captions.Add("drawtext=font='Impact':text='" + scoreText +
                    "':fontsize=140:fontcolor=white:" +
                    "shadowcolor=black@0.95:shadowx=4:shadowy=5:" +
                    "x=(w-text_w)/2:y=1400:" + delayed);
                captions.Add("drawtext=text='" + stars +
                    "':fontsize=78:fontcolor=0xE5C16E:" +
                    "shadowcolor=black@0.8:shadowx=2:shadowy=3:" +
                    "x=(w-text_w)/2:y=1565:" + delayed);
                if (movie.VoteCount > 0)
                    captions.Add("drawtext=font='Impact':text='TMDB AUDIENCE " +
                        movie.VoteAverage.ToString("0.0", CultureInfo.InvariantCulture) +
                        "/10':fontsize=35:fontcolor=white@0.85:" +
                        "shadowcolor=black@0.9:shadowx=2:shadowy=2:" +
                        "x=(w-text_w)/2:y=1685:" + show);
            }
        }
        filters.Add(baseInput + string.Join(",", captions) + "[v]");

        if (customMusic || source.HasAudio)
            filters.Add("[" + audioIndex + ":a]atrim=duration=" + MovieNumber(totalSeconds) +
                ",asetpts=PTS-STARTPTS,volume=" + MovieNumber(MovieMusicLevel) + "[a]");
        else filters.Add("anullsrc=r=48000:cl=stereo,atrim=duration=" + MovieNumber(totalSeconds) + "[a]");

        args.AddRange(["-filter_complex", string.Join(";", filters), "-map", "[v]",
            "-map", "[a]", "-t", MovieNumber(totalSeconds), "-frames:v", frameCount.ToString(CultureInfo.InvariantCulture), "-c:v", "libx264", "-preset", "veryfast",
            "-crf", "21", "-c:a", "aac", "-b:a", "160k", "-pix_fmt", "yuv420p",
            "-movflags", "+faststart", output]);
        movie.Status = "Beat-sync editing " + count + " distinct shots";
        var result = await MovieRunProcessAsync(ffmpeg, args, _cts.Token, folder);
        if (result.Code != 0)
            throw new InvalidOperationException("FFmpeg: " +
                result.Error[^Math.Min(900, result.Error.Length)..]);
        if (!File.Exists(output) || new FileInfo(output).Length < 10_000)
            throw new IOException("No finished video was created.");
        MovieStatus = "Exported " + output;
        return new PublishQueueItem
        {
            VideoPath = output, ThumbnailPath = poster, Title = movie.Label,
            Description = movie.Overview,
            TikTokCaption = movie.Label + " - quick movie review. " +
                "Our scores: Plot " + MovieNumber(score.Plot) + ", Acting " +
                MovieNumber(score.Acting) + ", Pacing " + MovieNumber(score.Pacing) +
                ", Cinematography " + MovieNumber(score.Cinematography) +
                ", Writing " + MovieNumber(score.Writing) + ", Ending " +
                MovieNumber(score.Ending) + ". Overall " + MovieNumber(score.Overall) +
                "/10. #MovieTok #MovieReview #FilmTok",
            YouTubeTitle = movie.Label + " #Shorts",
            YouTubeDescription = movie.Overview
        };
    }
}
