namespace Kkindle.Core;

/// <summary>
/// States exposed by the TTS service. Chapter navigation is kept as a
/// separate transient state so the UI can explain why playback is waiting.
/// </summary>
public enum TtsPlaybackState
{
    Stopped,
    Generating,
    Playing,
    Paused,
    Error,
    AdvancingChapter,
}

/// <summary>Options that affect the generated audio and its cache key.</summary>
public sealed record TtsOptions
{
    public const string DefaultVoice = "zh-CN-XiaoxiaoNeural";

    public string Voice { get; init; } = DefaultVoice;
    public int RatePercent { get; init; }
    public int VolumePercent { get; init; }
    public int PitchHz { get; init; }

    public static TtsOptions Normalize(TtsOptions? options)
    {
        options ??= new TtsOptions();
        return new TtsOptions
        {
            Voice = string.IsNullOrWhiteSpace(options.Voice)
                ? DefaultVoice
                : options.Voice.Trim(),
            RatePercent = Math.Clamp(options.RatePercent, -50, 100),
            VolumePercent = Math.Clamp(options.VolumePercent, -100, 100),
            PitchHz = Math.Clamp(options.PitchHz, -100, 100),
        };
    }

    public string RateArgument => SignedPercent(RatePercent);
    public string VolumeArgument => SignedPercent(VolumePercent);
    public string PitchArgument => $"{(PitchHz >= 0 ? "+" : string.Empty)}{PitchHz}Hz";

    private static string SignedPercent(int value)
        => $"{(value >= 0 ? "+" : string.Empty)}{value}%";
}

/// <summary>
/// User-facing settings. Speed is a multiplier because that is easier to
/// understand in the reader UI; it is converted to edge-tts' relative rate
/// argument when a request is made.
/// </summary>
public sealed class TtsSettings
{
    public const string DefaultProvider = "edge-tts";

    public string Provider { get; set; } = DefaultProvider;
    public string Voice { get; set; } = TtsOptions.DefaultVoice;
    public double Speed { get; set; } = 1.0;
    public int Volume { get; set; } = 100;
    public int Pitch { get; set; }
    public bool AutoAdvance { get; set; } = true;
    public int MaxCharactersPerRequest { get; set; } = 420;
    public int PrefetchCount { get; set; } = 2;

    /// <summary>
    /// Legacy property kept for source/settings compatibility with the
    /// earlier Windows-only prototype. New code should use <see cref="Voice"/>.
    /// </summary>
    public string MicrosoftVoice
    {
        get => Voice;
        set => Voice = value;
    }

    public TtsSettings Clone() => Normalize(this);

    public TtsOptions ToOptions()
    {
        var normalized = Normalize(this);
        return TtsOptions.Normalize(new TtsOptions
        {
            Voice = normalized.Voice,
            RatePercent = (int)Math.Round((normalized.Speed - 1.0) * 100.0),
            VolumePercent = normalized.Volume - 100,
            PitchHz = normalized.Pitch,
        });
    }

    public static TtsSettings Normalize(TtsSettings? settings)
    {
        settings ??= new TtsSettings();
        var voice = string.IsNullOrWhiteSpace(settings.Voice)
            ? TtsOptions.DefaultVoice
            : settings.Voice.Trim();
        var speed = double.IsFinite(settings.Speed)
            ? Math.Clamp(settings.Speed, 0.5, 2.0)
            : 1.0;

        return new TtsSettings
        {
            Provider = DefaultProvider,
            Voice = voice,
            Speed = speed,
            Volume = Math.Clamp(settings.Volume, 0, 200),
            Pitch = Math.Clamp(settings.Pitch, -100, 100),
            AutoAdvance = settings.AutoAdvance,
            MaxCharactersPerRequest = Math.Clamp(
                settings.MaxCharactersPerRequest,
                120,
                1200),
            PrefetchCount = Math.Clamp(settings.PrefetchCount, 0, 4),
        };
    }
}

public sealed record TtsVoiceInfo(string Id, string Name, string? Culture = null);

/// <summary>
/// The result of one engine request. Successful engine results normally point
/// at a temporary file; the playback queue moves that file into the cache.
/// </summary>
public sealed record TtsResult(
    bool IsSuccess,
    string? AudioPath,
    string Format,
    string? ErrorMessage = null,
    bool FromCache = false)
{
    public static TtsResult Success(
        string audioPath,
        string format = "mp3",
        bool fromCache = false)
        => new(true, audioPath, format, null, fromCache);

    public static TtsResult Failure(string message)
        => new(false, null, string.Empty, message);
}

public sealed record TtsAvailability(
    bool IsAvailable,
    string Message,
    string? ExecutablePath = null,
    string? Version = null);

/// <summary>Progress reported while the app prepares its TTS dependencies.</summary>
public sealed record TtsSetupProgress(
    string Message,
    bool IsIndeterminate = true,
    double? Percentage = null);

/// <summary>Result of the automatic TTS dependency setup.</summary>
public sealed record TtsSetupResult(
    bool IsSuccess,
    string Message,
    bool ChangedSystem = false);

/// <summary>
/// Installs or repairs the local dependencies required by a TTS engine and
/// audio output. The UI only sees this abstraction; it does not know how a
/// platform package manager works.
/// </summary>
public interface ITtsEnvironmentSetup
{
    Task<TtsSetupResult> EnsureReadyAsync(
        IProgress<TtsSetupProgress>? progress = null,
        CancellationToken cancellationToken = default);
}

public interface ITtsEngine : IDisposable
{
    string Id { get; }

    /// <summary>Fast local check; use CheckAvailabilityAsync for diagnostics.</summary>
    bool IsAvailable { get; }

    Task<TtsAvailability> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TtsVoiceInfo>> GetVoicesAsync(
        CancellationToken cancellationToken = default);

    Task<TtsResult> SynthesizeAsync(
        string text,
        TtsOptions options,
        CancellationToken cancellationToken = default);
}

/// <summary>One item that can be prefetched or played by the queue.</summary>
public sealed record TtsQueueItem(
    string BookKey,
    string ChapterKey,
    TtsTextSegment Segment,
    TtsOptions Options);

/// <summary>One text range sent to the TTS engine.</summary>
public readonly record struct TtsTextSegment(int Start, int Length, string Text)
{
    public int End => Start + Length;
}

/// <summary>Audio output is deliberately separate from the TTS engine.</summary>
public interface ITtsAudioPlayer : IDisposable
{
    bool IsAvailable { get; }
    string? UnavailableReason { get; }
    bool IsPaused { get; }

    Task PlayAsync(
        string audioPath,
        CancellationToken cancellationToken = default);

    void Pause();
    void Resume();
    void Stop();
}

public sealed record TtsCacheStatistics(int FileCount, long TotalBytes);
