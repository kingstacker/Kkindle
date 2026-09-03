using Kkindle.Core;
using NAudio.Wave;

namespace Kkindle.Platform.Windows;

/// <summary>Windows MP3/WAV output for cached TTS audio.</summary>
public sealed class WindowsTtsAudioPlayer : ITtsAudioPlayer
{
    private readonly object _gate = new();
    private WaveOut? _output;
    private WaveStream? _reader;
    private TaskCompletionSource<object?>? _completion;
    private bool _paused;
    private bool _disposed;

    public bool IsAvailable => !_disposed && OperatingSystem.IsWindows();

    public string? UnavailableReason
        => OperatingSystem.IsWindows()
            ? null
            : "Windows 音频播放器只能在 Windows 上使用。";

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
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException(UnavailableReason);
        if (!File.Exists(audioPath))
            throw new FileNotFoundException("TTS 音频文件不存在。", audioPath);
        cancellationToken.ThrowIfCancellationRequested();

        Stop();

        WaveStream reader = CreateReader(audioPath);
        var output = new WaveOut();
        var completion = new TaskCompletionSource<object?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _output = output;
            _reader = reader;
            _completion = completion;
            _paused = false;
        }

        output.PlaybackStopped += (_, _) => Complete(output, null);
        try
        {
            output.Init(reader);
            output.Play();
        }
        catch (Exception exception)
        {
            Complete(output, exception);
        }

        CancellationTokenRegistration registration = default;
        registration = cancellationToken.Register(() =>
        {
            try { output.Stop(); }
            catch { }
            Complete(output, new OperationCanceledException(cancellationToken));
        });
        _ = completion.Task.ContinueWith(
            _ => registration.Dispose(),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return completion.Task;
    }

    public void Pause()
    {
        WaveOut? output;
        lock (_gate)
        {
            output = _output;
            if (output is null) return;
            _paused = true;
        }

        try { output.Pause(); }
        catch { }
    }

    public void Resume()
    {
        WaveOut? output;
        lock (_gate)
        {
            output = _output;
            if (output is null) return;
            _paused = false;
        }

        try { output.Play(); }
        catch { }
    }

    public void Stop()
    {
        WaveOut? output;
        lock (_gate) output = _output;
        if (output is null) return;

        try { output.Stop(); }
        catch { }
        Complete(output, new OperationCanceledException());
    }

    private void Complete(WaveOut output, Exception? error)
    {
        WaveStream? reader;
        TaskCompletionSource<object?>? completion;
        lock (_gate)
        {
            if (!ReferenceEquals(_output, output)) return;
            _output = null;
            reader = _reader;
            _reader = null;
            completion = _completion;
            _completion = null;
            _paused = false;
        }

        try { output.Dispose(); }
        catch { }
        try { reader?.Dispose(); }
        catch { }

        if (completion is null) return;
        if (error is OperationCanceledException)
            completion.TrySetCanceled();
        else if (error is not null)
            completion.TrySetException(error);
        else
            completion.TrySetResult(null);
    }

    private static WaveStream CreateReader(string path)
    {
        var extension = Path.GetExtension(path);
        if (extension.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".mpeg", StringComparison.OrdinalIgnoreCase))
        {
            return new Mp3FileReader(path);
        }

        if (extension.Equals(".wav", StringComparison.OrdinalIgnoreCase))
            return new WaveFileReader(path);
        throw new InvalidDataException($"不支持的 TTS 音频格式：{extension}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }
}
