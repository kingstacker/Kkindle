using System.Runtime.InteropServices;
using AVFoundation;
using Kkindle.Core;

namespace Kkindle.iOSHead;

/// <summary>
/// Renders speech with AVSpeechSynthesizer into a PCM WAV file so the shared
/// read-aloud service can cache and prefetch sentences exactly as it does on
/// Android. iOS has no downloadable edge-tts voices; system voices are used
/// offline and stay available in airplane mode.
/// </summary>
internal sealed class IOSTtsEngine : ITtsEngine
{
    private static readonly TimeSpan SynthesisTimeout = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly AVSpeechSynthesizer _speech = new();
    private readonly string _directory;
    private bool _disposed;

    public IOSTtsEngine(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(directory);
    }

    public string Id => TtsSettings.DefaultProvider;

    public bool IsAvailable => !_disposed;

    public Task<TtsAvailability> CheckAvailabilityAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var voices = AVSpeechSynthesisVoice.GetSpeechVoices();
        return Task.FromResult(voices.Length > 0
            ? new TtsAvailability(true, "系统语音已就绪。")
            : new TtsAvailability(false, "这台设备没有可用的系统语音，请在系统设置中下载语音后重试。"));
    }

    public Task<IReadOnlyList<TtsVoiceInfo>> GetVoicesAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<TtsVoiceInfo> voices = AVSpeechSynthesisVoice.GetSpeechVoices()
            .Select(voice => new TtsVoiceInfo(
                voice.Identifier,
                string.IsNullOrWhiteSpace(voice.Name) ? voice.Identifier : voice.Name,
                voice.Language ?? string.Empty))
            .Where(voice => voice.Culture?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true
                || voice.Culture?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(voice => voice.Culture, StringComparer.Ordinal)
            .ThenBy(voice => voice.Name, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(voices);
    }

    public async Task<TtsResult> SynthesizeAsync(
        string text,
        TtsOptions options,
        CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return TtsResult.Failure("系统语音不可用。");
        }

        // One utterance at a time: WriteUtterance buffers belong to the
        // synthesizer until its callback completes.
        await _gate.WaitAsync(cancellationToken);
        var id = Guid.NewGuid().ToString("N");
        var path = Path.Combine(_directory, id + ".wav");
        try
        {
            var utterance = new AVSpeechUtterance(text)
            {
                Voice = SelectVoice(options.Voice),
                Rate = MapRate(options.RatePercent),
                PitchMultiplier = Math.Clamp(1f + options.PitchHz / 100f, 0.5f, 2f),
                Volume = Math.Clamp((options.VolumePercent + 100) / 100f, 0f, 1f),
            };

            var audio = new PcmCollector();
            var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _speech.WriteUtterance(utterance, buffer =>
            {
                if (done.Task.IsCompleted)
                {
                    return;
                }

                try
                {
                    if (buffer is not AVAudioPcmBuffer pcm || pcm.FrameLength == 0)
                    {
                        audio.WriteWav(path);
                        done.TrySetResult(audio.FrameCount > 0);
                        return;
                    }

                    audio.Append(pcm);
                }
                catch (Exception exception)
                {
                    done.TrySetException(exception);
                }
            });

            using var registration = cancellationToken.Register(() => done.TrySetCanceled(cancellationToken));
            var succeeded = await done.Task.WaitAsync(SynthesisTimeout, cancellationToken);
            return succeeded && File.Exists(path)
                ? TtsResult.Success(path, "wav")
                : TtsResult.Failure("系统语音合成失败，请稍后重试。");
        }
        catch (TimeoutException)
        {
            return TtsResult.Failure("系统语音合成超时。");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static AVSpeechSynthesisVoice? SelectVoice(string requested)
    {
        if (!string.IsNullOrWhiteSpace(requested))
        {
            var exact = AVSpeechSynthesisVoice.FromIdentifier(requested);
            if (exact is not null)
            {
                return exact;
            }

            // Desktop voices are edge-tts names such as zh-CN-XiaoxiaoNeural.
            // Their locale prefix still picks a sensible installed voice.
            var parts = requested.Split('-');
            if (parts.Length >= 2)
            {
                var language = $"{parts[0]}-{parts[1]}";
                var match = AVSpeechSynthesisVoice.FromLanguage(language);
                if (match is not null)
                {
                    return match;
                }
            }
        }

        return AVSpeechSynthesisVoice.FromLanguage("zh-CN")
            ?? AVSpeechSynthesisVoice.FromLanguage("zh-TW")
            ?? AVSpeechSynthesisVoice.GetSpeechVoices().FirstOrDefault();
    }

    private static float MapRate(int ratePercent)
    {
        var multiplier = Math.Clamp(1f + ratePercent / 100f, 0.5f, 2f);
        return Math.Clamp(
            AVSpeechUtterance.DefaultSpeechRate * multiplier,
            AVSpeechUtterance.MinimumSpeechRate,
            AVSpeechUtterance.MaximumSpeechRate);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _speech.StopSpeaking(AVSpeechBoundary.Immediate);
        _speech.Dispose();
        _gate.Dispose();
    }

    /// <summary>
    /// Accumulates AVSpeechSynthesizer PCM buffers into 16-bit mono samples and
    /// writes a canonical RIFF/WAVE file. The synthesizer emits float buffers
    /// on current iOS versions, but all common formats are accepted.
    /// </summary>
    private sealed class PcmCollector
    {
        private readonly MemoryStream _samples = new();
        private readonly BinaryWriter _writer;
        private int _sampleRate;
        private int _frameCount;

        public PcmCollector()
        {
            _writer = new BinaryWriter(_samples);
        }

        public long FrameCount => _frameCount;

        public void Append(AVAudioPcmBuffer buffer)
        {
            var format = buffer.Format;
            var frames = checked((int)buffer.FrameLength);
            var channels = Math.Max(1, checked((int)format.ChannelCount));
            if (frames <= 0)
            {
                return;
            }

            _sampleRate = _sampleRate == 0 ? (int)Math.Round(format.SampleRate) : _sampleRate;
            var interleaved = format.Interleaved;
            var samples = interleaved ? frames * channels : frames;
            var mixed = new float[frames];
            for (var channel = 0; channel < channels; channel++)
            {
                ReadChannel(buffer, format, channel, interleaved, samples, mixed, channels);
            }

            for (var frame = 0; frame < frames; frame++)
            {
                var value = Math.Clamp(mixed[frame] / channels, -1f, 1f);
                _writer.Write((short)MathF.Round(value * short.MaxValue));
            }

            _frameCount += frames;
        }

        private static void ReadChannel(
            AVAudioPcmBuffer buffer,
            AVAudioFormat format,
            int channel,
            bool interleaved,
            int sampleCount,
            float[] mixed,
            int channels)
        {
            var pointer = ChannelData(buffer, format, channel);
            if (pointer == IntPtr.Zero)
            {
                return;
            }

            switch (format.CommonFormat)
            {
                case AVAudioCommonFormat.PCMInt16:
                {
                    var samples = new short[sampleCount];
                    Marshal.Copy(pointer, samples, 0, sampleCount);
                    for (var frame = 0; frame < mixed.Length; frame++)
                    {
                        var index = interleaved ? frame * channels + channel : frame;
                        mixed[frame] += samples[index] / (float)short.MaxValue;
                    }

                    break;
                }
                case AVAudioCommonFormat.PCMInt32:
                {
                    var samples = new int[sampleCount];
                    Marshal.Copy(pointer, samples, 0, sampleCount);
                    for (var frame = 0; frame < mixed.Length; frame++)
                    {
                        var index = interleaved ? frame * channels + channel : frame;
                        mixed[frame] += samples[index] / 2147483648f;
                    }

                    break;
                }
                case AVAudioCommonFormat.PCMFloat64:
                {
                    var samples = new double[sampleCount];
                    Marshal.Copy(pointer, samples, 0, sampleCount);
                    for (var frame = 0; frame < mixed.Length; frame++)
                    {
                        var index = interleaved ? frame * channels + channel : frame;
                        mixed[frame] += (float)samples[index];
                    }

                    break;
                }
                default:
                {
                    var samples = new float[sampleCount];
                    Marshal.Copy(pointer, samples, 0, sampleCount);
                    for (var frame = 0; frame < mixed.Length; frame++)
                    {
                        var index = interleaved ? frame * channels + channel : frame;
                        mixed[frame] += samples[index];
                    }

                    break;
                }
            }
        }

        private static IntPtr ChannelData(
            AVAudioPcmBuffer buffer,
            AVAudioFormat format,
            int channel)
        {
            var list = format.CommonFormat switch
            {
                AVAudioCommonFormat.PCMInt16 => buffer.Int16ChannelData,
                AVAudioCommonFormat.PCMInt32 => buffer.Int32ChannelData,
                _ => buffer.FloatChannelData,
            };
            return list == IntPtr.Zero
                ? IntPtr.Zero
                : Marshal.ReadIntPtr(list, channel * IntPtr.Size);
        }

        public void WriteWav(string path)
        {
            if (_frameCount == 0 || _sampleRate == 0)
            {
                return;
            }

            var dataLength = checked((int)_samples.Length);
            using var file = File.Create(path);
            using var writer = new BinaryWriter(file);
            const short channels = 1;
            const short bitsPerSample = 16;

            writer.Write("RIFF"u8.ToArray());
            writer.Write(36 + dataLength);
            writer.Write("WAVE"u8.ToArray());
            writer.Write("fmt "u8.ToArray());
            writer.Write(16);
            writer.Write((short)1); // PCM
            writer.Write(channels);
            writer.Write(_sampleRate);
            writer.Write(_sampleRate * channels * bitsPerSample / 8);
            writer.Write((short)(channels * bitsPerSample / 8));
            writer.Write(bitsPerSample);
            writer.Write("data"u8.ToArray());
            writer.Write(dataLength);
            _samples.Position = 0;
            _samples.CopyTo(file);
            writer.Flush();
        }
    }
}
