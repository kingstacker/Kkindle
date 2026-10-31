using System.Collections.Concurrent;
using Android.Content;
using Android.Speech.Tts;
using Java.Util;
using Kkindle.Core;
using JavaFile = Java.IO.File;

namespace Kkindle.AndroidHead;

/// <summary>
/// Reads aloud with the phone's own text-to-speech engine. The desktop engine
/// runs a Python command line, which Android cannot host; this one renders each
/// sentence to a WAV file so caching and playback stay in the shared service.
/// </summary>
internal sealed class AndroidTtsEngine : ITtsEngine
{
    private static readonly TimeSpan InitTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan SynthesisTimeout = TimeSpan.FromSeconds(60);

    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TextToSpeech _speech;
    private readonly string _directory;
    private bool _disposed;

    public AndroidTtsEngine(Context context, string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
        _speech = new TextToSpeech(context, new InitListener(status =>
            _ready.TrySetResult(status == OperationResult.Success)));
        _speech.SetOnUtteranceProgressListener(new ProgressListener(_pending));
    }

    public string Id => TtsSettings.DefaultProvider;

    public bool IsAvailable => !_disposed && (!_ready.Task.IsCompleted || _ready.Task.Result);

    public async Task<TtsAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        return await IsReadyAsync(cancellationToken)
            ? new TtsAvailability(true, "系统语音已就绪。")
            : new TtsAvailability(false, "这台设备没有可用的文字转语音引擎，请在系统设置中安装后重试。");
    }

    public async Task<IReadOnlyList<TtsVoiceInfo>> GetVoicesAsync(CancellationToken cancellationToken = default)
    {
        if (!await IsReadyAsync(cancellationToken)) return [];
        return (_speech.Voices ?? [])
            .Where(voice => voice.Locale?.Language is "zh" or "en")
            .Select(voice => new TtsVoiceInfo(voice.Name, voice.Name, voice.Locale!.ToLanguageTag()))
            .OrderBy(voice => voice.Name, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<TtsResult> SynthesizeAsync(string text, TtsOptions options, CancellationToken cancellationToken = default)
    {
        if (!await IsReadyAsync(cancellationToken))
            return TtsResult.Failure("这台设备没有可用的文字转语音引擎。");

        // The engine holds one rate, pitch and voice at a time, so requests
        // that set them must not interleave.
        await _gate.WaitAsync(cancellationToken);
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(_directory, id + ".wav");
        try
        {
            Apply(options);
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = done;
            using var bundle = new Android.OS.Bundle();
            if (_speech.SynthesizeToFile(new Java.Lang.String(text), bundle, new JavaFile(path), id)
                != OperationResult.Success)
            {
                return TtsResult.Failure("系统语音合成失败。");
            }

            using var registration = cancellationToken.Register(() => done.TrySetCanceled(cancellationToken));
            var succeeded = await done.Task.WaitAsync(SynthesisTimeout, cancellationToken);
            return succeeded && File.Exists(path)
                ? TtsResult.Success(path, "wav")
                : TtsResult.Failure("系统语音合成失败，可能需要先在系统设置中下载语音数据。");
        }
        finally
        {
            _pending.TryRemove(id, out _);
            _gate.Release();
        }
    }

    private async Task<bool> IsReadyAsync(CancellationToken cancellationToken)
    {
        if (_disposed) return false;
        try
        {
            return await _ready.Task.WaitAsync(InitTimeout, cancellationToken);
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private void Apply(TtsOptions options)
    {
        _speech.SetSpeechRate((float)Math.Clamp(1 + options.RatePercent / 100d, 0.5, 2));
        _speech.SetPitch((float)Math.Clamp(1 + options.PitchHz / 100d, 0.5, 2));

        // Voices saved by the desktop app are edge-tts names; their locale prefix
        // still picks a matching system voice.
        var voice = _speech.Voices?.FirstOrDefault(item => item.Name == options.Voice);
        if (voice is not null)
        {
            _speech.SetVoice(voice);
            return;
        }
        var parts = options.Voice.Split('-');
        var tag = parts.Length >= 2 ? $"{parts[0]}-{parts[1]}" : "zh-CN";
        _speech.SetLanguage(Locale.ForLanguageTag(tag));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var pending in _pending.Values) pending.TrySetCanceled();
        _speech.Stop();
        _speech.Shutdown();
        _speech.Dispose();
    }

    private sealed class InitListener(Action<OperationResult> initialized)
        : Java.Lang.Object, TextToSpeech.IOnInitListener
    {
        public void OnInit(OperationResult status) => initialized(status);
    }

    private sealed class ProgressListener(ConcurrentDictionary<string, TaskCompletionSource<bool>> pending)
        : UtteranceProgressListener
    {
        public override void OnStart(string? utteranceId)
        {
        }

        public override void OnDone(string? utteranceId) => Complete(utteranceId, true);

        [Obsolete("Superseded by the overload with an error code; Android still calls both.")]
        public override void OnError(string? utteranceId) => Complete(utteranceId, false);

        public override void OnError(string? utteranceId, TextToSpeechError errorCode) => Complete(utteranceId, false);

        private void Complete(string? utteranceId, bool succeeded)
        {
            if (utteranceId is not null && pending.TryGetValue(utteranceId, out var done))
                done.TrySetResult(succeeded);
        }
    }
}
