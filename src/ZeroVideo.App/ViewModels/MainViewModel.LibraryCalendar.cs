using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Windows;
using System.Windows.Input;
using ZeroVideo.App.Infrastructure;
using ZeroVideo.App.Services;

namespace ZeroVideo.App.ViewModels;

public sealed class VideoLibraryEntry : ObservableObject
{
    public string Title { get; init; } = "";
    public string VideoPath { get; init; } = "";
    public string ProjectDirectory { get; init; } = "";
    public string ThumbnailPath { get; init; } = "";
    public string PublishingStatus { get; init; } = "";
    public DateTime Modified { get; init; }
    public long Bytes { get; init; }
    public Uri? VideoUri => File.Exists(VideoPath) ? new Uri(VideoPath) : null;
    public string SizeLabel => (Bytes/1048576.0).ToString("N1")+" MB";
}

public sealed class CalendarVideoEntry
{
    public string Title { get; init; } = "";
    public string Time { get; init; } = "";
    public string Status { get; init; } = "";
    public PublishQueueItem Item { get; init; } = null!;
}

public sealed class CalendarDayEntry
{
    public DateTime Date { get; init; }
    public string DayLabel => Date.Day.ToString(CultureInfo.InvariantCulture);
    public bool InCurrentMonth { get; init; }
    public bool IsToday => Date.Date == DateTime.Today;
    public ObservableCollection<CalendarVideoEntry> Videos { get; } = [];
}

public sealed partial class MainViewModel
{
    private DateTime _calendarMonth = new(DateTime.Today.Year,DateTime.Today.Month,1);
    public ObservableCollection<CalendarDayEntry> CalendarDays { get; } = [];
    public string CalendarMonthLabel => _calendarMonth.ToString("MMMM yyyy",CultureInfo.CurrentCulture);
    private CalendarVideoEntry? _selectedCalendarVideo;
    private string _calendarEditTime = "";
    private string _calendarStatus = "Scheduled videos appear on their local posting dates. Calendar edits do not publish.";
    public CalendarVideoEntry? SelectedCalendarVideo
    {
        get => _selectedCalendarVideo;
        set
        {
            if(!Set(ref _selectedCalendarVideo,value))return;
            if(value is not null)
                CalendarEditTime=value.Item.ScheduleDateText;
        }
    }
    public string CalendarEditTime
    {
        get => _calendarEditTime; set => Set(ref _calendarEditTime,value);
    }
    public string CalendarStatus
    {
        get => _calendarStatus; private set => Set(ref _calendarStatus,value);
    }
    public ICommand PreviousCalendarMonthCommand { get; private set; } = null!;
    public ICommand NextCalendarMonthCommand { get; private set; } = null!;
    public ICommand CurrentCalendarMonthCommand { get; private set; } = null!;
    public ICommand ApplyCalendarTimeCommand { get; private set; } = null!;
    public ICommand RefreshCalendarCommand { get; private set; } = null!;

    private string _librarySearch = "";
    private VideoLibraryEntry? _selectedLibraryVideo;
    private string _libraryStatus = "Select Refresh Library to find finished MP4s.";
    private string _fileManagementStatus = "Archives are additive copies. Nothing is moved or deleted.";
    public string LibrarySearch
    {
        get => _librarySearch;
        set { if(Set(ref _librarySearch,value))RefreshVideoLibrary(); }
    }
    public VideoLibraryEntry? SelectedLibraryVideo
    {
        get => _selectedLibraryVideo; set => Set(ref _selectedLibraryVideo,value);
    }
    public string LibraryStatus
    {
        get => _libraryStatus; private set => Set(ref _libraryStatus,value);
    }
    public string FileManagementStatus
    {
        get => _fileManagementStatus; private set => Set(ref _fileManagementStatus,value);
    }
    public ObservableCollection<VideoLibraryEntry> VideoLibrary { get; } = [];
    public ICommand RefreshLibraryCommand { get; private set; } = null!;
    public ICommand OpenLibraryVideoCommand { get; private set; } = null!;
    public ICommand OpenLibraryFolderCommand { get; private set; } = null!;
    public ICommand PreviewArchiveCommand { get; private set; } = null!;
    public ICommand ArchiveLibraryProjectCommand { get; private set; } = null!;

