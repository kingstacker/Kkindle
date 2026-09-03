using System.Diagnostics;
using System.Runtime.InteropServices;
using Kkindle.Core;

namespace Kkindle.Platform.Linux;

/// <summary>
/// Plays cached MP3 files with an installed native desktop player. mpv is
/// preferred, with ffplay/VLC/GStreamer fallbacks. SIGSTOP/SIGCONT provide
/// pause/resume without routing commands through a shell.
/// </summary>
public sealed class LinuxTtsAudioPlayer : ITtsAudioPlayer
{
    private static readonly string[] BackendNames =
    [
        "mpv",
        "ffplay",
        "cvlc",
        "vlc",
        "gst-play-1.0",
    ];

    private readonly object _gate = new();
    private Process? _process;
    private TaskCompletionSource<object?>? _completion;
    private bool _paused;
    private bool _disposed;

    public LinuxTtsAudioPlayer()
    {
    }

    public bool IsAvailable =>
        !_disposed
        && OperatingSystem.IsLinux()
        && ResolveExecutable() is not null;

    public string? UnavailableReason
        => OperatingSystem.IsLinux()
            ? "未找到可播放 MP3 的系统播放器。Debian 可以执行：sudo apt install mpv"
            : "Linux 音频播放器只能在 Linux 上使用。";

    public bool IsPaused
    {
        get
        {
            lock (_gate) return _paused;
        }
    }

    public Task PlayAsync(
        string audioPath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(audioPath);
        var executable = ResolveExecutable();
        if (executable is null)
            throw new InvalidOperationException(UnavailableReason);
        if (!File.Exists(audioPath))
            throw new FileNotFoundException("TTS 音频文件不存在。", audioPath);
        cancellationToken.ThrowIfCancellationRequested();

        Stop();

        var startInfo = BuildStartInfo(executable, audioPath);
        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };
        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _process = process;
            _completion = completion;
            _paused = false;
        }

        process.Exited += (_, _) => Complete(process, null);
        try
        {
            if (!process.Start())
                throw new InvalidOperationException("无法启动 Linux 音频播放器。");
            if (process.HasExited)
                Complete(process, null);
        }
        catch (Exception exception)
        {
            Complete(process, exception);
        }

        CancellationTokenRegistration registration = default;
        registration = cancellationToken.Register(() =>
            Terminate(process, new OperationCanceledException(cancellationToken)));
        _ = completion.Task.ContinueWith(
            _ => registration.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return completion.Task;
    }

    public void Pause()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            if (process is null || _paused) return;
            _paused = true;
        }

        if (kill(process.Id, SigStop) != 0)
        {
            lock (_gate) _paused = false;
        }
    }

    public void Resume()
    {
        Process? process;
        lock (_gate)
        {
            process = _process;
            if (process is null || !_paused) return;
            _paused = false;
        }

        if (kill(process.Id, SigContinue) != 0)
        {
            lock (_gate) _paused = true;
        }
    }

    public void Stop()
    {
        Process? process;
        lock (_gate) process = _process;
        if (process is null) return;
        Terminate(process, new OperationCanceledException());
    }

    private void Terminate(Process process, Exception error)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
        }

        Complete(process, error);
    }

    private void Complete(Process process, Exception? error)
    {
        TaskCompletionSource<object?>? completion;
        lock (_gate)
        {
            if (!ReferenceEquals(_process, process)) return;
            _process = null;
            completion = _completion;
            _completion = null;
            _paused = false;
        }

        try { process.Dispose(); }
        catch { }

        if (completion is null) return;
        if (error is OperationCanceledException)
            completion.TrySetCanceled();
        else if (error is not null)
            completion.TrySetException(error);
        else
            completion.TrySetResult(null);
    }

    private static ProcessStartInfo BuildStartInfo(
        string executable,
        string audioPath)
    {
        var name = Path.GetFileNameWithoutExtension(executable)
            .ToLowerInvariant();
        string[] arguments = name switch
        {
            "mpv" =>
            [
                "--no-video",
                "--really-quiet",
                "--force-window=no",
                "--audio-display=no",
                "--",
                audioPath,
            ],
            "ffplay" =>
            [
                "-nodisp",
                "-autoexit",
                "-loglevel",
                "quiet",
                "-nostdin",
                audioPath,
            ],
            "cvlc" or "vlc" =>
            [
                "--intf",
                "dummy",
                "--play-and-exit",
                "--no-video",
                audioPath,
            ],
            _ => [audioPath],
        };

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        return startInfo;
    }

    private static string? FindExecutableOnPath(string name)
    {
        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue)) return null;
        foreach (var directory in pathValue.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), name);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch
            {
            }
        }

        return null;
    }

    private static string? ResolveExecutable()
        => BackendNames
            .Select(FindExecutableOnPath)
            .FirstOrDefault(path => path is not null);

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);

    private const int SigContinue = 18;
    private const int SigStop = 19;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
