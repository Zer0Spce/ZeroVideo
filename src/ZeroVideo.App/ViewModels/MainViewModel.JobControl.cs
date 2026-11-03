using System.Windows;
using System.Windows.Input;
using ZeroVideo.App.Infrastructure;

namespace ZeroVideo.App.ViewModels;

public sealed partial class MainViewModel
{
    private bool _jobRunning;
    private bool _isPublishingJob;
    private string _activeJobName = "";
    private string _jobControlStatus = "Ready. Stop cancels safely; Force Stop closes only ZeroVideo automation processes.";
    public bool JobRunning
    {
        get => _jobRunning; private set => Set(ref _jobRunning,value);
    }
    public string JobControlStatus
    {
        get => _jobControlStatus; private set => Set(ref _jobControlStatus,value);
    }
    public ICommand StopJobCommand { get; private set; } = null!;
    public ICommand ForceStopJobCommand { get; private set; } = null!;

    private void InitializeJobControls()
    {
        StopJobCommand=new RelayCommand(RequestStopJob);
        ForceStopJobCommand=new AsyncCommand(ForceStopJobAsync);
    }

    // Every independently started automation enters this gate. Nested stages
    // (Shorts -> Write -> Render -> Metadata) share the same token and are not
    // separately registered. A canceled CTS is NEVER reset while a job runs.
    private async Task RunManagedJobAsync(string name,Func<Task> job,bool publishing=false)
    {
        if(JobRunning)
        {
            JobControlStatus="Already running: "+_activeJobName+
                ". Stop or complete it before starting another automation.";
            return;
        }
        if(_cts.IsCancellationRequested)
        {
            _cts.Dispose();
            _cts=new CancellationTokenSource();
        }
        JobRunning=true;
        _activeJobName=name;
        _isPublishingJob=publishing;
        JobControlStatus="Running: "+name+". Use Stop to cancel, or Force Stop if stuck.";
        try
        {
            await job();
            if(_cts.IsCancellationRequested)
                JobControlStatus="Stopped: "+name+
                    ". Existing project checkpoints and completed media remain on disk.";
            else
                JobControlStatus="Finished: "+name+". Ready for another job.";
        }
        catch(OperationCanceledException)
        {
            JobControlStatus="Canceled: "+name+". Completed work is preserved.";
            Log(JobControlStatus);
        }
        catch(Exception ex)
        {
            JobControlStatus="Failed: "+name+": "+ex.Message;
            Log("Managed job error: "+ex);
        }
        finally
        {
            _activeJobName="";
            _isPublishingJob=false;
            JobRunning=false;
            if(_cts.IsCancellationRequested)
            {
                _cts.Dispose();
                _cts=new CancellationTokenSource();
            }
            RefreshCommands();
        }
    }

    private void RequestStopJob()
    {
        if(!JobRunning)
        {
            JobControlStatus="No active automation to stop.";
            return;
        }
        JobControlStatus="Stop requested: "+_activeJobName+
            ". Current operation will be canceled; no new stage will start.";
        try { _cts.Cancel(); } catch(ObjectDisposedException) { }
        _ttsContinueGate?.TrySetCanceled();
        // FFmpeg's process is stopped as part of cooperative cancellation;
        // partially rendered output is not marked as finished.
        _runner.StopActiveProcesses();
        Log(JobControlStatus);
    }

    private async Task ForceStopJobAsync()
    {
        if(!JobRunning)
        {
            JobControlStatus="No active automation to force stop.";
            return;
        }
        var caution=_isPublishingJob?
            "\n\nIMPORTANT: A TikTok submission might already have been sent. "+
            "Verify the video in TikTok Studio before trying again. "+
            "ZeroVideo will NOT automatically retry or resubmit it.":"";
        if(MessageBox.Show(
            "FORCE STOP '"+_activeJobName+"'?\n\n"+
            "ZeroVideo will cancel its current work, terminate FFmpeg child "+
            "processes, interrupt local ComfyUI generation and close its "+
            "DEDICATED automated Edge session if necessary. "+
            "Your regular Edge windows and saved project media will not "+
            "be targeted. An interrupted render can leave a partial MP4."+
            caution+"\n\nProceed?",
            "Force Stop ZeroVideo automation",
            MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)
            return;

        JobControlStatus="FORCE STOP in progress. Saved checkpoints stay untouched.";
        try { _cts.Cancel(); } catch(ObjectDisposedException) { }
        _ttsContinueGate?.TrySetCanceled();
        _runner.StopActiveProcesses();
        try { await _localImages.InterruptOwnedGenerationAsync(); }
        catch(Exception ex) { Log("Local generation interrupt: "+ex.Message); }
        try { await _browser.ForceStopAutomationAsync(); }
        catch(Exception ex) { Log("Dedicated automation browser stop: "+ex.Message); }
        JobControlStatus=_isPublishingJob?
            "Force stopped. TikTok outcome is UNKNOWN: inspect TikTok Studio "+
            "before another submission. No automatic retry will occur.":
            "Force stop sent. Wait until the active task has exited before restarting.";
        Log(JobControlStatus);
    }
}
