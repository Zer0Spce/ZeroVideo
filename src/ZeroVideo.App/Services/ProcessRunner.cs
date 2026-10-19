using System.Diagnostics;

namespace ZeroVideo.App.Services;

public sealed class ProcessRunner
{
    public async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(string file, string arguments, string? workingDirectory = null, CancellationToken ct = default)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = file,
                Arguments = arguments,
                WorkingDirectory = workingDirectory ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, await stdoutTask, await stderrTask);
    }
}
