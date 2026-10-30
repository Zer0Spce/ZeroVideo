using System.Collections.ObjectModel;
using System.Text.Json;
using System.Globalization;
using System.Net.Http;
using System.Xml.Linq;
using System.Xml;
using System.Windows.Input;
using ZeroVideo.App.Infrastructure;

namespace ZeroVideo.App.ViewModels;

public sealed class VideoIdea : ObservableObject
{
    public string Title { get; init; } = "";
    public string Hook { get; init; } = "";
    public string Niche { get; init; } = "";
    private bool _isChecked;
    public bool IsChecked { get => _isChecked; set => Set(ref _isChecked, value); }
}

public sealed partial class MainViewModel
{
    private string _ideaNiche = "Horror";
    private int _ideaCount = 12;
    private VideoIdea? _selectedIdea;
    private string _ideaStatus = "Enter a niche and generate title and hook ideas.";
    public string IdeaNiche { get => _ideaNiche; set => Set(ref _ideaNiche, value); }
    public int IdeaCount { get => _ideaCount; set => Set(ref _ideaCount, Math.Clamp(value, 3, 30)); }
    public VideoIdea? SelectedIdea { get => _selectedIdea; set => Set(ref _selectedIdea, value); }
    public string IdeaStatus { get => _ideaStatus; set => Set(ref _ideaStatus, value); }
    public ObservableCollection<VideoIdea> GeneratedIdeas { get; } = [];
    public ICommand GenerateIdeasCommand { get; private set; } = null!;
    public ICommand QueueSelectedIdeaCommand { get; private set; } = null!;
    public ICommand UseSelectedIdeaCommand { get; private set; } = null!;
    public ICommand GenerateSmartIdeasCommand { get; private set; } = null!;
    public ICommand CheckAllIdeasCommand { get; private set; } = null!;
    public ICommand ClearIdeaChecksCommand { get; private set; } = null!;


    private void InitializeIdeasCommands()
    {
        GenerateIdeasCommand = new AsyncCommand(GenerateIdeasAsync);
        GenerateSmartIdeasCommand = new AsyncCommand(GenerateTrendingIdeasAsync);
        CheckAllIdeasCommand = new RelayCommand(() =>
        {
            foreach (var idea in GeneratedIdeas) idea.IsChecked = true;
            IdeaStatus = $"Checked {GeneratedIdeas.Count} ideas.";
        });
        ClearIdeaChecksCommand = new RelayCommand(() =>
        {
            foreach (var idea in GeneratedIdeas) idea.IsChecked = false;
            IdeaStatus = "Idea checkboxes cleared.";
        });
        QueueSelectedIdeaCommand = new RelayCommand(() =>
        {
            var chosen = GeneratedIdeas.Where(x => x.IsChecked).ToList();
            if (chosen.Count == 0)
            {
                IdeaStatus = "Check one or more ideas before adding them to Automation Shorts.";
                return;
            }
            var existing = new HashSet<string>(
                ShortQueue.Select(x => x.Idea.Trim()),
                StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var idea in chosen)
            {
                var title = idea.Title.Trim();
                if (title.Length == 0 || !existing.Add(title)) continue;
                if (ShortQueue.Count == 1 && string.IsNullOrWhiteSpace(ShortQueue[0].Idea))
                    ShortQueue[0].Idea = title;
                else ShortQueue.Add(new ShortQueueEntry { Idea = title });
                added++;
            }
            IdeaStatus = $"Added {added} new checked idea(s) to Automation Shorts. " +
                (chosen.Count - added > 0 ? "Duplicates were skipped." : "");
        });
        UseSelectedIdeaCommand = new RelayCommand(() =>
        {
            if (SelectedIdea is null) return;
            Topic = SelectedIdea.Title;
            IdeaStatus = "Copied selected title into the Create tab.";
        });
    }

