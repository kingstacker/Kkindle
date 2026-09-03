using System.Diagnostics;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// Invokes the pipx-installed edge-tts command on both Windows and Linux.
/// The engine only creates temporary MP3 files; caching and playback belong to
/// the layers above it.
/// </summary>
public sealed class EdgeTtsEngine : ITtsEngine
{
    public const string DefaultExecutableName = "edge-tts";
    public const string InstallationMessage =
        "未检测到 edge-tts。\n\n"
        + "Debian/Ubuntu 可以执行：\n"
        + "sudo apt install pipx\n"
        + "pipx install edge-tts\n\n"
        + "Windows PowerShell 可以执行：\n"
        + "py -m pip install --user pipx\n"
        + "py -m pipx ensurepath\n"
        + "pipx install edge-tts\n"
        + "（重新打开终端后再启动软件。）";

    private static readonly Regex VoiceNamePattern = new(
        @"(?<![\w-])(?<voice>[A-Za-z]{2,3}-[A-Za-z]{2,3}-[A-Za-z0-9]+(?:-[A-Za-z0-9]+)*)(?![\w-])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static IReadOnlyList<TtsVoiceInfo> PresetVoices { get; } =
    [
        new("zh-CN-XiaoxiaoNeural", "晓晓", "zh-CN"),
        new("zh-CN-XiaoyiNeural", "晓伊", "zh-CN"),
        new("zh-CN-YunxiNeural", "云希", "zh-CN"),
        new("zh-CN-YunjianNeural", "云健", "zh-CN"),
    ];

    private readonly object _gate = new();
    private readonly TimeSpan _processTimeout;
    private readonly string? _configuredExecutable;
    private string? _resolvedExecutable;
    private bool _disposed;

    public EdgeTtsEngine(
        TimeSpan? processTimeout = null,
        string? executablePath = null)
    {
        _processTimeout = processTimeout ?? TimeSpan.FromSeconds(90);
        if (_processTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(processTimeout));
        _configuredExecutable = string.IsNullOrWhiteSpace(executablePath)
            ? null
            : executablePath.Trim();
    }

    public string Id => TtsSettings.DefaultProvider;

    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return !_disposed
                    && (_resolvedExecutable is not null
                        || FindExecutableOnPath(_configuredExecutable ?? DefaultExecutableName) is not null);
            }
        }
    }

    public async Task<TtsAvailability> CheckAvailabilityAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            var executable = await ResolveExecutableAsync(
                refresh: true,
                cancellationToken).ConfigureAwait(false);
            if (executable is null)
            {
                return new TtsAvailability(false, InstallationMessage);
            }

            var result = await RunProcessAsync(
                executable,
                ["--version"],
                _processTimeout,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (result.TimedOut)
            {
                return new TtsAvailability(
                    false,
                    $"edge-tts 检测超时（{_processTimeout.TotalSeconds:0} 秒）。",
                    executable);
            }

            if (result.ExitCode != 0)
            {
                return new TtsAvailability(
                    false,
                    FormatProcessError(
                        result.StandardError,
                        "edge-tts --version 执行失败。"),
                    executable);
            }

            var version = FirstNonEmptyLine(result.StandardOutput, result.StandardError)
                ?? "未知版本";
            lock (_gate) _resolvedExecutable = executable;
            return new TtsAvailability(
                true,
                $"edge-tts 已就绪：{version}",
                executable,
                version);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return new TtsAvailability(
                false,
                $"检测 edge-tts 失败：{exception.Message}");
        }
    }

    public async Task<IReadOnlyList<TtsVoiceInfo>> GetVoicesAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var availability = await CheckAvailabilityAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!availability.IsAvailable || string.IsNullOrWhiteSpace(availability.ExecutablePath))
            return [];

        var result = await RunProcessAsync(
            availability.ExecutablePath,
            ["--list-voices"],
            _processTimeout,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        if (result.TimedOut)
        {
            throw new InvalidOperationException(
                $"获取 edge-tts 语音列表超时（{_processTimeout.TotalSeconds:0} 秒）。");
        }

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                FormatProcessError(result.StandardError, "edge-tts --list-voices 执行失败。"));
        }

        return ParseVoiceList(result.StandardOutput + Environment.NewLine + result.StandardError);
    }

    public async Task<TtsResult> SynthesizeAsync(
        string text,
        TtsOptions options,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        var normalized = TtsOptions.Normalize(options);
        var executable = await ResolveExecutableAsync(
            refresh: false,
            cancellationToken).ConfigureAwait(false);
        if (executable is null)
            return TtsResult.Failure(InstallationMessage);

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "kkindle-tts");
        var outputPath = Path.Combine(
            temporaryDirectory,
            $"{Guid.NewGuid():N}.mp3");

        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            var arguments = new[]
            {
                "--voice",
                normalized.Voice,
                $"--rate={normalized.RateArgument}",
                $"--pitch={normalized.PitchArgument}",
                $"--volume={normalized.VolumeArgument}",
                "--text",
                text,
                "--write-media",
                outputPath,
            };
            var result = await RunProcessAsync(
                executable,
                arguments,
                _processTimeout,
                cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            if (result.TimedOut)
            {
                DeleteTemporaryFile(outputPath);
                return TtsResult.Failure(
                    $"edge-tts 生成语音超时（{_processTimeout.TotalSeconds:0} 秒）。");
            }

            if (result.ExitCode != 0)
            {
                DeleteTemporaryFile(outputPath);
                return TtsResult.Failure(
                    FormatProcessError(result.StandardError, "edge-tts 生成语音失败。"));
            }

            if (!File.Exists(outputPath)
                || new FileInfo(outputPath).Length <= 0)
            {
                DeleteTemporaryFile(outputPath);
                return TtsResult.Failure(
                    "edge-tts 执行成功，但没有生成有效的 MP3 文件。");
            }

            return TtsResult.Success(outputPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DeleteTemporaryFile(outputPath);
            throw;
        }
        catch (Exception exception)
        {
            DeleteTemporaryFile(outputPath);
            return TtsResult.Failure($"调用 edge-tts 失败：{exception.Message}");
        }
    }

    private async Task<string?> ResolveExecutableAsync(
        bool refresh,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_disposed && !refresh && _resolvedExecutable is not null)
                return _resolvedExecutable;
        }

        var configured = _configuredExecutable;
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var configuredPath = FindExecutableOnPath(configured);
            if (configuredPath is not null)
            {
                lock (_gate) _resolvedExecutable = configuredPath;
                return configuredPath;
            }
        }

        // A configured path is the first choice for the app-local pipx
        // installation. If it has not been created yet, still search the
        // normal PATH for a user/system installation instead of asking
        // where.exe/which to resolve a missing absolute path.
        var lookupName = !string.IsNullOrWhiteSpace(configured)
            && !Path.IsPathRooted(configured)
            ? configured
            : DefaultExecutableName;
        var resolver = OperatingSystem.IsWindows() ? "where.exe" : "which";
        try
        {
            var lookup = await RunProcessAsync(
                resolver,
                [lookupName],
                TimeSpan.FromSeconds(5),
                cancellationToken).ConfigureAwait(false);
            if (!lookup.TimedOut && lookup.ExitCode == 0)
            {
                var path = lookup.StandardOutput
                    .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.Trim())
                    .FirstOrDefault(line => File.Exists(line));
                if (path is not null)
                {
                    path = Path.GetFullPath(path);
                    lock (_gate) _resolvedExecutable = path;
                    return path;
                }
            }
        }
        catch (Win32Exception)
        {
            // The direct PATH probe below still works when which/where is not
            // available in a minimal environment.
        }

        var fallback = FindExecutableOnPath(lookupName);
        if (fallback is not null)
        {
            lock (_gate) _resolvedExecutable = fallback;
        }

        return fallback;
    }

    private static IReadOnlyList<TtsVoiceInfo> ParseVoiceList(string output)
    {
        var voices = new Dictionary<string, TtsVoiceInfo>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var voice in PresetVoices)
            voices[voice.Id] = voice;

        foreach (Match match in VoiceNamePattern.Matches(output))
        {
            var id = match.Groups["voice"].Value;
            var parts = id.Split('-');
            var culture = parts.Length >= 2
                ? $"{parts[0]}-{parts[1]}"
                : null;
            if (!voices.ContainsKey(id))
            {
                voices[id] = new TtsVoiceInfo(id, id, culture);
            }
        }

        var presetOrder = PresetVoices
            .Select((voice, index) => (voice.Id, index))
            .ToDictionary(
                item => item.Id,
                item => item.index,
                StringComparer.OrdinalIgnoreCase);
        return voices.Values
            .OrderBy(voice => presetOrder.TryGetValue(voice.Id, out var index)
                ? index
                : int.MaxValue)
            .ThenBy(voice => voice.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string? FindExecutableOnPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (Path.IsPathRooted(name))
            return File.Exists(name) ? Path.GetFullPath(name) : null;

        var pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue)) return null;

        string[] names = OperatingSystem.IsWindows()
            ? [name, $"{name}.exe", $"{name}.cmd", $"{name}.bat"]
            : [name];
        foreach (var directory in pathValue.Split(
                     Path.PathSeparator,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var candidateName in names)
            {
                try
                {
                    var candidate = Path.Combine(directory.Trim(), candidateName);
                    if (File.Exists(candidate)) return Path.GetFullPath(candidate);
                }
                catch
                {
                    // Ignore malformed PATH entries.
                }
            }
        }

        return null;
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var standardOutput = process.StandardOutput.ReadToEndAsync();
        var standardError = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync();

        try
        {
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(exitTask, timeoutTask).ConfigureAwait(false);
            if (completed != exitTask)
            {
                KillProcess(process);
                await WaitForExitSafelyAsync(exitTask).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                return new ProcessResult(
                    -1,
                    await standardOutput.ConfigureAwait(false),
                    await standardError.ConfigureAwait(false),
                    true);
            }

            await exitTask.ConfigureAwait(false);
        }
        catch
        {
            KillProcess(process);
            await WaitForExitSafelyAsync(exitTask).ConfigureAwait(false);
            throw;
        }

        return new ProcessResult(
            process.ExitCode,
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false),
            false);
    }

    private static async Task WaitForExitSafelyAsync(Task exitTask)
    {
        try { await exitTask.ConfigureAwait(false); }
        catch { }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The process may have exited between HasExited and Kill.
        }
    }

    private static string? FirstNonEmptyLine(params string?[] values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries))
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);

    private static string FormatProcessError(string? standardError, string fallback)
    {
        var detail = standardError?.Trim();
        if (string.IsNullOrWhiteSpace(detail)) return fallback;
        if (detail.Length > 2000) detail = detail[^2000..];
        return $"{fallback}\n{detail}";
    }

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Cleanup must never replace the original TTS error.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _resolvedExecutable = null;
        }
    }

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool TimedOut);
}