    private void InitializeLibraryAndCalendarCommands()
    {
        PreviousCalendarMonthCommand = new RelayCommand(() =>
        {
            _calendarMonth=_calendarMonth.AddMonths(-1); RefreshPublishingCalendar();
        });
        NextCalendarMonthCommand = new RelayCommand(() =>
        {
            _calendarMonth=_calendarMonth.AddMonths(1); RefreshPublishingCalendar();
        });
        CurrentCalendarMonthCommand = new RelayCommand(() =>
        {
            _calendarMonth=new DateTime(DateTime.Today.Year,DateTime.Today.Month,1);
            RefreshPublishingCalendar();
        });
        RefreshCalendarCommand = new RelayCommand(RefreshPublishingCalendar);
        ApplyCalendarTimeCommand = new RelayCommand(ApplyCalendarTime);
        RefreshLibraryCommand = new RelayCommand(RefreshVideoLibrary);
        OpenLibraryVideoCommand = new RelayCommand(() =>
        {
            if(SelectedLibraryVideo is { } v && File.Exists(v.VideoPath))
                Process.Start(new ProcessStartInfo(v.VideoPath){UseShellExecute=true});
        });
        OpenLibraryFolderCommand = new RelayCommand(() =>
        {
            if(SelectedLibraryVideo is { } v && Directory.Exists(v.ProjectDirectory))
                Process.Start(new ProcessStartInfo("explorer.exe",v.ProjectDirectory){UseShellExecute=true});
        });
        PreviewArchiveCommand = new RelayCommand(PreviewLibraryArchive);
        ArchiveLibraryProjectCommand = new AsyncCommand(ArchiveLibraryProjectAsync);
    }

