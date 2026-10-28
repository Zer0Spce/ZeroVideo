using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using ZeroVideo.App.Infrastructure;

namespace ZeroVideo.App.ViewModels;

public sealed class VideoIdea : ObservableObject
{
    public string Title { get; init; } = "";
    public string Hook { get; init; } = "";
    public string Niche { get; init; } = "";
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

    private void InitializeIdeasCommands()
    {
        GenerateIdeasCommand = new AsyncCommand(GenerateIdeasAsync);
        QueueSelectedIdeaCommand = new RelayCommand(() =>
        {
            if (SelectedIdea is null) return;
            if (ShortQueue.Count == 1 && string.IsNullOrWhiteSpace(ShortQueue[0].Idea))
                ShortQueue[0].Idea = SelectedIdea.Title;
            else ShortQueue.Add(new ShortQueueEntry { Idea = SelectedIdea.Title });
            IdeaStatus = "Added to Automation Shorts queue: " + SelectedIdea.Title;
        });
        UseSelectedIdeaCommand = new RelayCommand(() =>
        {
            if (SelectedIdea is null) return;
            Topic = SelectedIdea.Title;
            IdeaStatus = "Copied selected title into the Create tab.";
        });
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
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            if (!root.TryGetProperty("response_type", out var type) ||
                type.GetString() != "zerovideo_ideas_v1")
                throw new InvalidDataException("ChatGPT returned JSON, but not the tagged ZeroVideo ideas response.");
            if (!root.TryGetProperty("ideas", out var ideas) || ideas.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("ChatGPT did not return the ideas array.");
            GeneratedIdeas.Clear();
            foreach (var item in ideas.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;
                var title = item.TryGetProperty("title", out var titleNode) && titleNode.ValueKind == JsonValueKind.String
                    ? titleNode.GetString()?.Trim() : null;
                var hook = item.TryGetProperty("hook", out var hookNode) && hookNode.ValueKind == JsonValueKind.String
                    ? hookNode.GetString()?.Trim() : null;
                if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(hook)) continue;
                GeneratedIdeas.Add(new VideoIdea { Title = title, Hook = hook, Niche = IdeaNiche.Trim() });
            }
            IdeaStatus = GeneratedIdeas.Count > 0
                ? $"{GeneratedIdeas.Count} ideas ready. Select one to add it to Shorts or Create."
                : "No usable ideas were returned. Try Generate again.";
            if (GeneratedIdeas.Count > 0) SelectedIdea = GeneratedIdeas[0];
        }
        catch (Exception ex)
        {
            IdeaStatus = "Idea generation failed: " + ex.Message;
            Log(IdeaStatus);
        }
    }
}
