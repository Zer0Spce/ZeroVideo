using System.Collections.Concurrent;
using System.Diagnostics;

namespace ZeroVideo.App.Services;

public sealed class ProcessRunner
{
    private readonly ConcurrentDictionary<int,Process> _active=new();

    public void StopActiveProcesses()
    {
        // Only processes launched by this runner are touched (FFmpeg and
        // dependency checks). Never enumerate or kill unrelated applications.
        foreach(var proc in _active.Values)
            try { if(!proc.HasExited)proc.Kill(entireProcessTree:true); }
            catch(InvalidOperationException) { }
            catch(System.ComponentModel.Win32Exception) { }
    }

    public async Task<(int ExitCode,string StdOut,string StdErr)> RunAsync(
        string file,string arguments,string? workingDirectory=null,CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        using var process=new Process
        {
            StartInfo=new ProcessStartInfo
            {
                FileName=file,Arguments=arguments,
                WorkingDirectory=workingDirectory??Environment.CurrentDirectory,
                UseShellExecute=false,RedirectStandardOutput=true,
                RedirectStandardError=true,CreateNoWindow=true
            }
        };
        if(!process.Start())throw new InvalidOperationException("Could not start "+file);
        _active[process.Id]=process;
        using var registration=ct.Register(()=> {
            try { if(!process.HasExited)process.Kill(entireProcessTree:true); }
            catch(InvalidOperationException) { }
            catch(System.ComponentModel.Win32Exception) { }
        });
        try
        {
            var stdout=process.StandardOutput.ReadToEndAsync(ct);
            var stderr=process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            return (process.ExitCode,await stdout,await stderr);
        }
        finally
        {
            _active.TryRemove(process.Id,out _);
            // A canceled WaitForExitAsync does not terminate the child by
            // itself. Ensure no orphaned FFmpeg continues after cancellation.
            if(ct.IsCancellationRequested)
                try { if(!process.HasExited)process.Kill(entireProcessTree:true); }
                catch { }
        }
    }
}
