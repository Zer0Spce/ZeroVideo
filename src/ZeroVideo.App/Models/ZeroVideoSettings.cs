using System.ComponentModel;

namespace ZeroVideo.App.Models;

public sealed class ZeroVideoSettings : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public string ProjectsRoot { get; set; } = "";
    public string FfmpegPath { get; set; } = "";
    public string FfprobePath { get; set; } = "";
    public string EdgeUserDataDirectory { get; set; } = "";
    public string ChatGptUrl { get; set; } = "https://chatgpt.com/";
    public string ClipchampUrl { get; set; } = "https://app.clipchamp.com/";
    public string ClipchampLanguage { get; set; } = "English (United States)";
    public string ClipchampVoice { get; set; } = "";
    public double ClipchampPace { get; set; } = 1.0;
    // Preserve Clipchamp by default; OmniVoice is an opt-in non-commercial
    // evaluation engine (pretrained model weights are CC BY-NC 4.0).
    private string _textToSpeechEngine = "Clipchamp";
    public string TextToSpeechEngine
    {
        get => _textToSpeechEngine;
        set
        {
            if (_textToSpeechEngine == value) return;
            _textToSpeechEngine = value;
            Notify(nameof(TextToSpeechEngine));
        }
    }
    private string _omniVoiceProfileId = "";
    public string OmniVoiceProfileId
    {
        get => _omniVoiceProfileId;
        set
        {
            if (_omniVoiceProfileId == value) return;
            _omniVoiceProfileId = value;
            Notify(nameof(OmniVoiceProfileId));
        }
    }
    public double OmniVoiceSpeed { get; set; } = 1.0;
    public bool OmniVoiceNonCommercialAcknowledged { get; set; }

    public string AutomationProfilePath { get; set; } = "";
    public bool KeepBrowserOpen { get; set; } = true;
    // Defaults to the current known-good ChatGPT visual workflow.
    // "Local ComfyUI" and "Hybrid" opt in to a separate local backend.
    public string ImageGenerationEngine { get; set; } = "ChatGPT";
    public string LocalImageCheckpointFile { get; set; } = "sd_xl_base_1.0.safetensors";

    public string BackgroundMusicPath { get; set; } = "";
    public double BackgroundMusicDb { get; set; } = 0;

}
