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
        string executable, IEnumerable<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
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


    private static string MovieNumber(double n) => n.ToString("0.###", CultureInfo.InvariantCulture);

    private static string MovieScore(MovieClipperEntry movie, int category)
    {
        // Reproducible editorial estimate, explicitly distinguished from the sourced TMDB score.
        var baseline = movie.VoteCount > 0 ? movie.VoteAverage : 6.5;
        var jitter = ((movie.TmdbId * (category + 3L) + category * 29L) % 15 - 7) * 0.1;
        return Math.Clamp(baseline + jitter, 2.0, 9.8).ToString("0.0", CultureInfo.InvariantCulture) + "/10";
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

    private async Task<PublishQueueItem> MovieRenderAsync(MovieClipperEntry movie, string trailer)
    {
        const double intro = 0.75;
        const int shots = 10;
        var shotDuration = (30.0 - intro) / shots;
        var internalFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ZeroVideo", "MovieClipper", movie.TmdbId + "-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff"));
        Directory.CreateDirectory(internalFolder);
        Directory.CreateDirectory(MovieExportFolder);
        var poster = Path.Combine(internalFolder, "poster.jpg");
        var output = Path.Combine(MovieExportFolder,
            movie.TmdbId + "_" + DateTime.Now.ToString("yyyyMMdd_HHmmssfff") + ".mp4");
        using (var response = await _movieHttp.GetAsync(
            "https://image.tmdb.org/t/p/original" + movie.PosterPath, _cts.Token))
        {
            response.EnsureSuccessStatusCode();
            await using var stream = File.Create(poster);
            await response.Content.CopyToAsync(stream, _cts.Token);
        }
        var ffmpeg = !string.IsNullOrWhiteSpace(Settings.FfmpegPath) &&
            File.Exists(Settings.FfmpegPath) ? Settings.FfmpegPath : "ffmpeg";
        var info = await MovieProbeAsync(trailer);
        movie.Status = "Finding trailer scene changes";
        var changes = await MovieSceneCutsAsync(ffmpeg, trailer, info.Seconds);
        var starts = new List<double>();
        var limit = Math.Max(0, info.Seconds - shotDuration - 0.1);
        for (var i = 0; i < shots; i++)
        {
            var target = limit * (i + 0.35) / shots;
            var maxDistance = Math.Max(2.5, info.Seconds / (shots * 3.0));
            var cut = changes.Where(x => Math.Abs(x - target) < maxDistance)
                .OrderBy(x => Math.Abs(x - target)).FirstOrDefault();
            starts.Add(Math.Clamp(cut > 0 ? cut : target, 0, limit));
        }
        await File.WriteAllTextAsync(Path.Combine(internalFolder, "edit-plan.json"),
            JsonSerializer.Serialize(new { movie = movie.Label, trailer, starts, detected_cuts = changes.Count,
                score_type = "editorial_estimate",
                scores = Enumerable.Range(0, 4).Select(i => MovieScore(movie, i)).ToArray() }),
            _cts.Token);

        // Ten excerpts are spread through the complete trailer, not its first 28 seconds.
        // Crop to vertical without distortion. No background boxes behind the central captions.
        var args = new List<string> { "-y", "-loop", "1", "-framerate", "30",
            "-t", MovieNumber(intro), "-i", poster };
        foreach (var start in starts)
            args.AddRange(["-ss", MovieNumber(start), "-t", MovieNumber(shotDuration),
                "-i", trailer]);
        var customMusic = File.Exists(MovieSoundFile);
        // A continuous audio bed begins at 0s, including during the opening poster.
        // Without custom music, use the original trailer audio where possible.
        if (customMusic)
            args.AddRange(["-stream_loop", "-1", "-i", MovieSoundFile]);
        else if (info.HasAudio)
            args.AddRange(["-stream_loop", "-1", "-i", trailer]);
        var audioIndex = shots + 1;
        const string portrait = "scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1,fps=30,format=yuv420p";
        var filters = new List<string> {
            "[0:v]" + portrait + ",trim=duration=" + MovieNumber(intro) + ",setpts=PTS-STARTPTS[v0]"
        };
        for (var i = 0; i < shots; i++)
            filters.Add("[" + (i + 1) + ":v]" + portrait + ",trim=duration=" +
                MovieNumber(shotDuration) + ",setpts=PTS-STARTPTS[v" + (i + 1) + "]");
        var inputs = string.Concat(Enumerable.Range(0, shots + 1).Select(i => "[v" + i + "]"));
        filters.Add(inputs + "concat=n=" + (shots + 1) + ":v=1:a=0[base]");
        var captions = "";
        var labels = new[] { "PLOT", "ACTING", "CINEMATOGRAPHY", "ENDING" };
        for (var i = 0; i < labels.Length; i++)
        {
            var from = intro + i * 6.9;
            var to = Math.Min(29, from + 6.9);
            var when = ":enable='between(t," + MovieNumber(from) + "," + MovieNumber(to) + ")'";
            captions += ",drawtext=text='" + labels[i] +
                "':fontsize=70:fontcolor=white:borderw=4:bordercolor=black:" +
                "x=(w-text_w)/2:y=(h-text_h)/2-115" + when;
            captions += ",drawtext=text='" + MovieScore(movie, i) +
                "':fontsize=108:fontcolor=0xFFE17C:borderw=5:bordercolor=black:" +
                "x=(w-text_w)/2:y=(h-text_h)/2+20" + when;
        }
        captions += ",drawtext=text='EDITORIAL ESTIMATE':fontsize=28:fontcolor=white:" +
            "borderw=2:bordercolor=black:x=(w-text_w)/2:y=(h-text_h)/2+160:" +
            "enable='between(t,0.75,28.5)'";
        if (movie.VoteCount > 0)
            captions += ",drawtext=text='TMDB AUDIENCE " +
                movie.VoteAverage.ToString("0.0", CultureInfo.InvariantCulture) +
                "/10':fontsize=54:fontcolor=white:borderw=4:bordercolor=black:" +
                "x=(w-text_w)/2:y=(h-text_h)/2:enable='gte(t,28.5)'";
        filters.Add("[base]" + captions.TrimStart(',') + "[v]");

        if (customMusic || info.HasAudio)
        {
            var volume = customMusic ? MovieMusicLevel : 0.8;
            filters.Add("[" + audioIndex + ":a]atrim=duration=30,asetpts=PTS-STARTPTS," +
                "volume=" + MovieNumber(volume) + ",apad=pad_dur=30[a]");
        }
        else filters.Add("anullsrc=r=48000:cl=stereo,atrim=duration=30[a]");
        args.AddRange(["-filter_complex", string.Join(";", filters),
            "-map", "[v]", "-map", "[a]", "-t", "30",
            "-c:v", "libx264", "-preset", "veryfast", "-crf", "21",
            "-c:a", "aac", "-b:a", "160k", "-pix_fmt", "yuv420p",
            "-movflags", "+faststart", output]);
        movie.Status = "Rendering 10-shot montage";
        var rendered = await MovieRunProcessAsync(ffmpeg, args, _cts.Token);
        if (rendered.Code != 0)
            throw new InvalidOperationException("FFmpeg: " +
                rendered.Error[^Math.Min(700, rendered.Error.Length)..]);
        if (!File.Exists(output) || new FileInfo(output).Length < 10_000)
            throw new InvalidDataException("Finished video file was not produced.");
        MovieStatus = "Exported " + output;
        return new PublishQueueItem {
            VideoPath = output, ThumbnailPath = poster, Title = movie.Label,
            Description = movie.Overview,
            TikTokCaption = movie.Label + " 🎬 " +
                (movie.VoteCount > 0 ? "TMDB audience " + movie.Rating + ". " : "") +
                "Category scores are editorial estimates. Would you watch it? " +
                "#MovieTok #MovieReview #FilmTok",
            YouTubeTitle = movie.Label + " #Shorts",
            YouTubeDescription = movie.Overview
        };
    }
}
