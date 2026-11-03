using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Input;
using ZeroVideo.App.Infrastructure;

namespace ZeroVideo.App.ViewModels;

public sealed partial class MainViewModel
{
    private string _seriesTheme = "Celebrity Horror Stories";
    private int _seriesEpisodeCount = 10;
    private string _seriesStatus = "Enter a series theme, generate distinct episodes and add them to Automation Shorts.";
    public string SeriesTheme
    {
        get => _seriesTheme; set => Set(ref _seriesTheme,value);
    }
    public int SeriesEpisodeCount
    {
        get => _seriesEpisodeCount; set => Set(ref _seriesEpisodeCount,Math.Clamp(value,3,30));
    }
    public string SeriesStatus
    {
        get => _seriesStatus; private set => Set(ref _seriesStatus,value);
    }
    public ObservableCollection<VideoIdea> SeriesEpisodes { get; } = [];
    public ICommand GenerateContentSeriesCommand { get; private set; } = null!;
    public ICommand AddContentSeriesToShortsCommand { get; private set; } = null!;

    private void InitializeContentSeriesCommands()
    {
        GenerateContentSeriesCommand = new AsyncCommand(GenerateContentSeriesAsync);
        AddContentSeriesToShortsCommand = new RelayCommand(() =>
        {
            var chosen=SeriesEpisodes.Where(x=>x.IsChecked).ToList();
            if(chosen.Count==0)
            {
                SeriesStatus="Check the episodes you want to send to Automation Shorts.";
                return;
            }
            var titles=new HashSet<string>(
                ShortQueue.Select(x=>x.Idea.Trim()),StringComparer.OrdinalIgnoreCase);
            var added=0;
            foreach(var episode in chosen)
            {
                if(!titles.Add(episode.Title))continue;
                if(ShortQueue.Count==1&&string.IsNullOrWhiteSpace(ShortQueue[0].Idea))
                    ShortQueue[0].Idea=episode.Title;
                else ShortQueue.Add(new ShortQueueEntry{Idea=episode.Title});
                added++;
            }
            SeriesStatus=$"Queued {added} series episode(s) in their planned order. " +
                "Existing duplicate titles were skipped. Video generation has not started.";
        });
    }

    private async Task GenerateContentSeriesAsync()
    {
        var theme=SeriesTheme.Trim();
        if(theme.Length<3)
        {
            SeriesStatus="Enter a descriptive series theme first.";
            return;
        }
        try
        {
            SeriesStatus=$"Planning {SeriesEpisodeCount} distinct episodes about {theme}...";
            var prompt=$"Create a cohesive but NON-repetitive {SeriesEpisodeCount}-episode " +
                $"short-form video series for the niche '{theme}'. Every episode must " +
                "have a unique topic, factual research direction and compelling opening hook. " +
                "Give the episodes a sensible progression from broad appeal to more specialized " +
                "stories, but make each one stand alone. Do not manufacture true stories, " +
                "accuse real people of crimes without evidence or guarantee viral reach. " +
                $"Return EXACTLY {SeriesEpisodeCount} entries, in episode order. " +
                "Start each video title with 'Episode 01: ', 'Episode 02: ', etc. " +
                "Return ONLY one complete valid JSON object, no markdown, with this shape: " +
                "{\"response_type\":\"zerovideo_ideas_v1\",\"niche\":\"series theme\"," +
                "\"ideas\":[{\"title\":\"Episode 01: Specific title\"," +
                "\"hook\":\"A specific accurate hook\"}]}. " +
                "Every title and hook must be nonempty.";
            var raw=await _chat.GenerateTextAsync(
                new ZeroVideo.App.Models.VideoProject {Title="Series plan: "+theme,Topic=theme},
                Settings,prompt,Log,_cts.Token);
            using var doc=JsonDocument.Parse(raw);
            var root=doc.RootElement;
            if(root.GetProperty("response_type").GetString()!="zerovideo_ideas_v1")
                throw new InvalidDataException("Wrong series response format.");
            var ideas=root.GetProperty("ideas");
            if(ideas.ValueKind!=JsonValueKind.Array ||
               ideas.GetArrayLength()!=SeriesEpisodeCount)
                throw new InvalidDataException($"Expected exactly {SeriesEpisodeCount} episode ideas.");
            var generated=new List<VideoIdea>();
            var titles=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var (idea,index) in ideas.EnumerateArray().Select((x,i)=>(x,i)))
            {
                var title=idea.GetProperty("title").GetString()?.Trim()??"";
                var hook=idea.GetProperty("hook").GetString()?.Trim()??"";
                if(title.Length==0||hook.Length==0||!titles.Add(title))
                    throw new InvalidDataException("Episode titles/hooks are missing or duplicated.");
                // Consistent ordering even if the AI used unusual numbering.
                var stripped=System.Text.RegularExpressions.Regex.Replace(title,
                    @"^(?:Episode|EP)\s*\d+\s*[-:.–]\s*","",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                generated.Add(new VideoIdea
                {
                    Title=$"Episode {index+1:00}: {stripped}",
                    Hook=hook,Niche=theme,IsChecked=true
                });
            }
            SeriesEpisodes.Clear();
            foreach(var episode in generated)SeriesEpisodes.Add(episode);
            SeriesStatus=$"{SeriesEpisodes.Count} distinct episodes ready. " +
                "Uncheck unwanted episodes, then choose Add Checked Episodes to Automation.";
        }
        catch(Exception ex)
        {
            SeriesStatus="Content Series generation failed: "+ex.Message;
            Log(SeriesStatus);
        }
    }
}
