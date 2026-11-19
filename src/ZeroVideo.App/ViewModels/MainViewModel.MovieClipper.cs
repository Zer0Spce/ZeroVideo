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
    private string _movieStatus = "Ready: ChatGPT ideas → TMDB artwork → automatic YouTube trailer → 30-second edit → TikTok queue.";
    private string _movieFirstSlot = DateTime.Now.AddHours(2).ToString("yyyy-MM-dd HH:00", CultureInfo.InvariantCulture);
    private bool _moviePublish = true;
    private bool _movieKeepDialogue;
    private bool _movieAutoDownload = true;
    private bool _movieProcessing;
    private double _movieMusicLevel = 0.85;
    private string _movieToken = "";
    // Duration is read from the selected soundtrack at render time.
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
    public bool MovieAutoDownload { get => _movieAutoDownload; set => Set(ref _movieAutoDownload, value); }
    public bool MovieProcessing { get => _movieProcessing; private set => Set(ref _movieProcessing, value); }
    public double MovieMusicLevel { get => _movieMusicLevel; set => Set(ref _movieMusicLevel, Math.Clamp(value, 0, 1)); }
    public ICommand MovieGenerateCommand { get; private set; } = null!;
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
        MovieStartCommand = new AsyncCommand(() => RunManagedJobAsync("MovieClipper: 30s batch", MovieStartAsync));
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
            if (string.IsNullOrWhiteSpace(_movieToken))
                throw new InvalidOperationException(
                    "TMDB credential not loaded. Launch the private portable package or install movieclipper.local.json in %LOCALAPPDATA%\\ZeroVideo.");
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
        var searchText = movie.Label + " official movie trailer";
        var search = await MovieRunProcessAsync(downloader,
            ["--no-playlist", "--dump-single-json", "--skip-download", "--no-warnings",
             "ytsearch8:" + searchText], _cts.Token);
        if (search.Code != 0)
            throw new InvalidOperationException("YouTube search failed: " +
                search.Error[^Math.Min(300, search.Error.Length)..]);
        using var listing = JsonDocument.Parse(search.Output);
        if (!listing.RootElement.TryGetProperty("entries", out var entries) ||
            entries.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("YouTube did not return trailer search results.");
        var candidates = new List<(string Url, int Score)>();
        foreach (var item in entries.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var title = MovieJson(item, "title");
            var channel = MovieJson(item, "channel");
            var url = MovieJson(item, "webpage_url");
            if (string.IsNullOrWhiteSpace(url))
            {
                var id = MovieJson(item, "id");
                if (id.Length == 11) url = "https://www.youtube.com/watch?v=" + id;
            }
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                !new[] { "youtube.com", "www.youtube.com", "youtu.be" }.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
                continue;
            var duration = item.TryGetProperty("duration", out var durationNode) &&
                durationNode.ValueKind == JsonValueKind.Number ? durationNode.GetDouble() : 0;
            if (duration is < 30 or > 360) continue;
            var lower = title.ToLowerInvariant();
            if (!lower.Contains("trailer", StringComparison.OrdinalIgnoreCase) ||
                lower.Contains("reaction") || lower.Contains("fan made") ||
                lower.Contains("fanmade") || lower.Contains("breakdown") ||
                lower.Contains("review") || lower.Contains("concept trailer"))
                continue;
            var expected = MovieClean(movie.Title);
            if (!MovieClean(title).Contains(expected, StringComparison.Ordinal)) continue;
            var score = 0;
            if (lower.Contains("official trailer")) score += 15;
            if (lower.Contains(movie.Year)) score += 2;
            var source = channel.ToLowerInvariant();
            if (new[] { "warner bros", "universal pictures", "paramount pictures",
                "sony pictures", "a24", "lionsgate", "20th century studios",
                "netflix", "disney", "focus features", "neon", "searchlight pictures",
                "movieclips trailers", "filmselect trailer" }.Any(source.Contains))
                score += 10;
            if (duration >= 55 && duration <= 190) score += 3;
            candidates.Add((url, score));
        }
        if (candidates.Count == 0)
            throw new InvalidDataException("No matching official movie trailer found on YouTube.");
        var chosen = candidates.OrderByDescending(x => x.Score).First().Url;
        var target = Path.Combine(MovieSourceFolder, movie.TmdbId + ".mp4");
        var pattern = Path.Combine(MovieSourceFolder, movie.TmdbId + ".%(ext)s");
        var arguments = new List<string> {
            "--no-playlist", "--no-warnings", "--no-progress",
            "--retries", "3", "--fragment-retries", "3",
            "-f", "bv*+ba/b", "-S", "res:1080,ext:mp4",
            "--merge-output-format", "mp4", "--recode-video", "mp4",
            "-o", pattern, chosen
        };
        if (!string.IsNullOrWhiteSpace(Settings.FfmpegPath) &&
            File.Exists(Settings.FfmpegPath))
            arguments.InsertRange(0, ["--ffmpeg-location", Settings.FfmpegPath]);
        var download = await MovieRunProcessAsync(downloader, arguments, _cts.Token);
        if (download.Code != 0)
            throw new InvalidOperationException("YouTube trailer download failed: " +
                download.Error[^Math.Min(450, download.Error.Length)..]);
        if (!File.Exists(target) || new FileInfo(target).Length < 10_000)
            throw new IOException("YouTube download did not create a usable MP4.");
        return target;
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
                if (string.IsNullOrWhiteSpace(movie.PosterPath)) { movie.Status = "No TMDB poster"; continue; }
                try
                {
                    var trailer = MovieFindFile(MovieSourceFolder, movie);
                    if (trailer is null && MovieAutoDownload)
                    {
                        movie.Status = "Searching and downloading YouTube official trailer";
                        trailer = await MovieDownloadTrailerAsync(movie);
                    }
                    if (trailer is null) { movie.Status = "Trailer not available"; continue; }
                    movie.Status = "Rendering";
                    var output = await MovieRenderAsync(movie, trailer);
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
                    movie.Status = "Queued " + output.ScheduleDateText;
                    SavePublishingQueue();
                }
                catch (Exception ex) { movie.Status = "Failed: " + ex.Message; Log("MovieClipper: " + movie.Label + ": " + ex.Message); }
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
        catch (Exception ex) { MovieStatus = "MovieClipper: " + ex.Message; Log(MovieStatus); }
        finally { MovieProcessing = false; }
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

    private async Task<MovieReviewScores> MovieGenerateScoresAsync(MovieClipperEntry movie)
    {
        var fallback = MovieFallbackScores(movie);
        try
        {
            movie.Status = "ChatGPT is generating six review scores";
            var prompt = "Generate film-review style numeric ratings for this movie. " +
                "These are YOUR subjective AI review scores, NOT TMDB category scores or quotations from critics. " +
                "Use public knowledge of the actual film when available, and this synopsis. " +
                "If you are uncertain about a story detail, do not invent it. " +
                "Rate each dimension 0.0 to 10.0 using exactly one decimal: " +
                "PLOT, ACTING, PACING, CINEMATOGRAPHY, WRITING, ENDING. " +
                "MOVIE: " + movie.Label + "\nSYNOPSIS: " + movie.Overview + "\n" +
                "TMDB audience vote average (calibration only): " + MovieNumber(movie.VoteAverage) + "/10. " +
                "Return ONLY a JSON object inside a fenced json code block. " +
                "Schema: {\"plot\":8.0,\"acting\":8.0,\"pacing\":8.0," +
                "\"cinematography\":8.0,\"writing\":8.0,\"ending\":8.0}.";
            var raw = await _chat.GenerateTextAsync(
                new VideoProject { Title = "MovieClipper scores: " + movie.Label, Topic = movie.Label },
                Settings, prompt, Log, _cts.Token);
            var open = raw.IndexOf('{');
            var close = raw.LastIndexOf('}');
            if (open < 0 || close <= open) throw new InvalidDataException("No scoring JSON.");
            using var doc = JsonDocument.Parse(raw.Substring(open, close - open + 1));
            double N(string name)
            {
                if (!doc.RootElement.TryGetProperty(name, out var n) ||
                    n.ValueKind != JsonValueKind.Number) throw new InvalidDataException("Missing " + name);
                var v = n.GetDouble();
                if (!double.IsFinite(v) || v < 0 || v > 10)
                    throw new InvalidDataException("Score out of range: " + name);
                return Math.Round(v, 1);
            }
            return new MovieReviewScores(N("plot"), N("acting"), N("pacing"),
                N("cinematography"), N("writing"), N("ending"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log("MovieClipper scoring fallback for " + movie.Label + ": " + ex.Message);
            return fallback;
        }
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
            || seconds < 5) throw new InvalidDataException("Trailer is too short to assemble 30 seconds.");
        var audio = doc.RootElement.GetProperty("streams").EnumerateArray()
            .Any(x => MovieJson(x, "codec_type") == "audio");
        return (seconds, audio);
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
        var logo = await MovieDownloadLogoAsync(movie, Path.Combine(folder, "title-logo.png"));
        var args = new List<string> { "-y" };
        for (var i = 0; i < count; i++)
            args.AddRange(["-ss", MovieNumber(starts[i]),
                "-t", MovieNumber(beatTimeline[i + 1] - beatTimeline[i]), "-i", trailer]);
        var logoIndex = count;
        if (logo is not null)
            args.AddRange(["-loop", "1", "-framerate", "30", "-t", MovieNumber(videoSeconds), "-i", logo]);
        var audioIndex = count + (logo is null ? 0 : 1);
        var customMusic = true;
        args.AddRange(["-i", soundtrack]);
        const string frame = "scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1,fps=30,format=yuv420p";
        var filters = new List<string>();
        for (var i = 0; i < count; i++)
            filters.Add("[" + i + ":v]" + frame + ",trim=duration=" +
                MovieNumber(beatTimeline[i + 1] - beatTimeline[i]) +
                ",setpts=PTS-STARTPTS[v" + i + "]");
        filters.Add(string.Concat(Enumerable.Range(0, count).Select(i => "[v" + i + "]")) +
            "concat=n=" + count + ":v=1:a=0[montage]");
        // Lock to exactly 30 seconds even if individual shot lengths round to video frames.
        filters.Add("[montage]tpad=stop_mode=clone:stop_duration=1," +
            "trim=duration=" + MovieNumber(videoSeconds) + ",setpts=PTS-STARTPTS[exact]");
        var baseInput = "[exact]";
        if (logo is not null)
        {
            filters.Add("[" + logoIndex + ":v]scale=820:500:force_original_aspect_ratio=decrease," +
                "format=rgba,fade=t=in:st=2.1:d=0.25:alpha=1,fade=t=out:st=5.0:d=0.3:alpha=1[logo]");
            filters.Add("[exact][logo]overlay=x=(W-w)/2:y=(H-h)/2:" +
                "enable='between(t,2.1,5.3)'[art]");
            baseInput = "[art]";
        }
        else
        {
            await File.WriteAllTextAsync(Path.Combine(folder, "movie-title.txt"), movie.Title, _cts.Token);
        }
        var captions = new List<string>();
        captions.Add("drawtext=text='MOVIE REVIEW':fontsize=90:fontcolor=white:" +
            "borderw=5:bordercolor=black:x=(w-text_w)/2:y=(h-text_h)/2:" +
            "enable='between(t,0,2.0)'");
        if (logo is null)
            captions.Add("drawtext=textfile=movie-title.txt:fontsize=82:fontcolor=white:" +
                "borderw=5:bordercolor=black:x=(w-text_w)/2:y=(h-text_h)/2:" +
                "enable='between(t,2.1,5.3)'");
        var labels = new[] { "PLOT", "ACTING", "PACING", "CINEMATOGRAPHY",
            "WRITING", "ENDING", "OVERALL" };
        var values = score.Parts.Concat([score.Overall]).ToArray();
        var froms = new[] { 5.3, 8.6, 11.9, 15.2, 18.5, 21.8, 25.1 }
            .Select(x => x / 30.0 * videoSeconds).ToArray();
        for (var i = 0; i < labels.Length; i++)
        {
            var from = froms[i];
            var to = i < labels.Length - 1 ? froms[i + 1] - 0.05 : videoSeconds - 0.1;
            var labelSize = labels[i] == "CINEMATOGRAPHY" ? 69 : 100;
            captions.Add("drawtext=text='" + labels[i] +
                "':fontsize=" + labelSize + ":fontcolor=white:borderw=5:bordercolor=black:" +
                "x=(w-text_w)/2:y=(h-text_h)/2-48:" +
                "enable='between(t," + MovieNumber(from) + "," + MovieNumber(to) + ")'");
            captions.Add("drawtext=text='" + values[i].ToString("0.0", CultureInfo.InvariantCulture) +
                "/10':fontsize=76:fontcolor=white:borderw=4:bordercolor=black:" +
                "x=(w-text_w)/2:y=(h-text_h)/2+75:" +
                "enable='between(t," + MovieNumber(from + Math.Min(0.95, (to - from) * 0.4)) + "," + MovieNumber(to) + ")'");
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
