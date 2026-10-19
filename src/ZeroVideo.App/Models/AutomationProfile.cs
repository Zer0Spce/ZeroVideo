namespace ZeroVideo.App.Models;

public sealed class AutomationProfile
{
    public SelectorSet ChatGpt { get; set; } = new();
    public ClipchampSelectorSet Clipchamp { get; set; } = new();
}

public class SelectorSet
{
    public List<string> PromptInputs { get; set; } = [];
    public List<string> SendButtons { get; set; } = [];
    public List<string> StopButtons { get; set; } = [];
    public List<string> AssistantMessages { get; set; } = [];
}

public sealed class ClipchampSelectorSet
{
    public List<string> NewVideoButtons { get; set; } = [];
    public List<string> RecordCreateButtons { get; set; } = [];
    public List<string> TextToSpeechButtons { get; set; } = [];
    public List<string> ScriptInputs { get; set; } = [];
    public List<string> LanguageControls { get; set; } = [];
    public List<string> VoiceControls { get; set; } = [];
    public List<string> SaveButtons { get; set; } = [];
    public List<string> ExportButtons { get; set; } = [];
    public List<string> AudioOnlyButtons { get; set; } = [];
    public List<string> DownloadButtons { get; set; } = [];
}