    private void RefreshPublishingCalendar()
    {
        var first = _calendarMonth.AddDays(-(int)_calendarMonth.DayOfWeek);
        // Six calendar rows provide enough room for any month.
        var selectedId = SelectedCalendarVideo?.Item.Id;
        CalendarDays.Clear();
        for(var d=0;d<42;d++)
        {
            var date=first.AddDays(d);
            var day = new CalendarDayEntry
            {
                Date=date,
                InCurrentMonth=date.Month==_calendarMonth.Month &&
                    date.Year==_calendarMonth.Year
            };
            foreach(var item in PublishingQueue.Where(v=>v.IsScheduled)
                .OrderBy(v=>v.ScheduledAt))
            {
                if(!DateTime.TryParseExact(item.ScheduleDateText,
                    "yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal,out var slot) ||
                    slot.Date!=date.Date)
                    continue;
                day.Videos.Add(new CalendarVideoEntry
                {
                    Title=item.Title,
                    Time=slot.ToString("HH:mm",CultureInfo.InvariantCulture),
                    Status=item.TikTokStatus,
                    Item=item
                });
            }
            CalendarDays.Add(day);
        }
        Raise(nameof(CalendarMonthLabel));
        SelectedCalendarVideo=CalendarDays.SelectMany(x=>x.Videos)
            .FirstOrDefault(x=>x.Item.Id==selectedId);
        CalendarStatus=$"{PublishingQueue.Count(x=>x.IsScheduled)} scheduled queue entries. " +
            "Select a video below to edit its local date/time; no TikTok action occurs.";
    }

    private void ApplyCalendarTime()
    {
        if(IsBatchPublishing)
        {
            CalendarStatus="Calendar edits are disabled while TikTok is publishing.";
            return;
        }
        if(SelectedCalendarVideo?.Item is not { } item)
        {
            CalendarStatus="Select a scheduled calendar entry first.";
            return;
        }
        if(!DateTime.TryParseExact(CalendarEditTime.Trim(),"yyyy-MM-dd HH:mm",
            CultureInfo.InvariantCulture,DateTimeStyles.AssumeLocal,out var slot) ||
            slot <= DateTime.Now.AddMinutes(20) || slot.Minute%5!=0)
        {
            CalendarStatus="Enter a future local YYYY-MM-DD HH:MM on a 5-minute increment.";
            return;
        }
        if(PublishingQueue.Any(v=>!ReferenceEquals(v,item) && v.IsScheduled &&
            v.ScheduledAt.HasValue && Math.Abs(
                (v.ScheduledAt.Value.LocalDateTime-slot).TotalMinutes)<60))
        {
            CalendarStatus="A different video already has a schedule within 60 minutes of this slot.";
            return;
        }
        item.IsScheduled=true;
        item.ScheduleDateText=slot.ToString("yyyy-MM-dd HH:mm",CultureInfo.InvariantCulture);
        item.ScheduledAt=new DateTimeOffset(slot);
        SavePublishingQueue();
        _calendarMonth=new DateTime(slot.Year,slot.Month,1);
        RefreshPublishingCalendar();
        CalendarStatus="Calendar saved the new LOCAL schedule to the queue; it has NOT submitted or changed any post on TikTok.";
    }

    private void RefreshVideoLibrary()
    {
        try
        {
            var root=Settings.ProjectsRoot;
            var current=SelectedLibraryVideo?.VideoPath;
            VideoLibrary.Clear();
            if(string.IsNullOrWhiteSpace(root)||!Directory.Exists(root))
            {
                LibraryStatus="Projects folder was not found. Check Settings → Projects Root.";
                return;
            }
            // Search exported videos only; do not enumerate recovery archives.
            var matches=Directory.EnumerateFiles(root,"*.mp4",SearchOption.AllDirectories)
                .Where(x=>Path.GetRelativePath(root,x).Split(
                    Path.DirectorySeparatorChar).All(part=>
                    !part.Equals("_ZeroVideoArchives",StringComparison.OrdinalIgnoreCase)))
                .Where(x=>string.IsNullOrWhiteSpace(LibrarySearch) ||
                    x.Contains(LibrarySearch,StringComparison.OrdinalIgnoreCase))
                .Take(1200).Select(path=>
                {
                    var info=new FileInfo(path);
                    var projectDir=FindProjectDirectory(path,root);
                    var queued=PublishingQueue.FirstOrDefault(q=>
                        q.VideoPath.Equals(path,StringComparison.OrdinalIgnoreCase));
                    var thumb=Path.Combine(projectDir,"thumbnail.png");
                    return new VideoLibraryEntry
                    {
                        Title=queued?.Title??Path.GetFileNameWithoutExtension(path),
                        VideoPath=path,ProjectDirectory=projectDir,
                        ThumbnailPath=File.Exists(thumb)?thumb:"",
                        PublishingStatus=queued?.TikTokStatus??"Not in publishing queue",
                        Modified=info.LastWriteTime,Bytes=info.Length
                    };
                }).OrderByDescending(x=>x.Modified).ToList();
            foreach(var item in matches)VideoLibrary.Add(item);
            SelectedLibraryVideo=matches.FirstOrDefault(x=>
                x.VideoPath.Equals(current,StringComparison.OrdinalIgnoreCase)) ??
                matches.FirstOrDefault();
            LibraryStatus=$"{matches.Count} finished MP4(s) found. Select a video to preview, open or archive its project.";
        }
        catch(Exception ex)
        {
            LibraryStatus="Library scan failed: "+ex.Message;
        }
    }

    private static string FindProjectDirectory(string path,string root)
    {
        var cursor=new DirectoryInfo(Path.GetDirectoryName(path)!);
        var rootFull=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        while(cursor is not null &&
              cursor.FullName.StartsWith(rootFull+Path.DirectorySeparatorChar,
                  StringComparison.OrdinalIgnoreCase))
        {
            if(File.Exists(Path.Combine(cursor.FullName,"project.json")))
                return cursor.FullName;
            cursor=cursor.Parent;
        }
        return Path.GetDirectoryName(path)!;
    }

    private List<string> GetArchivableFiles(VideoLibraryEntry video)
    {
        var root=Path.GetFullPath(Settings.ProjectsRoot).TrimEnd(Path.DirectorySeparatorChar);
        var dir=Path.GetFullPath(video.ProjectDirectory).TrimEnd(Path.DirectorySeparatorChar);
        if(dir.Equals(root,StringComparison.OrdinalIgnoreCase) ||
           !dir.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) ||
           !File.Exists(Path.Combine(dir,"project.json")))
            throw new InvalidOperationException("Only a valid project folder inside Projects Root may be archived.");
        return Directory.EnumerateFiles(dir,"*",SearchOption.AllDirectories)
            .Where(path=>!path.EndsWith(".tmp",StringComparison.OrdinalIgnoreCase))
            .Where(path=>!new FileInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint))
            .ToList();
    }
    private void PreviewLibraryArchive()
    {
        try
        {
            if(SelectedLibraryVideo is not { } v)
            {
                FileManagementStatus="Select a video in the library first.";
                return;
            }
            var files=GetArchivableFiles(v);
            var bytes=files.Sum(x=>new FileInfo(x).Length);
            FileManagementStatus=$"Archive preview: {files.Count} files, {bytes/1048576.0:N1} MB, " +
                $"project '{Path.GetFileName(v.ProjectDirectory)}'. " +
                "The ZIP will include project.json, scenes, audio and exports. " +
                "No files will be moved or removed.";
        }
        catch(Exception ex) { FileManagementStatus="Archive preview: "+ex.Message; }
    }

    private async Task ArchiveLibraryProjectAsync()
    {
        if(SelectedLibraryVideo is not { } v)
        {
            FileManagementStatus="Select a library video first.";
            return;
        }
        if(ShortsRunning||IsBatchPublishing)
        {
            FileManagementStatus="Wait for active production/publishing before archiving.";
            return;
        }
        List<string> files;
        try { files=GetArchivableFiles(v); }
        catch(Exception ex) { FileManagementStatus=ex.Message; return; }
        var bytes=files.Sum(x=>new FileInfo(x).Length);
        var target=Path.Combine(Settings.ProjectsRoot,"_ZeroVideoArchives",
            Path.GetFileName(v.ProjectDirectory)+"-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".zip");
        if(MessageBox.Show($"Create a COPY-ONLY archive?\n\n{files.Count} files, " +
            $"{bytes/1048576.0:N1} MB\n{v.ProjectDirectory}\n\nDestination: {target}\n\n" +
            "This will NOT delete, relocate, or change any original project files.",
            "Confirm project archive",MessageBoxButton.YesNo,
            MessageBoxImage.Question)!=MessageBoxResult.Yes)
            return;
        FileManagementStatus="Creating verified project archive...";
        var temporary=target+".partial";
        try
        {
            await Task.Run(()=>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using(var archive=ZipFile.Open(temporary,ZipArchiveMode.Create))
                foreach(var file in files)
                {
                    _cts.Token.ThrowIfCancellationRequested();
                    var inside=Path.GetRelativePath(v.ProjectDirectory,file);
                    archive.CreateEntryFromFile(file,inside,CompressionLevel.Optimal);
                }
                using(var check=ZipFile.OpenRead(temporary))
                {
                    if(check.Entries.Count!=files.Count)
                        throw new InvalidDataException("Archive entry count did not match the source.");
                    foreach(var entry in check.Entries)
                    {
                        _cts.Token.ThrowIfCancellationRequested();
                        if(entry.Length<0)throw new InvalidDataException("Invalid ZIP entry length.");
                    }
                }
                File.Move(temporary,target);
            },_cts.Token);
            FileManagementStatus="Verified archive created: "+target+
                ". Original project files and publishing queue were left untouched.";
        }
        catch(Exception ex)
        {
            try { if(File.Exists(temporary))File.Delete(temporary); }catch { }
            FileManagementStatus="Archive failed; original files unchanged: "+ex.Message;
        }
    }
}
