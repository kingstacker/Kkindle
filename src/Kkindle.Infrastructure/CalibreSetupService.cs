using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;

namespace Kkindle.Infrastructure;

public sealed record CalibreSetupProgress(string Message, long BytesReceived = 0, long? TotalBytes = null)
{
    public double? Percentage => TotalBytes is > 0
        ? Math.Clamp(BytesReceived * 100d / TotalBytes.Value, 0, 100)
        : null;
}

public sealed record CalibreSetupResult(string ExecutablePath, string Message);

/// <summary>
/// User-initiated installer for the external Calibre dependency and KFX Input
/// plugin. Nothing downloaded here is added to Kkindle's own release package.
/// </summary>
public sealed class CalibreSetupService : IDisposable
{
    internal static readonly Uri WindowsDownloadUri = new("https://calibre-ebook.com/dist/win64");
    internal static readonly Uri MacOSDownloadUri = new("https://calibre-ebook.com/dist/osx");
    internal static readonly Uri LinuxInstallerUri = new("https://download.calibre-ebook.com/linux-installer.sh");
    internal static readonly Uri KfxInputPluginUri = new("https://plugins.calibre-ebook.com/291290.zip");

    private const long MaximumCalibreDownloadBytes = 768L * 1024 * 1024;
    private const long MaximumPluginDownloadBytes = 64L * 1024 * 1024;
    private static readonly byte[] MsiHeader = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private readonly HttpClient _httpClient;
    private bool _disposed;

    public CalibreSetupService(HttpMessageHandler? handler = null)
    {
        handler ??= new HttpClientHandler { AllowAutoRedirect = true };
        _httpClient = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Kkindle", "1.0"));
    }

