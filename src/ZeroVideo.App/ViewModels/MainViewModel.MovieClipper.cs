using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Windows.Input;
using Microsoft.Win32;
using ZeroVideo.App.Infrastructure;

namespace ZeroVideo.App.ViewModels;

public sealed class MovieClipperMovie
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public string ReleaseDate { get; set; } = "";
    public string Overview { get; set; } = "";
    public string PosterPath { get; set; } = "";
    public double Rating { get; set; }
    public int VoteCount { get; set; }
    public string Display => Title + (ReleaseDate.Length >= 4 ? " (" + ReleaseDate[..4] + ")" : "") +
        (VoteCount > 0 ? "  •  TMDB " + Rating.ToString("0.0", CultureInfo.InvariantCulture) + "/10" : "  •  Unrated");
}

public sealed partial class MainViewModel
{
    private readonly HttpClient _movieHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private string _movieTmdbToken = "";
    private string _movieGenre = "Popular";
    private int _movieIdeaCount = 12;
    private string _movieTrailerPath = "";
    private string _movieMusicPath = "";
    private int _movieDuration = 30;
    private double _movieMusicVolume = 0.16;
    private string _movieStatus = "Search TMDB, pick a movie, then select an authorized local trailer file.";
    private MovieClipperMovie? _selectedMovie;
    private bool _movieHourlySchedule = true;
    private bool _movieKeepTrailerAudio = true;
    private string _movieFirstSlot = DateTime.Now.AddHours(1).ToString("yyyy-MM-dd HH:00", CultureInfo.InvariantCulture);
    public ObservableCollection<MovieClipperMovie> MovieIdeas { get; } = [];
    public ObservableCollection<string> MovieGenres { get; } = ["Popular", "Now Playing", "Upcoming", "Top Rated"];
    public string MovieTmdbToken { get => _movieTmdbToken; set => Set(ref _movieTmdbToken, value); }
    public string MovieGenre { get => _movieGenre; set => Set(ref _movieGenre, value); }
    public int MovieIdeaCount { get => _movieIdeaCount; set => Set(ref _movieIdeaCount, Math.Clamp(value, 1, 30)); }
    public MovieClipperMovie? SelectedMovie { get => _selectedMovie; set => Set(ref _selectedMovie, value); }
    public string MovieTrailerPath { get => _movieTrailerPath; set => Set(ref _movieTrailerPath, value); }
    public string MovieMusicPath { get => _movieMusicPath; set => Set(ref _movieMusicPath, value); }
    public int MovieDuration { get => _movieDuration; set => Set(ref _movieDuration, Math.Clamp(value, 10, 90)); }
    public double MovieMusicVolume { get => _movieMusicVolume; set => Set(ref _movieMusicVolume, Math.Clamp(value, 0, 1)); }
    public bool MovieKeepTrailerAudio { get => _movieKeepTrailerAudio; set => Set(ref _movieKeepTrailerAudio, value); }
    public bool MovieHourlySchedule { get => _movieHourlySchedule; set => Set(ref _movieHourlySchedule, value); }
    public string MovieFirstSlot { get => _movieFirstSlot; set => Set(ref _movieFirstSlot, value); }
    public string MovieStatus { get => _movieStatus; set => Set(ref _movieStatus, value); }
    public ICommand MovieGenerateIdeasCommand { get; private set; } = null!;
    public ICommand MovieBrowseTrailerCommand { get; private set; } = null!;
    public ICommand MovieBrowseMusicCommand { get; private set; } = null!;
    public ICommand MovieOpenBrowserCommand { get; private set; } = null!;
    public ICommand MovieRenderCommand { get; private set; } = null!;

