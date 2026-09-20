using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    private const string McpServerName = "kkindle";
    private const string McpStdioTransport = "stdio";
    private const string McpHttpTransport = "http";
    private const int DefaultMcpHttpPort = 8765;

    private bool _mcpSettingsInitialized;
    private bool _suppressMcpSettingsChanges;
    private long _mcpSettingsEditVersion;
    private long _mcpSettingsSavedVersion;
    private CancellationTokenSource? _mcpSettingsAutoSaveCancellation;
    private Task? _mcpSettingsSaveTask;
    private string _mcpExecutablePath = string.Empty;

    private void InitializeMcpSettings()
    {
        if (_mcpSettingsInitialized)
        {
            if (!McpSettingsHaveUnsavedChanges())
                PopulateMcpSettingsControls();
            RefreshMcpExecutableStatus();
            return;
        }

        PopulateMcpSettingsControls();
        McpEnabledCheck.IsCheckedChanged += (_, _) => McpSettingsControlChanged();
        McpTransportBox.SelectionChanged += McpTransportBox_SelectionChanged;
        McpHttpPortBox.ValueChanged += (_, _) => McpSettingsControlChanged();
        McpAccessTokenBox.PropertyChanged += (_, e) =>
        {
            if (e.Property == TextBox.TextProperty) McpSettingsControlChanged();
        };
        McpBookLibraryEnabledCheck.IsCheckedChanged += (_, _) => McpSettingsControlChanged();
        McpSearchBooksEnabledCheck.IsCheckedChanged += (_, _) => McpSettingsControlChanged();
        McpBookMetadataEnabledCheck.IsCheckedChanged += (_, _) => McpSettingsControlChanged();
        McpReadingProgressEnabledCheck.IsCheckedChanged += (_, _) => McpSettingsControlChanged();
        McpRecentBooksEnabledCheck.IsCheckedChanged += (_, _) => McpSettingsControlChanged();
        McpTagsEnabledCheck.IsCheckedChanged += (_, _) => McpSettingsControlChanged();
        McpCollectionsEnabledCheck.IsCheckedChanged += (_, _) => McpSettingsControlChanged();
        McpBookFileEnabledCheck.IsCheckedChanged += (_, _) => McpSettingsControlChanged();
        McpDeviceListEnabledCheck.IsCheckedChanged += (_, _) => McpSettingsControlChanged();
        McpDeviceStatusEnabledCheck.IsCheckedChanged += (_, _) => McpSettingsControlChanged();
        McpDeviceLibraryEnabledCheck.IsCheckedChanged += (_, _) => McpSettingsControlChanged();
        McpEjectDeviceEnabledCheck.IsCheckedChanged += (_, _) => McpSettingsControlChanged();
        McpSendToKindleEnabledCheck.IsCheckedChanged += (_, _) => McpSettingsControlChanged();
        McpConvertBookEnabledCheck.IsCheckedChanged += (_, _) => McpSettingsControlChanged();
        McpImportBookEnabledCheck.IsCheckedChanged += (_, _) => McpSettingsControlChanged();
        _mcpSettingsInitialized = true;

        if (McpSettingsHaveUnsavedChanges())
            ScheduleMcpSettingsAutoSave();
        RefreshMcpExecutableStatus();
    }

    private void PopulateMcpSettingsControls()
    {
        var settings = McpServerSettings.Normalize(_appSettings.McpServer);
        _suppressMcpSettingsChanges = true;
        try
        {
            McpEnabledCheck.IsChecked = settings.Enabled;
            McpTransportBox.SelectedIndex = settings.Transport.Equals(McpHttpTransport, StringComparison.OrdinalIgnoreCase)
                ? 1
                : 0;
            McpHttpPortBox.Value = settings.HttpPort;
            McpAccessTokenBox.Text = settings.AccessToken;
            McpBookLibraryEnabledCheck.IsChecked = settings.BookLibraryEnabled;
            McpSearchBooksEnabledCheck.IsChecked = settings.SearchBooksEnabled;
            McpBookMetadataEnabledCheck.IsChecked = settings.BookMetadataEnabled;
            McpReadingProgressEnabledCheck.IsChecked = settings.ReadingProgressEnabled;
            McpRecentBooksEnabledCheck.IsChecked = settings.RecentBooksEnabled;
            McpTagsEnabledCheck.IsChecked = settings.TagsEnabled;
            McpCollectionsEnabledCheck.IsChecked = settings.CollectionsEnabled;
            McpBookFileEnabledCheck.IsChecked = settings.BookFileEnabled;
            McpDeviceListEnabledCheck.IsChecked = settings.DeviceListEnabled;
            McpDeviceStatusEnabledCheck.IsChecked = settings.DeviceStatusEnabled;
            McpDeviceLibraryEnabledCheck.IsChecked = settings.DeviceLibraryEnabled;
            McpEjectDeviceEnabledCheck.IsChecked = settings.EjectDeviceEnabled;
            McpSendToKindleEnabledCheck.IsChecked = settings.SendToKindleEnabled;
            McpConvertBookEnabledCheck.IsChecked = settings.ConvertBookEnabled;
            McpImportBookEnabledCheck.IsChecked = settings.ImportBookEnabled;
            McpSettingsStatusText.Text = string.Empty;
            UpdateMcpTransportControls();
        }
        finally
        {
            _suppressMcpSettingsChanges = false;
        }
    }

    private void McpTransportBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressMcpSettingsChanges) return;
        UpdateMcpTransportControls();
        McpSettingsControlChanged();
    }

    private void UpdateMcpTransportControls()
    {
        var http = IsMcpHttpTransportSelected();
        McpHttpSettingsPanel.IsVisible = http;
        if (http && string.IsNullOrWhiteSpace(McpAccessTokenBox.Text))
            McpAccessTokenBox.Text = GenerateMcpAccessToken();
    }

    private void GenerateMcpAccessTokenButton_Click(object? sender, RoutedEventArgs e)
    {
        McpAccessTokenBox.Text = GenerateMcpAccessToken();
        McpSettingsStatusText.Text = T("访问令牌已生成。");
        McpAccessTokenBox.Focus();
        McpAccessTokenBox.SelectAll();
    }

    private static string GenerateMcpAccessToken()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private bool IsMcpHttpTransportSelected() =>
        string.Equals(
            (McpTransportBox.SelectedItem as ComboBoxItem)?.Tag?.ToString(),
            McpHttpTransport,
            StringComparison.OrdinalIgnoreCase);

    private void McpSettingsControlChanged()
    {
        if (_suppressMcpSettingsChanges || !_mcpSettingsInitialized) return;
        ScheduleMcpSettingsAutoSave();
    }

    private bool McpSettingsHaveUnsavedChanges() =>
        _mcpSettingsInitialized
        && ReadMcpSettingsFromControls() != McpServerSettings.Normalize(_appSettings.McpServer);

    private McpServerSettings ReadMcpSettingsFromControls() => McpServerSettings.Normalize(new McpServerSettings
    {
        Enabled = McpEnabledCheck.IsChecked != false,
        Transport = IsMcpHttpTransportSelected() ? McpHttpTransport : McpStdioTransport,
        HttpPort = McpHttpPortBox.Value is { } port ? decimal.ToInt32(port) : DefaultMcpHttpPort,
        AccessToken = McpAccessTokenBox.Text ?? string.Empty,
        BookLibraryEnabled = McpBookLibraryEnabledCheck.IsChecked != false,
        SearchBooksEnabled = McpSearchBooksEnabledCheck.IsChecked != false,
        BookMetadataEnabled = McpBookMetadataEnabledCheck.IsChecked != false,
        ReadingProgressEnabled = McpReadingProgressEnabledCheck.IsChecked != false,
        RecentBooksEnabled = McpRecentBooksEnabledCheck.IsChecked != false,
        TagsEnabled = McpTagsEnabledCheck.IsChecked != false,
        CollectionsEnabled = McpCollectionsEnabledCheck.IsChecked != false,
        BookFileEnabled = McpBookFileEnabledCheck.IsChecked != false,
        DeviceListEnabled = McpDeviceListEnabledCheck.IsChecked != false,
        DeviceStatusEnabled = McpDeviceStatusEnabledCheck.IsChecked != false,
        DeviceLibraryEnabled = McpDeviceLibraryEnabledCheck.IsChecked != false,
        EjectDeviceEnabled = McpEjectDeviceEnabledCheck.IsChecked != false,
        SendToKindleEnabled = McpSendToKindleEnabledCheck.IsChecked != false,
        ConvertBookEnabled = McpConvertBookEnabledCheck.IsChecked != false,
        ImportBookEnabled = McpImportBookEnabledCheck.IsChecked != false
    });

    private void ScheduleMcpSettingsAutoSave()
    {
        if (_suppressMcpSettingsChanges || !_mcpSettingsInitialized) return;
        _mcpSettingsEditVersion++;
        CancelMcpSettingsDebounce();
        var cancellation = new CancellationTokenSource();
        _mcpSettingsAutoSaveCancellation = cancellation;
        _mcpSettingsSaveTask = DebounceMcpSettingsAsync(cancellation);
    }

    private void CancelMcpSettingsDebounce()
    {
        var pending = _mcpSettingsAutoSaveCancellation;
        _mcpSettingsAutoSaveCancellation = null;
        pending?.Cancel();
    }

    private async Task DebounceMcpSettingsAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(600, cancellation.Token);
            if (!cancellation.IsCancellationRequested)
                await SaveMcpSettingsCoreAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_mcpSettingsAutoSaveCancellation, cancellation))
                _mcpSettingsAutoSaveCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task<bool> FlushMcpSettingsAsync()
    {
        if (!_mcpSettingsInitialized) return true;

        do
        {
            CancelMcpSettingsDebounce();
            if (_mcpSettingsSaveTask is { } pending)
                await pending;
            if (!McpSettingsHaveUnsavedChanges()
                && _mcpSettingsSavedVersion == _mcpSettingsEditVersion)
                return true;
            if (!await SaveMcpSettingsCoreAsync()) return false;
        }
        while (_mcpSettingsSavedVersion < _mcpSettingsEditVersion);

        return true;
    }

    private async Task<bool> SaveMcpSettingsCoreAsync()
    {
        await _appSettingsSaveGate.WaitAsync();
        try
        {
            if (_mcpSettingsSavedVersion == _mcpSettingsEditVersion
                && !McpSettingsHaveUnsavedChanges())
                return true;

            var version = _mcpSettingsEditVersion;
            var settings = AppSettings.Normalize(_appSettings with
            {
                McpServer = ReadMcpSettingsFromControls()
            });
            _appSettings = settings;
            await _appSettingsStore.SaveAsync(settings, _lifetimeCancellation.Token);
            _mcpSettingsSavedVersion = version;
            HandleLocalDataChanged(LocalDataChangeKind.Settings);
            McpSettingsStatusText.Text = T("MCP 设置已保存。");
            return true;
        }
        catch (Exception exception)
        {
            McpSettingsStatusText.Text = T("保存 MCP 设置失败：{0}", UiText.Localize(exception.Message));
            return false;
        }
        finally
        {
            _appSettingsSaveGate.Release();
        }
    }

    private void RefreshMcpExecutableStatus()
    {
        _mcpExecutablePath = ResolveMcpExecutablePath();
        var exists = File.Exists(_mcpExecutablePath);
        McpExecutablePathText.Text = _mcpExecutablePath;
        McpExecutableStatusText.Text = exists
            ? T("MCP server 已就绪")
            : T("未构建 MCP server");
        var brush = AppAppearanceResources.GetBrush(exists ? "SuccessBrush" : "DangerBrush");
        McpExecutableStatusDot.Fill = brush;
        McpExecutableStatusText.Foreground = brush;
    }

    private static string ResolveMcpExecutablePath()
    {
        var applicationDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        var executableNames = OperatingSystem.IsWindows()
            ? new[] { "Kkindle.McpServer.exe", "Kkindle.McpServer" }
            : new[] { "Kkindle.McpServer", "Kkindle.McpServer.exe" };
        var candidates = new List<string>();

        foreach (var name in executableNames)
        {
            AddCandidate(Path.Combine(applicationDirectory, name));
            AddCandidate(Path.Combine(applicationDirectory, "mcp", name));
            AddCandidate(Path.Combine(applicationDirectory, "McpServer", name));
        }

        if (FindRepositoryRoot(applicationDirectory) is { } repositoryRoot)
        {
            foreach (var name in executableNames)
            {
                AddCandidate(Path.Combine(repositoryRoot, "artifacts", "Kkindle-debug-win-x64-latest", name));
                AddCandidate(Path.Combine(repositoryRoot, "src", "Kkindle.McpServer", "bin", "Debug", "net8.0", name));
                AddCandidate(Path.Combine(repositoryRoot, "src", "Kkindle.McpServer", "bin", "Debug", "net8.0", "win-x64", name));
                AddCandidate(Path.Combine(repositoryRoot, "src", "Kkindle.McpServer", "bin", "Debug", "net8.0", "win-x64", "publish", name));
                AddCandidate(Path.Combine(repositoryRoot, "src", "Kkindle.McpServer", "bin", "Release", "net8.0", name));
                AddCandidate(Path.Combine(repositoryRoot, "src", "Kkindle.McpServer", "bin", "Release", "net8.0", "win-x64", "publish", name));
            }
        }

        return candidates.FirstOrDefault(File.Exists)
            ?? candidates.FirstOrDefault()
            ?? Path.Combine(applicationDirectory, executableNames[0]);

        void AddCandidate(string path)
        {
            var fullPath = Path.GetFullPath(path);
            if (!candidates.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                candidates.Add(fullPath);
        }
    }

    private static string? FindRepositoryRoot(string startDirectory)
    {
        var directory = new DirectoryInfo(startDirectory);
        for (var depth = 0; directory is not null && depth < 10; depth++, directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Kkindle.sln")))
                return directory.FullName;
        }

        return null;
    }

    private async void CopyHermesMcpConfigurationButton_Click(object? sender, RoutedEventArgs e)
    {
        RefreshMcpExecutableStatus();
        await CopyMcpConfigurationAsync(BuildHermesMcpConfiguration());
    }

    private async void CopyCodexMcpConfigurationButton_Click(object? sender, RoutedEventArgs e)
    {
        RefreshMcpExecutableStatus();
        await CopyMcpConfigurationAsync(BuildCodexMcpConfiguration());
    }

    private async void CopyAiPromptButton_Click(object? sender, RoutedEventArgs e)
    {
        RefreshMcpExecutableStatus();
        await CopyMcpConfigurationAsync(
            BuildAiPrompt(),
            T("MCP 接入说明已复制到剪贴板。"));
    }

    private async Task CopyMcpConfigurationAsync(string configuration, string? successMessage = null)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            {
                McpSettingsStatusText.Text = T("剪贴板不可用。");
                return;
            }

            if (!await FlushMcpSettingsAsync()) return;
            await clipboard.SetTextAsync(configuration);
            McpSettingsStatusText.Text = successMessage ?? T("MCP 配置已复制到剪贴板。");
        }
        catch (Exception exception)
        {
            McpSettingsStatusText.Text = T("复制 MCP 配置失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private string BuildAiPrompt()
    {
        var settings = ReadMcpSettingsFromControls();
        var lines = new List<string>
        {
            T("请为我接入 kkindle 的 MCP 服务。"),
            string.Empty,
            T("通用 YAML 配置："),
            "```yaml",
            BuildGenericMcpConfiguration(settings),
            "```",
            string.Empty,
            T("当前启用的功能：")
        };

        var features = GetEnabledMcpFeatures(settings);
        if (features.Count == 0)
            lines.Add($"- {T("当前没有启用任何功能。")}");
        else
            lines.AddRange(features.Select(feature => $"- {feature.ToolName}：{feature.Description}"));

        lines.Add(T("被禁用的功能不会出现在上面的清单中。"));
        if (settings.Transport == McpHttpTransport)
        {
            lines.Add(string.Empty);
            lines.Add(T("HTTP 模式通过 token 鉴权；token 由你在 kkindle 设置中提供，接入说明不会包含 token 明文。"));
        }

        lines.Add(string.Empty);
        lines.Add(T("接入后你可以让我查询我的 Kindle 书库、搜索书籍、查看连接到电脑的 Kindle 设备。例如: 我的 Kindle 书库里有什么书?"));
        return string.Join(Environment.NewLine, lines);
    }

    private IReadOnlyList<(string ToolName, string Description)> GetEnabledMcpFeatures(McpServerSettings settings)
    {
        var features = new List<(string ToolName, string Description)>();
        if (settings.BookLibraryEnabled)
            features.Add(("list_library", T("列出书库")));
        if (settings.SearchBooksEnabled)
            features.Add(("search_books", T("搜索书籍")));
        if (settings.BookMetadataEnabled)
            features.Add(("get_book_metadata", T("书籍详情")));
        if (settings.ReadingProgressEnabled)
            features.Add(("get_reading_progress", T("阅读进度与位置")));
        if (settings.RecentBooksEnabled)
            features.Add(("list_recent", T("最近阅读与统计")));
        if (settings.TagsEnabled)
            features.Add(("list_tags", T("书库标签")));
        if (settings.CollectionsEnabled)
            features.Add(("list_collections", T("书库合集")));
        if (settings.BookFileEnabled)
            features.Add(("get_book_file", T("书籍文件路径")));
        if (settings.DeviceListEnabled)
            features.Add(("list_devices", T("列出设备")));
        if (settings.DeviceStatusEnabled)
            features.Add(("device_status", T("设备状态")));
        if (settings.DeviceLibraryEnabled)
            features.Add(("list_device_library", T("设备书库")));
        if (settings.EjectDeviceEnabled)
            features.Add(("eject_device", T("安全弹出设备")));
        if (settings.SendToKindleEnabled)
            features.Add(("send_to_kindle", T("发送到 Kindle")));
        if (settings.ConvertBookEnabled)
            features.Add(("convert_book", T("格式转换")));
        if (settings.ImportBookEnabled)
            features.Add(("import_book", T("导入书库")));
        return features;
    }

    private string BuildGenericMcpConfiguration(McpServerSettings settings)
    {
        var enabled = settings.Enabled ? "true" : "false";
        if (settings.Transport == McpHttpTransport)
        {
            return $"mcp_servers:\n  {McpServerName}:\n    enabled: {enabled}\n    url: 'http://127.0.0.1:{settings.HttpPort}/mcp'\n    headers:\n      Authorization: 'Bearer <TOKEN_FROM_KKINDLE_SETTINGS>'";
        }

        return $"mcp_servers:\n  {McpServerName}:\n    enabled: {enabled}\n    command: '{QuoteYamlSingle(_mcpExecutablePath)}'\n    args: ['--root', '{QuoteYamlSingle(Path.GetFullPath(_paths.Root))}']";
    }

    private string BuildHermesMcpConfiguration()
    {
        EnsureHttpAccessToken();
        var settings = ReadMcpSettingsFromControls();
        var enabled = settings.Enabled ? "true" : "false";
        if (settings.Transport == McpHttpTransport)
        {
            return $"mcp_servers:\n  {McpServerName}:\n    enabled: {enabled}\n    url: 'http://127.0.0.1:{settings.HttpPort}/mcp'\n    headers:\n      Authorization: 'Bearer {QuoteYamlSingle(settings.AccessToken)}'";
        }

        return $"mcp_servers:\n  {McpServerName}:\n    enabled: {enabled}\n    command: '{QuoteYamlSingle(_mcpExecutablePath)}'\n    args: ['--root', '{QuoteYamlSingle(Path.GetFullPath(_paths.Root))}']";
    }

    private string BuildCodexMcpConfiguration()
    {
        EnsureHttpAccessToken();
        var settings = ReadMcpSettingsFromControls();
        var enabled = settings.Enabled ? "true" : "false";
        if (settings.Transport == McpHttpTransport)
        {
            return $"[mcp_servers.{McpServerName}]\nenabled = {enabled}\nurl = \"http://127.0.0.1:{settings.HttpPort}/mcp\"\nhttp_headers = {{ \"Authorization\" = \"Bearer {QuoteTomlBasic(settings.AccessToken)}\" }}";
        }

        return $"[mcp_servers.{McpServerName}]\nenabled = {enabled}\ncommand = \"{QuoteTomlBasic(_mcpExecutablePath)}\"\nargs = [\"--root\", \"{QuoteTomlBasic(Path.GetFullPath(_paths.Root))}\"]";
    }

    private void EnsureHttpAccessToken()
    {
        if (IsMcpHttpTransportSelected() && string.IsNullOrWhiteSpace(McpAccessTokenBox.Text))
            McpAccessTokenBox.Text = GenerateMcpAccessToken();
    }

    private static string QuoteYamlSingle(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    private static string QuoteTomlBasic(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal)
        .Replace("\r", "\\r", StringComparison.Ordinal)
        .Replace("\n", "\\n", StringComparison.Ordinal);
}
