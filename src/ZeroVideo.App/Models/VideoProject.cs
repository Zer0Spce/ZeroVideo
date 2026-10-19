using System.Text.Json.Serialization;

namespace ZeroVideo.App.Models;

public sealed class VideoProject
{
    public string Id { get; set; } = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
    public string Title { get; set; } = "Untitled ZeroVideo Project";
    public string Topic { get; set; } = "";
    public int TargetMinutes { get; set; } = 10;
    public string AspectRatio { get; set; } = "16:9";
    public string Style { get; set; } = "Documentary explainer";
    public string Language { get; set; } = "English";
    public string Voice { get; set; } = "";
    public string? ChatGptConversationUrl { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
    public List<ProjectChapter> Chapters { get; set; } = [];
    public List<string> Sources { get; set; } = [];
    public ProjectOptions Options { get; set; } = new();
    [JsonIgnore] public string ProjectDirectory { get; set; } = "";
}

public sealed class ProjectOptions
{
    public bool GenerateCaptions { get; set; } = true;
    public bool AddMotion { get; set; } = true;
    public bool AddBackgroundMusic { get; set; }
    public bool AddSoundEffects { get; set; }
    public bool AvoidRepeatedVisuals { get; set; } = true;
    public bool GenerateMissingVisuals { get; set; } = true;
    public int MaxGeneratedVisuals { get; set; } = 120;
    public string? BackgroundMusicPath { get; set; }
    public double BackgroundMusicVolume { get; set; } = 0.10;
}

public sealed class ProjectChapter
{
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Purpose { get; set; } = "";
    public string Narration { get; set; } = "";
    public int TargetSeconds { get; set; }
    public string? AudioPath { get; set; }
    public List<ProjectScene> Scenes { get; set; } = [];
}

public sealed class ProjectScene
{
    public int Number { get; set; }
    public string Role { get; set; } = "detail";
    public string Narration { get; set; } = "";
    public string VisualType { get; set; } = "broll";
    public string VisualQuery { get; set; } = "";
    public string ImagePrompt { get; set; } = "";
    public string ShotDirection { get; set; } = "slow push in";
    public double TargetSeconds { get; set; } = 6;
    public string? MediaPath { get; set; }
    public string? SoundEffectPath { get; set; }
}