    private void InitializeMovieClipperCommands()
    {
        MovieGenerateIdeasCommand = new AsyncCommand(MovieGenerateIdeasAsync);
        MovieBrowseTrailerCommand = new RelayCommand(() =>
        {
            var dialog = new OpenFileDialog { Filter = "Video files|*.mp4;*.mov;*.mkv;*.webm", CheckFileExists = true };
            if (dialog.ShowDialog() == true) MovieTrailerPath = dialog.FileName;
        });
        MovieBrowseMusicCommand = new RelayCommand(() =>
        {
            var dialog = new OpenFileDialog { Filter = "Audio files|*.mp3;*.wav;*.m4a;*.aac;*.flac", CheckFileExists = true };
            if (dialog.ShowDialog() == true) MovieMusicPath = dialog.FileName;
        });
        MovieOpenBrowserCommand = new RelayCommand(() =>
        {
            if (SelectedMovie is null) { MovieStatus = "Choose a movie first."; return; }
            var query = Uri.EscapeDataString(SelectedMovie.Title + " official trailer");
            Process.Start(new ProcessStartInfo("https://www.youtube.com/results?search_query=" + query) { UseShellExecute = true });
            MovieStatus = "Choose a trailer in your browser, then import a source file you have permission to use.";
        });
        MovieRenderCommand = new AsyncCommand(MovieRenderAsync);
    }

