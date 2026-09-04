using System.Text.Json;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>Persists TTS preferences separately from the general app settings.</summary>
public sealed class TtsSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly AppPaths _paths;

    public TtsSettingsStore(AppPaths paths)
    {
        _paths = paths;
    }

    private string SettingsPath => Path.Combine(_paths.Data, "tts-settings.json");

    public async Task<TtsSettings> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        if (!File.Exists(SettingsPath)) return new TtsSettings();

        try
        {
            await using var stream = new FileStream(
                SettingsPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                useAsync: true);
            var persisted = await JsonSerializer.DeserializeAsync<PersistedTtsSettings>(
                stream,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            if (persisted is null) return new TtsSettings();

            return TtsSettings.Normalize(new TtsSettings
            {
                Provider = persisted.Provider ?? TtsSettings.DefaultProvider,
                Model = persisted.Model ?? string.Empty,
                Voice = string.IsNullOrWhiteSpace(persisted.Voice)
                    ? persisted.MicrosoftVoice ?? TtsOptions.DefaultVoice
                    : persisted.Voice,
                AudioFormat = persisted.AudioFormat ?? TtsOptions.DefaultAudioFormat,
                SampleRate = persisted.SampleRate,
                Speed = persisted.Speed,
                Volume = persisted.Volume,
                Pitch = persisted.Pitch,
                AutoAdvance = persisted.AutoAdvance,
                MaxCharactersPerRequest = persisted.MaxCharactersPerRequest,
                PrefetchCount = persisted.PrefetchCount,
            });
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or System.ComponentModel.Win32Exception)
        {
            return new TtsSettings();
        }
    }

    public async Task SaveAsync(
        TtsSettings settings,
        CancellationToken cancellationToken = default)
    {
        _paths.EnsureDirectories();
        var normalized = TtsSettings.Normalize(settings);
        var persisted = new PersistedTtsSettings
        {
            Provider = normalized.Provider,
            Model = normalized.Model,
            Voice = normalized.Voice,
            AudioFormat = normalized.AudioFormat,
            SampleRate = normalized.SampleRate,
            Speed = normalized.Speed,
            Volume = normalized.Volume,
            Pitch = normalized.Pitch,
            AutoAdvance = normalized.AutoAdvance,
            MaxCharactersPerRequest = normalized.MaxCharactersPerRequest,
            PrefetchCount = normalized.PrefetchCount,
        };

        var temporaryPath = SettingsPath + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        persisted,
                        JsonOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporaryPath, SettingsPath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch
            {
            }

            throw;
        }
    }

    private sealed class PersistedTtsSettings
    {
        public string? Provider { get; set; }
        public string? Model { get; set; }
        public string? Voice { get; set; }

        // Read-only migration field for the previous Windows-only prototype.
        public string? MicrosoftVoice { get; set; }

        public string? AudioFormat { get; set; }
        public int SampleRate { get; set; }

        public double Speed { get; set; } = 1.0;
        public int Volume { get; set; } = 100;
        public int Pitch { get; set; }
        public bool AutoAdvance { get; set; } = true;
        public int MaxCharactersPerRequest { get; set; } = TtsTextSegmenter.DefaultMaximumCharacters;
        public int PrefetchCount { get; set; } = 2;
    }
}
