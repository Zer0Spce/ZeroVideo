namespace ZeroVideo.App.Models;

public sealed class ZeroVideoSettings
{
    public string ProjectsRoot { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    public string FfprobePath { get; set; } = "";
    public string EdgeUserDataDirectory { get; set; } = "";
    public string ChatGptUrl { get; set; } = "https://chatgpt.com/";
    public string ClipchampUrl { get; set; } = "https://app.clipchamp.com/";
    public string ClipchampLanguage { get; set; } = "English (United States)";
    public string ClipchampVoice { get; set; } = "";
    public double ClipchampPace { get; set; } = 1.0;
    public string AutomationProfilePath { get; set; } = "";
    public bool KeepBrowserOpen { get; set; } = true;
    // Defaults to the current known-good ChatGPT visual workflow.
    // "Local ComfyUI" and "Hybrid" opt in to a separate local backend.
    public string ImageGenerationEngine { get; set; } = "ChatGPT";
    public string LocalImageCheckpointFile { get; set; } = "sd_xl_base_1.0.safetensors";

    public string BackgroundMusicPath { get; set; } = "";
    public double BackgroundMusicDb { get; set; } = 0;

}