    private async Task MovieGenerateIdeasAsync()
    {
        if (string.IsNullOrWhiteSpace(MovieTmdbToken))
        {
            MovieStatus = "Enter your TMDB Read Access Token. It is kept in memory only.";
            return;
        }
        try
        {
            MovieStatus = "Fetching movie ideas from TMDB...";
            var endpoint = MovieGenre switch
            {
                "Now Playing" => "now_playing",
                "Upcoming" => "upcoming",
                "Top Rated" => "top_rated",
                _ => "popular"
            };
            using var req = new HttpRequestMessage(HttpMethod.Get,
                "https://api.themoviedb.org/3/movie/" + endpoint + "?language=en-US&page=1");
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", MovieTmdbToken.Trim());
            using var response = await _movieHttp.SendAsync(req, _cts.Token);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(_cts.Token));
            MovieIdeas.Clear();
            foreach (var movie in doc.RootElement.GetProperty("results").EnumerateArray().Take(MovieIdeaCount))
            {
                MovieIdeas.Add(new MovieClipperMovie
                {
                    Id = movie.GetProperty("id").GetInt32(),
                    Title = JsonString(movie, "title"),
                    ReleaseDate = JsonString(movie, "release_date"),
                    Overview = JsonString(movie, "overview"),
                    PosterPath = JsonString(movie, "poster_path"),
                    Rating = movie.TryGetProperty("vote_average", out var score) ? score.GetDouble() : 0,
                    VoteCount = movie.TryGetProperty("vote_count", out var count) ? count.GetInt32() : 0
                });
            }
            SelectedMovie = MovieIdeas.FirstOrDefault();
            MovieStatus = MovieIdeas.Count + " movies discovered. Pick one to generate a vertical clip.";
        }
        catch (Exception ex)
        {
            MovieStatus = "TMDB discovery failed: " + ex.Message;
        }
    }

    private static string JsonString(JsonElement item, string key) =>
        item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private async Task MovieRenderAsync()
    {
        if (SelectedMovie is null) { MovieStatus = "Select a movie first."; return; }
        if (string.IsNullOrWhiteSpace(MovieTmdbToken)) { MovieStatus = "Enter the TMDB token."; return; }
        if (!File.Exists(MovieTrailerPath)) { MovieStatus = "Import a permitted trailer video file first."; return; }
        if (!string.IsNullOrWhiteSpace(MovieMusicPath) && !File.Exists(MovieMusicPath))
        { MovieStatus = "The selected background-music file does not exist."; return; }
        if (string.IsNullOrWhiteSpace(SelectedMovie.PosterPath))
        { MovieStatus = "This movie has no TMDB poster. Choose another movie."; return; }
        try
        {
            var movie = SelectedMovie;
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ZeroVideo", "MovieClipper", movie.Id + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(root);
            var poster = Path.Combine(root, "poster.jpg");
            var output = Path.Combine(root, "movieclip.mp4");
            MovieStatus = "Downloading official TMDB poster...";
            using (var posterResponse = await _movieHttp.GetAsync(
                "https://image.tmdb.org/t/p/original" + movie.PosterPath, _cts.Token))
            {
                posterResponse.EnsureSuccessStatusCode();
                await using var stream = File.Create(poster);
                await posterResponse.Content.CopyToAsync(stream, _cts.Token);
            }
            var clipLength = Math.Max(8, MovieDuration - 2);
            MovieStatus = "Rendering 1080 x 1920 movie clip with FFmpeg...";
            // A static TMDB card followed by portrait-reframed movie footage. Source footage is never stretched.
            var args = new List<string> {
                "-y", "-loop", "1", "-framerate", "30", "-t", "2", "-i", poster,
                "-t", clipLength.ToString(CultureInfo.InvariantCulture), "-i", MovieTrailerPath
            };
            bool addMusic = !string.IsNullOrWhiteSpace(MovieMusicPath);
            if (addMusic) args.AddRange(["-stream_loop", "-1", "-i", MovieMusicPath]);
            const string canvas = "scale=1080:1920:force_original_aspect_ratio=increase,crop=1080:1920,setsar=1,fps=30,format=yuv420p";
            var filter = "[0:v]" + canvas + "[cover];[1:v]" + canvas +
                "[footage];[cover][footage]concat=n=2:v=1:a=0[v]";
            if (addMusic)
            {
                if (MovieKeepTrailerAudio)
                    filter += ";[1:a]adelay=2000|2000[dialog];[2:a]volume=" +
                        MovieMusicVolume.ToString(CultureInfo.InvariantCulture) +
                        ",atrim=duration=" + MovieDuration + "[music];[dialog][music]amix=inputs=2:duration=longest:normalize=0[a]";
                else
                    filter += ";[2:a]volume=" + MovieMusicVolume.ToString(CultureInfo.InvariantCulture) +
                        ",atrim=duration=" + MovieDuration + "[a]";
            }
            args.AddRange(["-filter_complex", filter, "-map", "[v]"]);
            // When music isn't provided, output video-only (rather than fail on a trailer without audio).
            if (addMusic) args.AddRange(["-map", "[a]", "-c:a", "aac", "-b:a", "160k"]);
            args.AddRange(["-t", MovieDuration.ToString(CultureInfo.InvariantCulture),
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "21",
                "-pix_fmt", "yuv420p", "-movflags", "+faststart", output]);
            var start = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardError = true };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Unable to launch FFmpeg.");
            var error = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(_cts.Token);
            if (process.ExitCode != 0) throw new InvalidOperationException("FFmpeg failed: " +
                (await error).TakeLast(900).Aggregate("", (s, c) => s + c));
            var caption = movie.Title + " 🎬 " +
                (movie.VoteCount > 0 ? "TMDB " + movie.Rating.ToString("0.0", CultureInfo.InvariantCulture) + "/10. " : "") +
                "What do you think? #MovieTok #MovieReview #FilmTok";
            var item = new PublishQueueItem {
                VideoPath = output, ThumbnailPath = poster, Title = movie.Title,
                Description = movie.Overview, TikTokCaption = caption,
                YouTubeTitle = movie.Title, YouTubeDescription = movie.Overview
            };
            if (MovieHourlySchedule)
            {
                if (!DateTime.TryParseExact(MovieFirstSlot, "yyyy-MM-dd HH:mm",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var first) || first <= DateTime.Now)
                    throw new InvalidDataException("Choose a future local start time (YYYY-MM-DD HH:mm).");
                var last = PublishingQueue.Where(x => x.IsScheduled && x.ScheduledAt.HasValue)
                    .Max(x => x.ScheduledAt);
                var slot = last.HasValue && last.Value.LocalDateTime >= first
                    ? last.Value.AddHours(1) : new DateTimeOffset(first);
                item.IsScheduled = true;
                item.ScheduledAt = slot;
                item.ScheduleDateText = slot.LocalDateTime.ToString("yyyy-MM-dd HH:mm");
            }
            TrackPublishingItem(item);
            PublishingQueue.Add(item);
            SavePublishingQueue();
            SelectedPublishItem = item;
            MovieStatus = "Finished! Added to Publishing queue" +
                (item.IsScheduled ? " with an hourly slot. Final TikTok upload requires publisher authorization." : ".") ;
        }
        catch (Exception ex)
        {
            MovieStatus = "MovieClipper render failed: " + ex.Message;
        }
    }
}