    private static readonly HttpClient TrendHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };

    // A live public feed is required: do not claim a niche is "trending now"
    // if the app cannot retrieve a dated source. Google News covers a chosen
    // niche; Google Trends provides current broader search interest.
    private static async Task<List<string>> GetCurrentTrendSignalsAsync(
        string niche, CancellationToken ct)
    {
        var signals = new List<string>();
        var requests = new[]
        {
            ("News", "https://news.google.com/rss/search?q=" +
                Uri.EscapeDataString(niche + " when:7d") +
                "&hl=en-US&gl=US&ceid=US:en"),
            ("Search", "https://trends.google.com/trending/rss?geo=US")
        };
        foreach (var (source, url) in requests)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await TrendHttpClient.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();
                var xml = await response.Content.ReadAsStringAsync(ct);
                // RSS is external/untrusted; disable DTD processing and cap XML.
                if (xml.Length > 1_500_000) continue;
                using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                });
                var doc = XDocument.Load(reader);
                var now = DateTimeOffset.UtcNow;
                var items = doc.Descendants("item")
                    .Select(e => new
                    {
                        Title = e.Element("title")?.Value.Trim() ?? "",
                        Date = e.Element("pubDate")?.Value.Trim() ?? "",
                        Link = e.Element("link")?.Value.Trim() ?? ""
                    })
                    .Where(x => x.Title.Length is > 7 and < 240)
                    .Take(source == "News" ? 18 : 12);
                foreach (var item in items)
                {
                    if (!DateTimeOffset.TryParse(item.Date, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal, out var published))
                        continue;
                    if (published > now.AddHours(12) || published < now.AddDays(-8))
                        continue;
                    // Basic allow-list for RSS links to avoid appending unknown
                    // URLs and unbounded prompt content from a third-party feed.
                    if (!Uri.TryCreate(item.Link, UriKind.Absolute, out var link) ||
                        link.Scheme != Uri.UriSchemeHttps ||
                        !(link.Host.EndsWith("google.com", StringComparison.OrdinalIgnoreCase) ||
                          link.Host.EndsWith("google.co.uk", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    signals.Add($"{source} | {published:yyyy-MM-dd} | " +
                        item.Title.Replace('\n',' ').Replace('\r',' ') + " | " + link);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                // Continue if either feed is temporarily unavailable; the
                // final guard still requires actual fresh evidence.
            }
        }
        return signals.Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToList();
    }

    private async Task GenerateTrendingIdeasAsync()
    {
        var niche = IdeaNiche.Trim();
        if (string.IsNullOrWhiteSpace(niche))
        {
            IdeaStatus = "Enter a niche first, then choose Smart Ideas.";
            return;
        }
        try
        {
            IdeaStatus = "Checking live trend/news sources for " + niche + "...";
            var signals = await GetCurrentTrendSignalsAsync(niche, _cts.Token);
            var tikTokSignals = TikTokIdeaSignals();
            if (signals.Count + tikTokSignals.Count < 2)
            {
                IdeaStatus = "Smart Ideas 2.0 needs current trend evidence. No fresh " +
                    "Creative Center capture or recent public news/search feed was available. " +
                    "Capture TikTok trends in the Intelligence tab or use Generate video ideas.";
                return;
            }
            IdeaStatus = $"Smart Ideas 2.0: researching {signals.Count} public " +
                $"and {tikTokSignals.Count} TikTok signals...";
            var prompt =
                $"Act as a careful short-form video researcher and creator. Today's local date is " +
                $"{DateTimeOffset.Now:yyyy-MM-dd}. My niche is: {niche}. " +
                $"Generate exactly {IdeaCount} distinct original curiosity-driven video ideas with specific hooks. " +
                "Use the dated public news/search signals and the fresh visible TikTok " +
                "Creative Center hashtags below as evidence of topical interest. " +
                "Account historical top-view videos may inform the user's preferred niche; " +
                "they are NOT proof of current trending popularity. " +
                "Prioritize verified and niche-relevant topics; clearly separate factual " +
                "claims from speculation. Never invent facts, promise virality, or describe " +
                "hashtag captures as audited growth rankings. " +
                "Return ONLY one complete JSON object without markdown: " +
                "{\"response_type\":\"zerovideo_ideas_v1\",\"niche\":\"" +
                niche.Replace("\"", "'") +
                "\",\"ideas\":[{\"title\":\"Specific curiosity-driven title\", \"hook\":\"Factual opening hook\"}]}. " +
                "Every title and hook nonempty. Public dated signals:\n" +
                string.Join("\n", signals) +
                "\nTikTok Creative Center visible hashtags, with capture dates:\n" +
                string.Join("\n", tikTokSignals) +
                "\nUser's historical top-viewed account topics (NOT current trends):\n" +
                TikTokIdeaAccountEvidence();
            var raw = await _chat.GenerateTextAsync(new ZeroVideo.App.Models.VideoProject
            {
                Title = "Smart ideas: " + niche,
                Topic = niche
            }, Settings, prompt, Log, _cts.Token);
            PopulateGeneratedIdeas(raw, niche);
            IdeaStatus = $"Smart Ideas 2.0: {GeneratedIdeas.Count} ideas informed by " +
                $"{signals.Count} recent public signals and {tikTokSignals.Count} " +
                "captured Creative Center hashtags. Watchability is an estimate, not a guarantee.";
        }
        catch (Exception ex)
        {
            IdeaStatus = "Smart Ideas failed: " + ex.Message;
            Log(IdeaStatus);
        }
    }

    private void PopulateGeneratedIdeas(string raw, string niche)
    {
        using var document = JsonDocument.Parse(raw);
        var root = document.RootElement;
        if (!root.TryGetProperty("response_type", out var type) ||
            type.GetString() != "zerovideo_ideas_v1")
            throw new InvalidDataException("ChatGPT returned an unexpected ideas JSON response.");
        if (!root.TryGetProperty("ideas", out var ideas) ||
            ideas.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("ChatGPT did not return an ideas array.");
        var fresh = new List<VideoIdea>();
        var titles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in ideas.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var title = item.TryGetProperty("title", out var tn) &&
                tn.ValueKind == JsonValueKind.String ? tn.GetString()?.Trim() : null;
            var hook = item.TryGetProperty("hook", out var hn) &&
                hn.ValueKind == JsonValueKind.String ? hn.GetString()?.Trim() : null;
            if (string.IsNullOrWhiteSpace(title) ||
                string.IsNullOrWhiteSpace(hook) || !titles.Add(title)) continue;
            fresh.Add(new VideoIdea { Title = title, Hook = hook, Niche = niche });
        }
        if (fresh.Count == 0) throw new InvalidDataException("No usable title and hook pairs returned.");
        GeneratedIdeas.Clear();
        foreach (var idea in fresh) GeneratedIdeas.Add(idea);
        SelectedIdea = GeneratedIdeas.FirstOrDefault();
    }

    private async Task GenerateIdeasAsync()
    {
        if (string.IsNullOrWhiteSpace(IdeaNiche))
        {
            IdeaStatus = "Enter a niche first.";
            return;
        }

        try
        {
            IdeaStatus = "Generating ideas in ChatGPT...";
            var raw = await _chat.GenerateVideoIdeasAsync(IdeaNiche.Trim(), IdeaCount, Settings, Log, _cts.Token);
            PopulateGeneratedIdeas(raw, IdeaNiche.Trim());
            IdeaStatus = $"{GeneratedIdeas.Count} ideas ready. Check the ones to add to Automation Shorts.";
        }
        catch (Exception ex)
        {
            IdeaStatus = "Idea generation failed: " + ex.Message;
            Log(IdeaStatus);
        }
    }
}