    public string? LocateCalibre(string? userPath = null)
    {
        var operatingSystem = CalibreExecutableLocator.CurrentOperatingSystem();
        return CalibreExecutableLocator.Locate(
            AppContext.BaseDirectory,
            userPath,
            Environment.GetEnvironmentVariable("PATH") ?? Environment.GetEnvironmentVariable("Path"),
            operatingSystem,
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
    }

    public async Task<CalibreSetupResult> InstallCalibreAsync(
        IProgress<CalibreSetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var workDirectory = CreateWorkDirectory();
        try
        {
            if (OperatingSystem.IsWindows())
                return await InstallWindowsAsync(workDirectory, progress, cancellationToken);
            if (OperatingSystem.IsMacOS())
                return await InstallMacOSAsync(workDirectory, progress, cancellationToken);
            if (OperatingSystem.IsLinux())
                return await InstallLinuxAsync(workDirectory, progress, cancellationToken);
            throw new PlatformNotSupportedException("Calibre automatic installation supports Windows, Linux and macOS only.");
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    public async Task<bool> IsKfxInputInstalledAsync(
        string? calibrePath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var ebookConvert = LocateCalibre(calibrePath);
        if (string.IsNullOrWhiteSpace(ebookConvert)) return false;

        var calibreDirectory = Path.GetDirectoryName(ebookConvert);
        if (string.IsNullOrWhiteSpace(calibreDirectory)) return false;
        var customizer = Path.Combine(
            calibreDirectory,
            CalibreExecutableLocator.ToolName("calibre-customize", CalibreExecutableLocator.CurrentOperatingSystem()));
        if (!File.Exists(customizer)) return false;

        var listed = await RunCommandAsync(
            customizer,
            ["--list-plugins"],
            cancellationToken,
            calibreConfigurationDirectory: Environment.GetEnvironmentVariable("KKINDLE_CALIBRE_CONFIG_DIRECTORY"));
        return IsKfxInputListed(listed.ExitCode, listed.Output, listed.Error);
    }

    public async Task<string> InstallKfxInputAsync(
        string? calibrePath,
        IProgress<CalibreSetupProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var ebookConvert = LocateCalibre(calibrePath)
            ?? throw new InvalidOperationException("未找到 Calibre。请先安装 Calibre，或指定 ebook-convert 路径。");
        var calibreDirectory = Path.GetDirectoryName(ebookConvert)
            ?? throw new InvalidOperationException("Calibre 路径无效。");
        var customizer = Path.Combine(
            calibreDirectory,
            CalibreExecutableLocator.ToolName("calibre-customize", CalibreExecutableLocator.CurrentOperatingSystem()));
        if (!File.Exists(customizer))
            throw new InvalidOperationException($"Calibre 安装中缺少 {Path.GetFileName(customizer)}。");

        var workDirectory = CreateWorkDirectory();
        try
        {
            var pluginPath = Path.Combine(workDirectory, "KFX Input.zip");
            progress?.Report(new CalibreSetupProgress("正在从 Calibre 官方插件索引下载 KFX Input…"));
            await DownloadAsync(KfxInputPluginUri, pluginPath, MaximumPluginDownloadBytes, progress, cancellationToken);
            ValidateKfxPluginPackage(pluginPath);

            progress?.Report(new CalibreSetupProgress("正在安装 KFX Input…"));
            var install = await RunCommandAsync(
                customizer,
                ["--add-plugin", pluginPath],
                cancellationToken,
                calibreConfigurationDirectory: Environment.GetEnvironmentVariable("KKINDLE_CALIBRE_CONFIG_DIRECTORY"));
            if (install.ExitCode != 0)
                throw new InvalidOperationException($"KFX Input 安装失败：{install.Detail}");

            var listed = await RunCommandAsync(
                customizer,
                ["--list-plugins"],
                cancellationToken,
                calibreConfigurationDirectory: Environment.GetEnvironmentVariable("KKINDLE_CALIBRE_CONFIG_DIRECTORY"));
            if (!IsKfxInputListed(listed.ExitCode, listed.Output, listed.Error))
                throw new InvalidOperationException("KFX Input 安装完成，但 Calibre 未能识别该插件。");

            progress?.Report(new CalibreSetupProgress("KFX Input 已安装。"));
            return ebookConvert;
        }
        finally
        {
            TryDeleteDirectory(workDirectory);
        }
    }

    private async Task<CalibreSetupResult> InstallWindowsAsync(
        string workDirectory,
        IProgress<CalibreSetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var installer = Path.Combine(workDirectory, "calibre-installer.msi");
        progress?.Report(new CalibreSetupProgress("正在下载 Calibre Windows 安装程序…"));
        await DownloadAsync(WindowsDownloadUri, installer, MaximumCalibreDownloadBytes, progress, cancellationToken);
        await ValidateMsiAsync(installer, cancellationToken);
        await VerifyWindowsSignatureAsync(installer, cancellationToken);

        progress?.Report(new CalibreSetupProgress("正在启动 Calibre 安装程序，请确认系统安装提示…"));
        var startInfo = new ProcessStartInfo
        {
            FileName = "msiexec.exe",
            UseShellExecute = true,
            Verb = "runas"
        };
        startInfo.ArgumentList.Add("/i");
        startInfo.ArgumentList.Add(installer);
        startInfo.ArgumentList.Add("/passive");
        startInfo.ArgumentList.Add("/norestart");
        var result = await RunShellProcessAsync(startInfo, cancellationToken);
        if (result is not (0 or 3010))
            throw new InvalidOperationException($"Calibre 安装程序退出，代码 {result}。");

        var executable = LocateCalibre()
            ?? throw new InvalidOperationException("Calibre 安装已结束，但未找到 ebook-convert.exe。请完成安装后点击浏览指定路径。");
        return new CalibreSetupResult(executable, result == 3010 ? "Calibre 已安装，系统建议稍后重启。" : "Calibre 已安装。");
    }

    private async Task<CalibreSetupResult> InstallLinuxAsync(
        string workDirectory,
        IProgress<CalibreSetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var installer = Path.Combine(workDirectory, "linux-installer.sh");
        progress?.Report(new CalibreSetupProgress("正在下载 Calibre 官方 Linux 安装器…"));
        await DownloadAsync(LinuxInstallerUri, installer, 4 * 1024 * 1024, progress, cancellationToken);
        await ValidateShellScriptAsync(installer, cancellationToken);

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrWhiteSpace(userProfile)) throw new InvalidOperationException("无法确定当前 Linux 用户目录。");
        var installDirectory = Path.Combine(userProfile, "calibre-bin");
        progress?.Report(new CalibreSetupProgress("正在安装 Calibre 到 ~/calibre-bin…"));
        var result = await RunCommandAsync(
            "/bin/sh",
            [installer, $"install_dir={installDirectory}", "isolated=y"],
            cancellationToken);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Calibre Linux 安装失败：{result.Detail}");

        var executable = Path.Combine(installDirectory, "ebook-convert");
        if (!File.Exists(executable))
            throw new InvalidOperationException("Calibre 安装已结束，但 ~/calibre-bin/ebook-convert 不存在。");
        return new CalibreSetupResult(executable, "Calibre 已安装到 ~/calibre-bin。");
    }

    private async Task<CalibreSetupResult> InstallMacOSAsync(
        string workDirectory,
        IProgress<CalibreSetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var image = Path.Combine(workDirectory, "calibre.dmg");
        progress?.Report(new CalibreSetupProgress("正在下载 Calibre macOS 磁盘映像…"));
        await DownloadAsync(MacOSDownloadUri, image, MaximumCalibreDownloadBytes, progress, cancellationToken);
        await ValidateDmgAsync(image, cancellationToken);

        var verify = await RunCommandAsync("/usr/bin/hdiutil", ["verify", image], cancellationToken);
        if (verify.ExitCode != 0) throw new InvalidOperationException($"Calibre DMG 校验失败：{verify.Detail}");

        var mountDirectory = Path.Combine(workDirectory, "mount");
        Directory.CreateDirectory(mountDirectory);
        var mounted = false;
        try
        {
            progress?.Report(new CalibreSetupProgress("正在挂载并安装 Calibre…"));
            var attach = await RunCommandAsync(
                "/usr/bin/hdiutil",
                ["attach", image, "-nobrowse", "-readonly", "-mountpoint", mountDirectory],
                cancellationToken);
            if (attach.ExitCode != 0) throw new InvalidOperationException($"无法挂载 Calibre DMG：{attach.Detail}");
            mounted = true;

            var sourceApp = Directory.EnumerateDirectories(mountDirectory, "*.app", SearchOption.TopDirectoryOnly)
                .FirstOrDefault(path => Path.GetFileName(path).Equals("calibre.app", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("Calibre DMG 中未找到 calibre.app。");
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var applicationsDirectory = Path.Combine(userProfile, "Applications");
            Directory.CreateDirectory(applicationsDirectory);
            var destinationApp = Path.Combine(applicationsDirectory, "calibre.app");
            var stagedApp = Path.Combine(workDirectory, "staged-calibre.app");
            var copy = await RunCommandAsync("/usr/bin/ditto", [sourceApp, stagedApp], cancellationToken);
            if (copy.ExitCode != 0) throw new InvalidOperationException($"无法复制 calibre.app：{copy.Detail}");
            var signature = await RunCommandAsync(
                "/usr/bin/codesign",
                ["--verify", "--deep", "--strict", stagedApp],
                cancellationToken);
            if (signature.ExitCode != 0) throw new InvalidOperationException($"Calibre 应用签名校验失败：{signature.Detail}");

            await ReplaceMacApplicationAsync(stagedApp, destinationApp, cancellationToken);

            var executable = Path.Combine(destinationApp, "Contents", "MacOS", "ebook-convert");
            if (!File.Exists(executable)) throw new InvalidOperationException("安装后的 calibre.app 缺少 ebook-convert。");
            return new CalibreSetupResult(executable, "Calibre 已安装到 ~/Applications。");
        }
        finally
        {
            if (mounted)
                await RunCommandAsync("/usr/bin/hdiutil", ["detach", mountDirectory, "-force"], CancellationToken.None);
        }
    }

    private static async Task ReplaceMacApplicationAsync(
        string stagedApp,
        string destinationApp,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(destinationApp) && new DirectoryInfo(destinationApp).LinkTarget is not null)
            throw new InvalidOperationException("~/Applications/calibre.app 是符号链接，已拒绝自动覆盖。");

        var backupApp = destinationApp + ".kkindle-backup-" + Guid.NewGuid().ToString("N");
        var hasBackup = false;
        try
        {
            if (Directory.Exists(destinationApp))
            {
                Directory.Move(destinationApp, backupApp);
                hasBackup = true;
            }

            var install = await RunCommandAsync("/usr/bin/ditto", [stagedApp, destinationApp], cancellationToken);
            if (install.ExitCode != 0) throw new InvalidOperationException($"无法安装 calibre.app：{install.Detail}");
            if (hasBackup)
            {
                TryDeleteDirectory(backupApp);
                hasBackup = false;
            }
        }
        catch
        {
            TryDeleteDirectory(destinationApp);
            if (hasBackup && Directory.Exists(backupApp)) Directory.Move(backupApp, destinationApp);
            throw;
        }
    }

    private async Task DownloadAsync(
        Uri uri,
        string destination,
        long maximumBytes,
        IProgress<CalibreSetupProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        EnsureTrustedDownloadUri(response.RequestMessage?.RequestUri ?? uri);
        var length = response.Content.Headers.ContentLength;
        if (length is > 0 && length > maximumBytes)
            throw new InvalidDataException("下载文件超过允许的大小。");

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destinationStream = new FileStream(
            destination,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            81920,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[81920];
        long received = 0;
        while (true)
        {
            var count = await source.ReadAsync(buffer, cancellationToken);
            if (count == 0) break;
            received += count;
            if (received > maximumBytes) throw new InvalidDataException("下载文件超过允许的大小。");
            await destinationStream.WriteAsync(buffer.AsMemory(0, count), cancellationToken);
            progress?.Report(new CalibreSetupProgress("正在下载…", received, length));
        }
        await destinationStream.FlushAsync(cancellationToken);
    }

    internal static void ValidateKfxPluginPackage(string path)
    {
        using var archive = ZipFile.OpenRead(path);
        if (archive.Entries.Count is 0 or > 2000)
            throw new InvalidDataException("KFX Input 插件包结构无效。");
        var importMarker = archive.Entries.FirstOrDefault(entry =>
            entry.FullName.EndsWith("plugin-import-name-kfx_input.txt", StringComparison.OrdinalIgnoreCase));
        var pluginDirectory = importMarker is null
            ? string.Empty
            : importMarker.FullName[..Math.Max(0, importMarker.FullName.LastIndexOf('/') + 1)];
        var initializerPath = $"{pluginDirectory}__init__.py";
        var initializer = archive.Entries.FirstOrDefault(entry =>
            entry.FullName.Equals(initializerPath, StringComparison.OrdinalIgnoreCase));
        if (importMarker is null || initializer is null || initializer.Length > 2 * 1024 * 1024)
            throw new InvalidDataException("下载内容不是有效的 KFX Input 插件包。");
        using var reader = new StreamReader(initializer.Open(), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var content = reader.ReadToEnd();
        if (!content.Contains("KFX Input", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("下载的插件包未声明 KFX Input。");
    }

    internal static bool IsKfxInputListed(int exitCode, string? output, string? error)
    {
        if (exitCode != 0) return false;
        return string.Concat(output, Environment.NewLine, error)
            .Contains("KFX Input", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task ValidateMsiAsync(string path, CancellationToken cancellationToken)
    {
        var header = new byte[MsiHeader.Length];
        await using var stream = File.OpenRead(path);
        if (await stream.ReadAsync(header, cancellationToken) != header.Length || !header.SequenceEqual(MsiHeader))
            throw new InvalidDataException("下载内容不是有效的 Windows MSI 安装程序。");
    }

    private static async Task ValidateDmgAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        if (stream.Length < 512) throw new InvalidDataException("下载的 DMG 文件不完整。");
        stream.Seek(-512, SeekOrigin.End);
        var signature = new byte[4];
        if (await stream.ReadAsync(signature, cancellationToken) != signature.Length
            || Encoding.ASCII.GetString(signature) != "koly")
            throw new InvalidDataException("下载内容不是有效的 macOS DMG。");
    }

    private static async Task ValidateShellScriptAsync(string path, CancellationToken cancellationToken)
    {
        var prefix = new byte[2];
        await using var stream = File.OpenRead(path);
        if (await stream.ReadAsync(prefix, cancellationToken) != prefix.Length || prefix[0] != '#' || prefix[1] != '!')
            throw new InvalidDataException("下载内容不是有效的 Linux 安装脚本。");
    }

    private static async Task VerifyWindowsSignatureAsync(string path, CancellationToken cancellationToken)
    {
        var result = await RunCommandAsync(
            "powershell.exe",
            BuildWindowsSignatureVerificationArguments(path),
            cancellationToken);
        if (result.ExitCode != 0) throw new InvalidDataException($"Calibre MSI 数字签名无效：{result.Detail}");
    }

    internal static IReadOnlyList<string> BuildWindowsSignatureVerificationArguments(string path)
    {
        // With -Command, arguments appended after the command are parsed as
        // part of the command text by Windows PowerShell. That loses paths
        // containing spaces and leaves the old $args[0] expression empty.
        // Encode the complete script so the MSI path is passed as data.
        var escapedPath = path.Replace("'", "''");
        var securityModule = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "Modules",
            "Microsoft.PowerShell.Security",
            "Microsoft.PowerShell.Security.psd1");
        var escapedModule = securityModule.Replace("'", "''");
        var command = $"Import-Module -Name '{escapedModule}' -ErrorAction Stop; $s=Get-AuthenticodeSignature -LiteralPath '{escapedPath}'; if($s.Status -ne 'Valid'){{Write-Error $s.Status; exit 2}}";
        var encodedCommand = Convert.ToBase64String(Encoding.Unicode.GetBytes(command));
        return ["-NoProfile", "-NonInteractive", "-EncodedCommand", encodedCommand];
    }

    private static void EnsureTrustedDownloadUri(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps) throw new InvalidDataException("下载地址不是 HTTPS。");
        var host = uri.IdnHost;
        var trusted = host.Equals("calibre-ebook.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".calibre-ebook.com", StringComparison.OrdinalIgnoreCase)
            || host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
        if (!trusted) throw new InvalidDataException($"下载被重定向到未受信任的主机：{host}");
    }

    private static async Task<CommandResult> RunCommandAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        string? calibreConfigurationDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(calibreConfigurationDirectory))
            startInfo.Environment["CALIBRE_CONFIG_DIRECTORY"] = Path.GetFullPath(calibreConfigurationDirectory);
        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"无法启动 {executable}。");
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch
            {
                TryKill(process);
                throw;
            }
            return new CommandResult(process.ExitCode, await outputTask, await errorTask);
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException($"系统无法启动 {executable}。", exception);
        }
    }

    private static async Task<int> RunShellProcessAsync(ProcessStartInfo startInfo, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"无法启动 {startInfo.FileName}。");
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch
            {
                TryKill(process);
                throw;
            }
            return process.ExitCode;
        }
        catch (Win32Exception exception)
        {
            throw new InvalidOperationException("Calibre 安装被取消或无法启动。", exception);
        }
    }

    private static string CreateWorkDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "Kkindle", "setup", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
    }

    private sealed record CommandResult(int ExitCode, string Output, string Error)
    {
        public string Detail
        {
            get
            {
                var value = string.IsNullOrWhiteSpace(Error) ? Output : Error;
                value = value.Trim();
                return value.Length == 0 ? $"退出码 {ExitCode}" : value.Length <= 1000 ? value : value[^1000..];
            }
        }
    }
}
