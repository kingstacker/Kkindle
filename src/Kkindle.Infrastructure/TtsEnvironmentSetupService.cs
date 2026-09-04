using System.ComponentModel;
using System.Diagnostics;
using Kkindle.Core;

namespace Kkindle.Infrastructure;

/// <summary>
/// Prepares the external pieces needed by the edge-tts implementation. The
/// installation is deliberately application-local where possible: pipx keeps
/// its virtual environment and command shims below Kkindle's data directory.
/// Only Linux system audio/Python packages require an OS authorization dialog.
/// </summary>
public sealed class TtsEnvironmentSetupService : ITtsEnvironmentSetup
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(20);

    private readonly AppPaths _paths;
    private readonly ITtsEngine _engine;
    private readonly ITtsAudioPlayer _player;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public TtsEnvironmentSetupService(
        AppPaths paths,
        ITtsEngine engine,
        ITtsAudioPlayer player)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        _player = player ?? throw new ArgumentNullException(nameof(player));
    }

    public async Task<TtsSetupResult> EnsureReadyAsync(
        IProgress<TtsSetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Report(progress, "正在检查 TTS 环境…");
            var engineAvailability = await _engine.CheckAvailabilityAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            var needsEngine = !engineAvailability.IsAvailable;
            var needsPlayer = !_player.IsAvailable;

            if (!needsEngine && !needsPlayer)
            {
                Report(progress, "TTS 环境已就绪。", 100);
                return new TtsSetupResult(true, "TTS 环境已就绪。");
            }

            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
            {
                return Failure(
                    "当前平台暂不支持自动准备 edge-tts。",
                    progress);
            }

            var changedSystem = false;
            if (OperatingSystem.IsLinux())
            {
                var packages = await GetMissingLinuxPackagesAsync(
                        needsEngine,
                        needsPlayer,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (packages.Count > 0)
                {
                    await InstallLinuxPackagesAsync(
                            packages,
                            progress,
                            cancellationToken)
                        .ConfigureAwait(false);
                    changedSystem = true;
                }
            }

            if (needsEngine)
            {
                await EnsureEdgeTtsAsync(progress, cancellationToken)
                    .ConfigureAwait(false);
            }

            var finalEngine = await _engine.CheckAvailabilityAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            if (!finalEngine.IsAvailable)
            {
                return Failure(
                    finalEngine.Message,
                    progress,
                    changedSystem);
            }

            if (!_player.IsAvailable)
            {
                return Failure(
                    _player.UnavailableReason
                        ?? "TTS 已安装，但当前平台没有可用的音频输出。",
                    progress,
                    changedSystem);
            }

            Report(progress, "TTS 已自动安装并准备完成。", 100);
            return new TtsSetupResult(
                true,
                "TTS 已自动安装并准备完成。",
                changedSystem);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return Failure(
                $"TTS 自动安装失败：{exception.Message}",
                progress);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<string>> GetMissingLinuxPackagesAsync(
        bool needsEngine,
        bool needsPlayer,
        CancellationToken cancellationToken)
    {
        var packages = new List<string>();
        if (needsPlayer)
            packages.Add("mpv");

        if (needsEngine)
        {
            if (await FindPythonAsync(cancellationToken).ConfigureAwait(false) is null)
                packages.Add("python3");
            if (FindExecutableOnPath("pipx") is null)
                packages.Add("pipx");
        }

        return packages
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private async Task EnsureEdgeTtsAsync(
        IProgress<TtsSetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var python = await FindPythonAsync(cancellationToken).ConfigureAwait(false);
        if (python is null && OperatingSystem.IsWindows())
        {
            await InstallWindowsPythonAsync(progress, cancellationToken)
                .ConfigureAwait(false);
            python = await FindPythonAsync(cancellationToken).ConfigureAwait(false);
        }

        if (python is null)
        {
            throw new InvalidOperationException(
                OperatingSystem.IsLinux()
                    ? "未找到 python3。请允许系统授权对话框安装 python3 后重试。"
                    : "未找到 Python，且无法通过 winget 自动安装。请先安装 Python 3。 ");
        }

        var pipx = FindExecutableOnPath("pipx");
        if (pipx is null)
        {
            var moduleCheck = await RunPythonModuleAsync(
                    python,
                    ["pipx", "--version"],
                    ProbeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (moduleCheck.ExitCode != 0 || moduleCheck.TimedOut)
            {
                if (OperatingSystem.IsLinux())
                {
                    throw new InvalidOperationException(
                        "未找到 pipx。请允许系统授权对话框安装 pipx 后重试。\n"
                        + FormatProcessDetail(moduleCheck, "pipx 检测失败。"));
                }

                Report(progress, "正在安装 pipx…");
                var installPipx = await RunPythonModuleAsync(
                        python,
                        [
                            "pip",
                            "install",
                            "--user",
                            "--upgrade",
                            "pipx",
                        ],
                        CommandTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                RequireSuccess(installPipx, "安装 pipx 失败。");
            }
        }

        pipx = FindExecutableOnPath("pipx");
        var pipxUsesModule = pipx is null;
        if (pipxUsesModule)
        {
            var moduleCheck = await RunPythonModuleAsync(
                    python,
                    ["pipx", "--version"],
                    ProbeTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            RequireSuccess(moduleCheck, "安装 pipx 后仍无法启动 pipx。");
        }

        Directory.CreateDirectory(TtsRuntimePaths.PipxHome(_paths));
        Directory.CreateDirectory(TtsRuntimePaths.PipxBin(_paths));
        Directory.CreateDirectory(TtsRuntimePaths.PipxMan(_paths));
        Directory.CreateDirectory(TtsRuntimePaths.PipxVenvCache(_paths));

        Report(progress, "正在安装 edge-tts 到 Kkindle 专用环境…");
        var installArguments = new List<string>();
        if (pipxUsesModule)
        {
            installArguments.AddRange(["pipx", "install", "edge-tts", "--force"]);
        }
        else
        {
            installArguments.AddRange(["install", "edge-tts", "--force"]);
        }

        // Do not let pipx select its own default interpreter here. Windows
        // Store Python redirects venv paths into the package sandbox, which
        // makes pipx fail immediately after printing only
        // "creating virtual environment...". FindPythonAsync filters that
        // interpreter out and records the real interpreter path for pipx.
        installArguments.AddRange(["--python", PythonInterpreterPath(python)]);

        var install = pipxUsesModule
            ? await RunPythonModuleAsync(
                    python,
                    installArguments,
                    CommandTimeout,
                    cancellationToken,
                    BuildPipxEnvironment(python))
                .ConfigureAwait(false)
            : await RunProcessAsync(
                    pipx!,
                    installArguments,
                    CommandTimeout,
                    cancellationToken,
                    BuildPipxEnvironment(python))
                .ConfigureAwait(false);
        RequireSuccess(install, "安装 edge-tts 失败。");

        var edgeExecutable = TtsRuntimePaths.EdgeTtsCandidates(_paths)
            .FirstOrDefault(File.Exists);
        if (edgeExecutable is null)
        {
            throw new InvalidOperationException(
                "edge-tts 安装命令已完成，但没有找到 Kkindle 专用的 edge-tts 启动文件。 ");
        }
    }

    private async Task InstallWindowsPythonAsync(
        IProgress<TtsSetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var winget = FindExecutableOnPath("winget");
        if (winget is null)
        {
            throw new InvalidOperationException(
                "Windows 未找到 winget，无法自动安装 Python。请安装 App Installer 后重试。\n"
                + "可执行：winget install --id Python.Python.3.12 --scope user");
        }

        Report(progress, "正在通过 Windows 包管理器安装 Python…");
        var result = await RunProcessAsync(
                winget,
                [
                    "install",
                    "--id",
                    "Python.Python.3.12",
                    "--exact",
                    "--scope",
                    "user",
                    "--silent",
                    "--accept-package-agreements",
                    "--accept-source-agreements",
                ],
                CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        RequireSuccess(result, "通过 winget 安装 Python 失败。");

        if (await FindPythonAsync(cancellationToken).ConfigureAwait(false) is null)
        {
            throw new InvalidOperationException(
                "Python 安装程序已结束，但当前系统仍找不到 python.exe。请重启 Kkindle 后重试。 ");
        }
    }

    private async Task InstallLinuxPackagesAsync(
        IReadOnlyList<string> packages,
        IProgress<TtsSetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var apt = FindExecutableOnPath("apt-get")
            ?? (File.Exists("/usr/bin/apt-get") ? "/usr/bin/apt-get" : null);
        if (apt is null)
        {
            throw new InvalidOperationException(
                "未找到 apt-get，无法自动安装 Linux TTS 依赖。 ");
        }

        var pkexec = FindExecutableOnPath("pkexec");
        var sudo = FindExecutableOnPath("sudo");
        var isRoot = string.Equals(
            Environment.UserName,
            "root",
            StringComparison.OrdinalIgnoreCase);

        if (!isRoot && pkexec is null && sudo is null)
        {
            throw new InvalidOperationException(
                "安装 Linux TTS 依赖需要 pkexec 或 sudo，但系统中未找到。 ");
        }

        Report(progress, "正在请求系统授权并更新软件包索引…");
        var update = await RunPrivilegedAptAsync(
                apt,
                pkexec,
                sudo,
                ["update"],
                cancellationToken)
            .ConfigureAwait(false);
        RequireSuccess(update, "更新 Linux 软件包索引失败。");

        Report(
            progress,
            $"正在安装 Linux TTS 依赖：{string.Join(", ", packages)}…");
        var installArguments = new List<string>
        {
            "install",
            "--yes",
            "--no-install-recommends",
        };
        installArguments.AddRange(packages);
        var install = await RunPrivilegedAptAsync(
                apt,
                pkexec,
                sudo,
                installArguments,
                cancellationToken)
            .ConfigureAwait(false);
        RequireSuccess(install, "安装 Linux TTS 依赖失败。");
    }

    private static Task<ProcessResult> RunPrivilegedAptAsync(
        string apt,
        string? pkexec,
        string? sudo,
        IReadOnlyList<string> aptArguments,
        CancellationToken cancellationToken)
    {
        if (string.Equals(
                Environment.UserName,
                "root",
                StringComparison.OrdinalIgnoreCase))
        {
            return RunProcessAsync(
                apt,
                aptArguments,
                CommandTimeout,
                cancellationToken);
        }

        if (pkexec is not null)
        {
            var arguments = new List<string> { apt };
            arguments.AddRange(aptArguments);
            return RunProcessAsync(
                pkexec,
                arguments,
                CommandTimeout,
                cancellationToken);
        }

        var sudoArguments = new List<string> { "--non-interactive", apt };
        sudoArguments.AddRange(aptArguments);
        return RunProcessAsync(
            sudo!,
            sudoArguments,
            CommandTimeout,
            cancellationToken);
    }

    private async Task<PythonCommand?> FindPythonAsync(
        CancellationToken cancellationToken)
    {
        var candidates = new List<PythonCommand>();
        if (OperatingSystem.IsWindows())
        {
            foreach (var name in new[] { "python.exe", "python3.exe", "py.exe" })
            {
                var path = FindExecutableOnPath(name);
                if (path is not null)
                {
                    candidates.Add(new PythonCommand(
                        path,
                        Path.GetFileName(path).Equals(
                            "py.exe",
                            StringComparison.OrdinalIgnoreCase)
                            ? ["-3"]
                            : []));
                }
            }

            foreach (var path in EnumerateWindowsPythonLocations())
                candidates.Add(new PythonCommand(path, []));
        }
        else
        {
            foreach (var name in new[] { "python3", "python" })
            {
                var path = FindExecutableOnPath(name);
                if (path is not null)
                    candidates.Add(new PythonCommand(path, []));
            }
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (!seen.Add(candidate.Executable)) continue;
            try
            {
                var result = await RunPythonAsync(
                        candidate,
                        ["-c", "import sys; print(sys.executable)"],
                        ProbeTimeout,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (result.TimedOut || result.ExitCode != 0)
                    continue;

                var interpreterPath = FirstNonEmptyLine(
                    result.StandardOutput,
                    result.StandardError);
                if (IsMicrosoftStorePythonPath(interpreterPath))
                    continue;

                return candidate with { InterpreterPath = interpreterPath };
            }
            catch (Win32Exception)
            {
            }
            catch (FileNotFoundException)
            {
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateWindowsPythonLocations()
    {
        if (!OperatingSystem.IsWindows()) yield break;

        var roots = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "AppData",
                "Local",
                "Programs",
                "Python"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Python"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Python"),
        };

        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            IEnumerable<string> directories;
            try
            {
                directories = Directory.Exists(root)
                    ? Directory.EnumerateDirectories(root, "Python*", SearchOption.TopDirectoryOnly)
                    : [];
            }
            catch
            {
                continue;
            }

            foreach (var directory in directories.OrderByDescending(path => path))
            {
                var python = Path.Combine(directory, "python.exe");
                if (File.Exists(python)) yield return python;
            }
        }
    }

    private static string PythonInterpreterPath(PythonCommand python)
        => string.IsNullOrWhiteSpace(python.InterpreterPath)
            ? python.Executable
            : python.InterpreterPath;

    internal static bool IsMicrosoftStorePythonPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Replace('/', '\\');
        return normalized.Contains(
                   @"\Microsoft\WindowsApps\",
                   StringComparison.OrdinalIgnoreCase)
            || normalized.Contains(
                   @"\Packages\PythonSoftwareFoundation.",
                   StringComparison.OrdinalIgnoreCase);
    }

    private static string? FirstNonEmptyLine(params string?[] values)
        => values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries))
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0);

    private Dictionary<string, string> BuildPipxEnvironment(PythonCommand python)
    {
        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PIPX_HOME"] = TtsRuntimePaths.PipxHome(_paths),
            ["PIPX_BIN_DIR"] = TtsRuntimePaths.PipxBin(_paths),
            ["PIPX_MAN_DIR"] = TtsRuntimePaths.PipxMan(_paths),
            ["PIPX_VENV_CACHEDIR"] = TtsRuntimePaths.PipxVenvCache(_paths),
            ["PIPX_DEFAULT_PYTHON"] = PythonInterpreterPath(python),
        };
        return environment;
    }

    private static async Task<ProcessResult> RunPythonModuleAsync(
        PythonCommand python,
        IReadOnlyList<string> moduleArguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
        => await RunPythonAsync(
                python,
                ["-m", .. moduleArguments],
                timeout,
                cancellationToken,
                environment)
            .ConfigureAwait(false);

    private static Task<ProcessResult> RunPythonAsync(
        PythonCommand python,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var allArguments = new List<string>(python.PrefixArguments.Count + arguments.Count);
        allArguments.AddRange(python.PrefixArguments);
        allArguments.AddRange(arguments);
        return RunProcessAsync(
            python.Executable,
            allArguments,
            timeout,
            cancellationToken,
            environment);
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);
        if (environment is not null)
        {
            foreach (var item in environment)
                startInfo.Environment[item.Key] = item.Value;
        }

        using var process = new Process { StartInfo = startInfo };
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        var exitTask = process.WaitForExitAsync();
        try
        {
            var timeoutTask = Task.Delay(timeout, cancellationToken);
            var completed = await Task.WhenAny(exitTask, timeoutTask)
                .ConfigureAwait(false);
            if (completed != exitTask)
            {
                KillProcess(process);
                await WaitForExitSafelyAsync(exitTask).ConfigureAwait(false);
                if (cancellationToken.IsCancellationRequested)
                    throw new OperationCanceledException(cancellationToken);

                return new ProcessResult(
                    -1,
                    await outputTask.ConfigureAwait(false),
                    await errorTask.ConfigureAwait(false),
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
            await outputTask.ConfigureAwait(false),
            await errorTask.ConfigureAwait(false),
            false);
    }

    private static string? FindExecutableOnPath(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (Path.IsPathRooted(name))
            return File.Exists(name) ? Path.GetFullPath(name) : null;

        var pathValue = Environment.GetEnvironmentVariable("PATH")
            ?? Environment.GetEnvironmentVariable("Path");
        if (string.IsNullOrWhiteSpace(pathValue)) return null;

        var names = OperatingSystem.IsWindows()
            ? BuildWindowsExecutableNames(name)
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
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<string> BuildWindowsExecutableNames(string name)
    {
        if (Path.HasExtension(name)) return [name];
        return [name + ".exe", name, name + ".cmd", name + ".bat"];
    }

    private static void Report(
        IProgress<TtsSetupProgress>? progress,
        string message,
        double? percentage = null)
        => progress?.Report(new TtsSetupProgress(
            message,
            percentage is null,
            percentage));

    private static TtsSetupResult Failure(
        string? message,
        IProgress<TtsSetupProgress>? progress,
        bool changedSystem = false)
    {
        var detail = string.IsNullOrWhiteSpace(message)
            ? "没有更多错误信息。"
            : message.Trim();
        Report(progress, detail);
        return new TtsSetupResult(false, detail, changedSystem);
    }

    private static void RequireSuccess(ProcessResult result, string fallback)
    {
        if (result.TimedOut)
            throw new InvalidOperationException(fallback + "命令执行超时。 ");
        if (result.ExitCode != 0)
            throw new InvalidOperationException(FormatProcessDetail(result, fallback));
    }

    private static string FormatProcessDetail(ProcessResult result, string fallback)
    {
        var detail = string.Join(
            Environment.NewLine,
            new[] { result.StandardError, result.StandardOutput }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim()));
        if (detail.Length > 3000) detail = detail[^3000..];
        return string.IsNullOrWhiteSpace(detail)
            ? fallback
            : fallback + Environment.NewLine + detail;
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
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _gate.Dispose();
    }

    private sealed record PythonCommand(
        string Executable,
        IReadOnlyList<string> PrefixArguments,
        string? InterpreterPath = null);

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError,
        bool TimedOut);
}
