using System.Text.Json;
using ZeroVideo.App.Models;

namespace ZeroVideo.App.ViewModels;

public sealed class MovieCheckpoint
{
    public string Niche { get; set; } = "";
    public int IdeaCount { get; set; }
    public string ChatUrl { get; set; } = "";
    public string LastStatus { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public List<MovieClipperEntrySnapshot> Ideas { get; set; } = [];
    public List<MovieCheckpointItem> Items { get; set; } = [];
}

public sealed class MovieClipperEntrySnapshot
{
    public int TmdbId { get; set; }
    public string Title { get; set; } = "";
    public string Year { get; set; } = "";
    public string PosterPath { get; set; } = "";
    public string Overview { get; set; } = "";
    public double VoteAverage { get; set; }
    public int VoteCount { get; set; }
    public bool IsChecked { get; set; }
    public string Status { get; set; } = "Ready";
}

public sealed class MovieCheckpointItem
{
    public int TmdbId { get; set; }
    public string Stage { get; set; } = "Ready";
    public string Error { get; set; } = "";
    public string TrailerPath { get; set; } = "";
    public string OutputPath { get; set; } = "";
    public string ThumbnailPath { get; set; } = "";
    public string SeoTitle { get; set; } = "";
    public string SeoCaption { get; set; } = "";
    public string SeoHashtags { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed partial class MainViewModel
{
    private static readonly string MovieCheckpointPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ZeroVideo", "MovieClipper", "last-run.checkpoint.json");

    private void MoviePrepareChat()
    {
        if (MovieNewChat)
        {
            // Exactly one new chat for this run. All subsequent calls use the same VideoProject.
            _movieChatProject = new VideoProject
            {
                Title = "MovieClipper batch: " + MovieNiche,
                Topic = MovieNiche
            };
            _chat.RequestNewChat();
            MovieNewChat = false;
            _movieCheckpoint.ChatUrl = "";
            MovieSaveCheckpoint();
        }
        else if (string.IsNullOrWhiteSpace(_movieChatProject.ChatGptConversationUrl) &&
                 !string.IsNullOrWhiteSpace(_movieCheckpoint.ChatUrl))
            _movieChatProject.ChatGptConversationUrl = _movieCheckpoint.ChatUrl;
    }

    private void MovieSaveCheckpoint()
    {
        _movieCheckpoint.Niche = MovieNiche;
        _movieCheckpoint.IdeaCount = MovieCount;
        _movieCheckpoint.ChatUrl = _movieChatProject.ChatGptConversationUrl ?? _movieCheckpoint.ChatUrl;
        _movieCheckpoint.UpdatedAt = DateTimeOffset.UtcNow;
        _movieCheckpoint.Ideas = MovieClipperIdeas.Select(x => new MovieClipperEntrySnapshot
        {
            TmdbId = x.TmdbId, Title = x.Title, Year = x.Year,
            PosterPath = x.PosterPath, Overview = x.Overview,
            VoteAverage = x.VoteAverage, VoteCount = x.VoteCount,
            IsChecked = x.IsChecked, Status = x.Status
        }).ToList();
        Directory.CreateDirectory(Path.GetDirectoryName(MovieCheckpointPath)!);
        var tmp = MovieCheckpointPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_movieCheckpoint, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
        File.Move(tmp, MovieCheckpointPath, true);
        Raise(nameof(MovieCheckpointStatus));
    }

    private void MovieCheckpointIdeas()
    {
        // Do not remove finished movie records when re-generating the same niche.
        MovieSaveCheckpoint();
    }

    private void MovieRestoreCheckpoint()
    {
        try
        {
            if (!File.Exists(MovieCheckpointPath)) return;
            var json = File.ReadAllText(MovieCheckpointPath);
            var checkpoint = JsonSerializer.Deserialize<MovieCheckpoint>(json);
            if (checkpoint is null) return;
            _movieCheckpoint = checkpoint;
            if (!string.IsNullOrWhiteSpace(checkpoint.Niche)) MovieNiche = checkpoint.Niche;
            if (checkpoint.IdeaCount > 0) MovieCount = checkpoint.IdeaCount;
            _movieChatProject = new VideoProject
            {
                Title = "MovieClipper batch: " + MovieNiche,
                Topic = MovieNiche,
                ChatGptConversationUrl = string.IsNullOrWhiteSpace(checkpoint.ChatUrl)
                    ? null : checkpoint.ChatUrl
            };
            MovieClipperIdeas.Clear();
            foreach (var i in checkpoint.Ideas)
                MovieClipperIdeas.Add(new MovieClipperEntry
                {
                    TmdbId = i.TmdbId, Title = i.Title, Year = i.Year,
                    PosterPath = i.PosterPath, Overview = i.Overview,
                    VoteAverage = i.VoteAverage, VoteCount = i.VoteCount,
                    IsChecked = i.IsChecked, Status = i.Status
                });
            MovieStatus = "Restored " + MovieClipperIdeas.Count +
                " movie ideas and stage checkpoints. Continue with Resume Workflow.";
            Raise(nameof(MovieCheckpointStatus));
        }
        catch (Exception ex) { MovieStatus = "MovieClipper checkpoint load failed: " + ex.Message; }
    }

    private MovieCheckpointItem MovieCheckpointItem(MovieClipperEntry movie)
    {
        var existing = _movieCheckpoint.Items.FirstOrDefault(x => x.TmdbId == movie.TmdbId);
        if (existing is not null) return existing;
        var created = new MovieCheckpointItem { TmdbId = movie.TmdbId };
        _movieCheckpoint.Items.Add(created);
        return created;
    }

    private void MovieCheckpointStage(MovieClipperEntry movie, string stage, string error = "")
    {
        var item = MovieCheckpointItem(movie);
        item.Stage = stage;
        item.Error = error;
        item.UpdatedAt = DateTimeOffset.UtcNow;
        movie.Status = stage + (string.IsNullOrWhiteSpace(error) ? "" : ": " + error);
        MovieSaveCheckpoint();
    }

    private PublishQueueItem MovieQueuedItemFromCheckpoint(MovieClipperEntry movie, MovieCheckpointItem checkpoint)
    {
        if (string.IsNullOrWhiteSpace(checkpoint.OutputPath) || !File.Exists(checkpoint.OutputPath))
            throw new FileNotFoundException("Checkpoint MP4 is missing.", checkpoint.OutputPath);
        return new PublishQueueItem
        {
            VideoPath = checkpoint.OutputPath,
            ThumbnailPath = File.Exists(checkpoint.ThumbnailPath) ? checkpoint.ThumbnailPath : "",
            Title = string.IsNullOrWhiteSpace(checkpoint.SeoTitle) ? movie.Label : checkpoint.SeoTitle,
            Description = movie.Overview,
            TikTokCaption = MovieAssembleCaption(checkpoint, movie),
            YouTubeTitle = movie.Label + " #Shorts",
            YouTubeDescription = movie.Overview
        };
    }

    private static string MovieAssembleCaption(MovieCheckpointItem cp, MovieClipperEntry movie)
    {
        if (!string.IsNullOrWhiteSpace(cp.SeoCaption))
            return (cp.SeoCaption.Trim() + " " + cp.SeoHashtags.Trim()).Trim();
        return movie.Label + " | Plot, acting, pacing, cinematography, writing and ending rated out of 10. " +
            "Which score would you change? #MovieReview #FilmTok #MovieTok";
    }

    private async Task MovieGenerateSeoAsync(MovieClipperEntry movie, MovieCheckpointItem checkpoint)
    {
        if (!string.IsNullOrWhiteSpace(checkpoint.SeoCaption) &&
            !string.IsNullOrWhiteSpace(checkpoint.SeoHashtags)) return;

        var fallbackTitle = movie.Label + " Review: Is It Worth Watching?";
        var fallbackCaption = movie.Label +
            " in a fast movie review. Plot, acting, pacing, cinematography, writing and ending rated out of 10. " +
            "Agree with the score? Tell us what you'd rate it.";
        var fallbackHashtags = "#MovieReview #FilmTok #MovieTok";
        try
        {
            movie.Status = "ChatGPT: generating SEO title, description and hashtags";
            var prompt = "Write compelling but accurate TikTok search-friendly metadata for this movie review. " +
                "Avoid made-up plot details, misleading headlines, promises of views, and unrelated trending tags. " +
                "Use a strong curiosity-driven hook with the movie title near the beginning. " +
                "No unverified claims that the movie is best, trending, number one, or newly released. " +
                "Return JSON only: {\"title\":\"...\",\"caption\":\"...\",\"hashtags\":\"#Tag1 #Tag2 #Tag3 #Tag4 #Tag5\"}. " +
                "Title: 40-85 characters. Caption: 100-240 characters, natural English. " +
                "Hashtags: 3-5 relevant movie, genre or film-review tags. " +
                "Film: " + movie.Label + ". Synopsis: " + movie.Overview;
            var response = await _chat.GenerateTextAsync(
                _movieChatProject, Settings, prompt, Log, _cts.Token);
            MovieSaveCheckpoint();
            var first = response.IndexOf('{');
            var last = response.LastIndexOf('}');
            if (first < 0 || last < first) throw new InvalidDataException("No metadata JSON");
            using var doc = JsonDocument.Parse(response.Substring(first, last - first + 1));
            var title = MovieJson(doc.RootElement, "title").Trim();
            var caption = MovieJson(doc.RootElement, "caption").Trim();
            var hashtags = MovieJson(doc.RootElement, "hashtags").Trim();
            if (title.Length is < 15 or > 120 || caption.Length is < 25 or > 400 ||
                hashtags.Length < 4 || !hashtags.Contains('#'))
                throw new InvalidDataException("Invalid metadata fields");
            checkpoint.SeoTitle = title;
            checkpoint.SeoCaption = caption;
            checkpoint.SeoHashtags = hashtags;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log("MovieClipper SEO fallback for " + movie.Label + ": " + ex.Message);
            checkpoint.SeoTitle = fallbackTitle;
            checkpoint.SeoCaption = fallbackCaption;
            checkpoint.SeoHashtags = fallbackHashtags;
        }
        MovieSaveCheckpoint();
    }
}
