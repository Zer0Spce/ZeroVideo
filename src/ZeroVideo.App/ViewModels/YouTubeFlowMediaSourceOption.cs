using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ZeroVideo.App.ViewModels;

/// <summary>One user-configurable YouTube Flow source, in priority order.</summary>
public sealed class YouTubeFlowMediaSourceOption : INotifyPropertyChanged
{
    private bool _isEnabled;
    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public bool RequiresReview { get; }

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value) return;
            _isEnabled = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
        }
    }

    public YouTubeFlowMediaSourceOption(string id, string name,
        string description, bool enabled = true, bool requiresReview = false)
    {
        Id = id;
        Name = name;
        Description = description;
        RequiresReview = requiresReview;
        _isEnabled = enabled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
