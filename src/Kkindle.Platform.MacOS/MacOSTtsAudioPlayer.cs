using System.Diagnostics;
using System.Runtime.InteropServices;
using Kkindle.Core;

namespace Kkindle.Platform.MacOS;

/// <summary>
/// Plays cached MP3 files with the afplay tool that ships with macOS. Pause
/// and resume suspend the child process with SIGSTOP/SIGCONT, matching the
/// Linux backend's approach and keeping TTS free of extra dependencies.
/// </summary>
public sealed class MacOSTtsAudioPlayer : ITtsAudioPlayer
{
    internal const string DefaultExecutable = "/usr/bin/afplay";

    private readonly object _gate = new();
    private Process? _process;
    private TaskCompletionSource<object?>? _completion;
    private bool _paused;
    private bool _disposed;

    public bool IsAvailable =>
        !_disposed
        && OperatingSystem.IsMacOS()
        && ResolveExecutable() is not null;

    public string? UnavailableReason
        => OperatingSystem.IsMacOS()
            ? "未找到 macOS 系统音频播放器 afplay。"
            : "macOS 音频播放器只能在 macOS 上使用。";

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
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException(UnavailableReason);
        var executable = ResolveExecutable();
        if (executable is null)
            throw new InvalidOperationException(UnavailableReason);
        if (!File.Exists(audioPath))
            throw new FileNotFoundException("TTS 音频文件不存在。", audioPath);
        cancellationToken.ThrowIfCancellationRequested();

        Stop();

        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // afplay derives the decoder from the file extension, so the cached
        // MP3 name is passed as-is.
        startInfo.ArgumentList.Add(audioPath);
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
                throw new InvalidOperationException("无法启动 macOS 音频播放器。");
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

    private static string? ResolveExecutable()
    {
        if (File.Exists(DefaultExecutable)) return DefaultExecutable;

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue)) return null;
        foreach (var directory in pathValue.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim(), "afplay");
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
            catch
            {
            }
        }

        return null;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int processId, int signal);

    // Darwin numbers signals differently from Linux (SIGSTOP is 17 here,
    // SIGCONT is 19), so the constants cannot be shared.
    private const int SigStop = 17;
    private const int SigContinue = 19;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
