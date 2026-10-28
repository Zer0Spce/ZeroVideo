using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using ZeroVideo.App.Infrastructure;
using ZeroVideo.App.Services;

namespace ZeroVideo.App.ViewModels;

public sealed partial class MainViewModel
{
    private readonly PublishingService _publishing = new();
    private string _publishFile = "";
    private string _publishTitle = "";
    private string _publishDescription = "";
    private string _youtubePrivacy = "private";
    private string _tikTokPrivacy = "SELF_ONLY";
    private string _publishSchedule = "";
    private bool _publishConsent;
    private string _publishStatus = "Select an exported MP4, enter authorized credentials and review before posting.";
    // Access tokens remain in process memory and are never written to ZeroVideo settings or logs.
    public string YouTubeAccessToken { private get; set; } = "";
    public string TikTokAccessToken { private get; set; } = "";
    public string PublishFile { get => _publishFile; set => Set(ref _publishFile, value); }
    public string PublishTitle { get => _publishTitle; set => Set(ref _publishTitle, value); }
    public string PublishDescription { get => _publishDescription; set => Set(ref _publishDescription, value); }
    public string YouTubePrivacy { get => _youtubePrivacy; set => Set(ref _youtubePrivacy, value); }
    public string TikTokPrivacy { get => _tikTokPrivacy; set => Set(ref _tikTokPrivacy, value); }
    public string PublishSchedule { get => _publishSchedule; set => Set(ref _publishSchedule, value); }
    public bool PublishConsent { get => _publishConsent; set => Set(ref _publishConsent, value); }
    public string PublishStatus { get => _publishStatus; set => Set(ref _publishStatus, value); }
    public ObservableCollection<string> YouTubePrivacyOptions { get; } = ["private", "unlisted", "public"];
    public ObservableCollection<string> TikTokPrivacyOptions { get; } = ["SELF_ONLY", "MUTUAL_FOLLOW_FRIENDS", "FOLLOWER_OF_CREATOR", "PUBLIC_TO_EVERYONE"];
    public ICommand BrowsePublishFileCommand { get; private set; } = null!;
    public ICommand PublishYouTubeCommand { get; private set; } = null!;
    public ICommand PublishTikTokCommand { get; private set; } = null!;

    private void InitializePublishingCommands()
    {
        BrowsePublishFileCommand = new RelayCommand(() =>
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "MP4 videos|*.mp4", CheckFileExists = true };
            if (dialog.ShowDialog() != true) return;
            PublishFile = dialog.FileName;
            PublishTitle = Path.GetFileNameWithoutExtension(dialog.FileName);
        });
        PublishYouTubeCommand = new AsyncCommand(async () =>
        {
            if (!ValidatePublish()) return;
            try
            {
                DateTimeOffset? scheduled = null;
                if (!string.IsNullOrWhiteSpace(PublishSchedule))
                {
                    if (!DateTimeOffset.TryParse(PublishSchedule, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out var date))
                        throw new FormatException("Schedule format invalid. Use e.g. 2026-11-01 18:00 (local time).");
                    scheduled = date;
                }
                PublishStatus = "Uploading to YouTube...";
                PublishStatus = await _publishing.UploadYouTubeAsync(PublishFile, PublishTitle, PublishDescription,
                    YouTubeAccessToken, YouTubePrivacy, scheduled, _cts.Token);
            }
            catch (Exception ex) { PublishStatus = "YouTube: " + ex.Message; }
            finally { PublishConsent = false; }
        });
        PublishTikTokCommand = new AsyncCommand(async () =>
        {
            if (!ValidatePublish()) return;
            try
            {
                PublishStatus = "Uploading to TikTok...";
                PublishStatus = await _publishing.UploadTikTokAsync(PublishFile, PublishTitle,
                    TikTokAccessToken, TikTokPrivacy, _cts.Token);
            }
            catch (Exception ex) { PublishStatus = "TikTok: " + ex.Message; }
            finally { PublishConsent = false; }
        });
    }

    private bool ValidatePublish()
    {
        if (!PublishConsent)
        {
            PublishStatus = "Review your account, content, privacy and rights, then check the authorization box.";
            return false;
        }
        if (!File.Exists(PublishFile) || !PublishFile.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            PublishStatus = "Select a valid exported MP4 first.";
            return false;
        }
        if (string.IsNullOrWhiteSpace(PublishTitle))
        {
            PublishStatus = "Enter a video title.";
            return false;
        }
        if (MessageBox.Show($"Upload this video?\n\n{PublishTitle}\n{PublishFile}\n\nYou are authorizing a real platform upload.",
            "Confirm video upload", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return false;
        return true;
    }
}
