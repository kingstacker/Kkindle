using System.Runtime.InteropServices;
using Kkindle.Core;

namespace Kkindle.Platform.MacOS;

/// <summary>
/// Plays cached MP3 files in-process with AVFoundation's <c>AVAudioPlayer</c>.
/// </summary>
/// <remarks>
/// The previous backend ran <c>/usr/bin/afplay</c> and paused it with
/// SIGSTOP/SIGCONT. While a child process was stopped, every other
/// <c>Process.Start</c> in the app stalled until playback resumed (Keychain
/// access via <c>security</c>, <c>diskutil</c> eject, edge-tts, updates), and a
/// crash during a pause left a frozen afplay behind. Playing in-process avoids
/// both: there is no child process to stop or orphan.
/// All AVAudioPlayer calls are serialized under <see cref="_gate"/>.
/// </remarks>
public sealed class MacOSTtsAudioPlayer : ITtsAudioPlayer
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private readonly object _gate = new();
    private Playback? _current;
    private bool _disposed;

    private sealed class Playback(IntPtr player)
    {
        public IntPtr Player { get; } = player;
        public TaskCompletionSource<object?> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Timer? Timer { get; set; }
        public bool Paused { get; set; }
    }

    public bool IsAvailable =>
        !_disposed
        && OperatingSystem.IsMacOS()
        && AVFoundation.IsAvailable;

    public string? UnavailableReason
        => OperatingSystem.IsMacOS()
            ? "无法加载 macOS 系统音频框架 AVFoundation。"
            : "macOS 音频播放器只能在 macOS 上使用。";

    public bool IsPaused
    {
        get
        {
            lock (_gate) return _current?.Paused == true;
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
        if (!AVFoundation.IsAvailable)
            throw new InvalidOperationException(UnavailableReason);
        if (!File.Exists(audioPath))
            throw new FileNotFoundException("TTS 音频文件不存在。", audioPath);
        cancellationToken.ThrowIfCancellationRequested();

        Stop();

        Playback playback;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            playback = new Playback(AVFoundation.CreatePlayer(Path.GetFullPath(audioPath)));
            _current = playback;
            if (AVFoundation.Play(playback.Player))
                playback.Timer = new Timer(_ => Poll(playback), null, PollInterval, PollInterval);
        }

        if (playback.Timer is null)
        {
            Finish(playback, new InvalidOperationException("无法开始播放 TTS 音频。"));
            return playback.Completion.Task;
        }

        if (cancellationToken.CanBeCanceled)
        {
            var registration = cancellationToken.Register(() =>
                Finish(playback, new OperationCanceledException(cancellationToken)));
            _ = playback.Completion.Task.ContinueWith(
                _ => registration.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        return playback.Completion.Task;
    }

    public void Pause()
    {
        lock (_gate)
        {
            var playback = _current;
            if (playback is null || playback.Paused) return;
            AVFoundation.Pause(playback.Player);
            playback.Paused = true;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            var playback = _current;
            if (playback is null || !playback.Paused) return;
            if (AVFoundation.Play(playback.Player))
                playback.Paused = false;
        }
    }

    public void Stop()
    {
        Playback? playback;
        lock (_gate) playback = _current;
        if (playback is not null)
            Finish(playback, new OperationCanceledException());
    }

    private void Poll(Playback playback)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_current, playback) || playback.Paused) return;
            if (AVFoundation.IsPlaying(playback.Player)) return;
        }
        // Not paused and no longer playing: the segment reached its end.
        Finish(playback, null);
    }

    private void Finish(Playback playback, Exception? error)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_current, playback)) return;
            _current = null;
            playback.Timer?.Dispose();
            AVFoundation.StopAndRelease(playback.Player);
        }

        if (error is OperationCanceledException)
            playback.Completion.TrySetCanceled();
        else if (error is not null)
            playback.Completion.TrySetException(error);
        else
            playback.Completion.TrySetResult(null);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
    }

    /// <summary>Minimal Objective-C bridge to <c>AVAudioPlayer</c>.</summary>
    private static class AVFoundation
    {
        private const string ObjC = "/usr/lib/libobjc.A.dylib";
        private const string FrameworkPath = "/System/Library/Frameworks/AVFoundation.framework/AVFoundation";

        private static readonly Lazy<bool> Loaded = new(() =>
            OperatingSystem.IsMacOS()
            && NativeLibrary.TryLoad(FrameworkPath, out _)
            && objc_getClass("AVAudioPlayer") != IntPtr.Zero);

        public static bool IsAvailable => Loaded.Value;

        public static IntPtr CreatePlayer(string fullPath)
        {
            var pool = objc_autoreleasePoolPush();
            try
            {
                var nsPath = SendStr(objc_getClass("NSString"), Sel("stringWithUTF8String:"), fullPath);
                var url = SendPtr(objc_getClass("NSURL"), Sel("fileURLWithPath:"), nsPath);
                var allocated = Send(objc_getClass("AVAudioPlayer"), Sel("alloc"));
                var player = SendPtrError(allocated, Sel("initWithContentsOfURL:error:"), url, out var error);
                if (player == IntPtr.Zero)
                    throw new InvalidDataException($"无法读取 TTS 音频：{Describe(error)}");
                SendBool(player, Sel("prepareToPlay"));
                return player;
            }
            finally
            {
                objc_autoreleasePoolPop(pool);
            }
        }

        public static bool Play(IntPtr player) => WithPool(() => SendBool(player, Sel("play")));

        public static bool IsPlaying(IntPtr player) => WithPool(() => SendBool(player, Sel("isPlaying")));

        public static void Pause(IntPtr player) => WithPool(() =>
        {
            SendVoid(player, Sel("pause"));
            return true;
        });

        public static void StopAndRelease(IntPtr player) => WithPool(() =>
        {
            SendVoid(player, Sel("stop"));
            SendVoid(player, Sel("release"));
            return true;
        });

        private static T WithPool<T>(Func<T> action)
        {
            var pool = objc_autoreleasePoolPush();
            try { return action(); }
            finally { objc_autoreleasePoolPop(pool); }
        }

        private static string Describe(IntPtr error)
        {
            if (error == IntPtr.Zero) return "未知错误";
            var text = Send(Send(error, Sel("localizedDescription")), Sel("UTF8String"));
            return Marshal.PtrToStringUTF8(text) ?? "未知错误";
        }

        private static IntPtr Sel(string name) => sel_registerName(name);

        [DllImport(ObjC, CharSet = CharSet.Ansi)]
        private static extern IntPtr objc_getClass(string name);

        [DllImport(ObjC, CharSet = CharSet.Ansi)]
        private static extern IntPtr sel_registerName(string name);

        [DllImport(ObjC)]
        private static extern IntPtr objc_autoreleasePoolPush();

        [DllImport(ObjC)]
        private static extern void objc_autoreleasePoolPop(IntPtr pool);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendPtr(IntPtr receiver, IntPtr selector, IntPtr argument);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendStr(
            IntPtr receiver,
            IntPtr selector,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string argument);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern IntPtr SendPtrError(
            IntPtr receiver,
            IntPtr selector,
            IntPtr argument,
            out IntPtr error);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.U1)]
        private static extern bool SendBool(IntPtr receiver, IntPtr selector);

        [DllImport(ObjC, EntryPoint = "objc_msgSend")]
        private static extern void SendVoid(IntPtr receiver, IntPtr selector);
    }
}
