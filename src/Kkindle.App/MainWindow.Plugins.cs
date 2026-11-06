using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using System.Text.Json.Nodes;
using Kkindle.Core;
using Kkindle.Infrastructure.Plugins;

namespace Kkindle;

/// <summary>
/// Plugin surfaces in the main window: the book context menu and detail-pane
/// entries, and the plugin section of the settings page. Plugins are installed
/// as <c>.kkplugin</c> archives and each page opens in its own web view window.
/// </summary>
public partial class MainWindow
{
    private static readonly FilePickerFileType PluginPackageFileType = new("Kkindle 插件")
    {
        Patterns = [$"*{PluginPackage.Extension}"],
        MimeTypes = ["application/zip"]
    };

    private List<InstalledPlugin> _pluginSnapshot = [];
    private readonly List<PluginWindow> _openPluginWindows = [];
    private bool _pluginsInitialized;
    private PluginBookTaskService? _pluginBookTasks;

    /// <summary>Installed, enabled plugins; refresh after install or removal.</summary>
    public IReadOnlyList<InstalledPlugin> Plugins => _pluginSnapshot;

    /// <summary>Raised whenever the installed set or its enabled flags change.</summary>
    public event EventHandler? PluginsChanged;

    private void InitializePlugins()
    {
        if (_pluginsInitialized) return;
        _pluginsInitialized = true;
        _pluginManager.Changed += (_, _) => Dispatcher.UIThread.Post(RefreshPluginContributions);
        RefreshPluginContributions();
    }

    private void RefreshPluginContributions()
    {
        _pluginSnapshot = _pluginManager.List()
            .Where(plugin => plugin.Enabled)
            .ToList();
        DetailPluginActionsButton.IsVisible = _pluginSnapshot.Any(plugin => plugin.Manifest.BookActions.Count > 0);
        RefreshPluginSettingsUi();
        RefreshPluginCommandMenu();
        RefreshReaderPluginActions();
        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---- global contributions -----------------------------------------

    /// <summary>Sends an app event to every plugin page that is currently open.</summary>
    private void BroadcastPluginEvent(string name, JsonObject payload)
    {
        if (_openPluginWindows.Count == 0) return;
        foreach (var window in _openPluginWindows.ToArray())
            window.RaiseHostEvent(name, payload.DeepClone());
    }

    private void BroadcastLibraryChanged() =>
        BroadcastPluginEvent("library.changed", new JsonObject());

    private void BroadcastReadingChanged(LocalDataChangeKind kind)
    {
        if (kind is LocalDataChangeKind.Settings or LocalDataChangeKind.ReadingLayout) return;
        BroadcastPluginEvent("reading.changed", new JsonObject
        {
            ["kind"] = kind.ToString().ToLowerInvariant()
        });
    }

    /// <summary>Live-updates the palette of every open plugin page.</summary>
    private void BroadcastPluginThemeChanged(string appTheme)
    {
        if (_openPluginWindows.Count == 0) return;
        var theme = GetPluginTheme();
        var payload = new JsonObject
        {
            ["theme"] = theme.IsDark ? "dark" : "light",
            ["appTheme"] = appTheme,
            ["paper"] = theme.Paper,
            ["ink"] = theme.Ink,
            ["mutedInk"] = theme.MutedInk,
            ["accent"] = theme.Accent
        };
        foreach (var window in _openPluginWindows.ToArray())
        {
            window.ApplyTheme(theme);
            window.RaiseHostEvent("theme.changed", payload.DeepClone());
        }
    }

    private void RefreshPluginCommandMenu()
    {
        var commands = _pluginSnapshot
            .SelectMany(plugin => plugin.Manifest.Commands.Select(command => (Plugin: plugin, Command: command)))
            .ToArray();
        PluginCommandsButton.IsVisible = commands.Length > 0;
        if (commands.Length == 0)
        {
            PluginCommandsButton.Flyout = null;
            return;
        }

        var flyout = new MenuFlyout();
        foreach (var (plugin, command) in commands)
        {
            var item = new MenuItem
            {
                Header = $"{plugin.Manifest.Name} · {command.Title}"
            };
            item.Click += async (_, _) => await OpenPluginCommandAsync(plugin, command);
            flyout.Items.Add(item);
        }
        PluginCommandsButton.Flyout = flyout;
    }

    private async Task OpenPluginCommandAsync(InstalledPlugin plugin, PluginCommand command) =>
        await ShowPluginWindowAsync(plugin, command.Page, command.Title, command.Width, command.Height, null, []);

    /// <summary>Rebuilds the reader selection bar's plugin buttons.</summary>
    private void RefreshReaderPluginActions()
    {
        var actions = _pluginSnapshot
            .SelectMany(plugin => plugin.Manifest.ReaderActions.Select(action => (Plugin: plugin, Action: action)))
            .ToArray();
        ReaderSelectionPluginActions.IsVisible = actions.Length > 0;
        ReaderSelectionPluginActions.Children.Clear();
        if (actions.Length == 0) return;

        ReaderSelectionPluginActions.Children.Add(new Avalonia.Controls.Shapes.Rectangle
        {
            Width = 1,
            Height = 18,
            Margin = new Thickness(2, 0),
            Fill = AppAppearanceResources.GetBrush("ReaderBorderBrush"),
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        });
        foreach (var (plugin, action) in actions)
        {
            var button = new Button
            {
                Content = new TextBlock
                {
                    Text = action.Title,
                    FontSize = 12,
                    MaxWidth = 140,
                    TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis
                }
            };
            button.Classes.Add("readerSelectionAction");
            button.Classes.Add("pluginReaderAction");
            ToolTip.SetTip(button, action.Title);
            button.Click += async (_, _) => await OpenReaderPluginActionAsync(plugin, action);
            ReaderSelectionPluginActions.Children.Add(button);
        }
    }

    private async Task OpenReaderPluginActionAsync(InstalledPlugin plugin, PluginReaderAction action)
    {
        var text = _readerPendingSelection;
        if (string.IsNullOrWhiteSpace(text)) return;
        var selection = new PluginSelectionContext(
            text,
            _readerPendingSelectionStartOffset,
            _readerPendingSelectionEndOffset,
            _readerPendingSelectionPrefix,
            _readerPendingSelectionSuffix,
            _readerBookCard?.Book.Id,
            _readerBookFile?.Id,
            _readerIsPdf ? _readerPdfPage : _readerChapterIndex,
            GetReaderChapterPath(),
            (CurrentReaderHost as IReaderPositionHost)?.CaptureContentPosition());
        IReadOnlyList<Guid> books = _readerBookCard is null ? [] : [_readerBookCard.Book.Id];
        HideReaderSelectionPopup();
        await ShowPluginWindowAsync(
            plugin, action.Page, action.Title, action.Width, action.Height,
            action.Id, books, selection);
    }

    // ---- book actions --------------------------------------------------

    /// <summary>Appends the enabled plugins' book actions; true when any were added.</summary>
    private bool AddPluginBookActionMenuItems(
        ContextMenu menu,
        BookCardViewModel card,
        IEnumerable<InstalledPlugin>? plugins = null)
    {
        // The context menu belongs to one card, but the user may have selected
        // several books; hand the plugin the whole selection when that card is
        // part of it.
        var selected = GetSelectedCards();
        var targets = selected.Contains(card) && selected.Count > 1
            ? selected.Where(candidate => candidate.Book is not null).ToArray()
            : [card];
        var added = false;
        foreach (var plugin in plugins ?? _pluginSnapshot)
        {
            foreach (var action in plugin.Manifest.BookActions)
            {
                menu.Items.Add(CreateMenuItem(
                    action.Title,
                    () => OpenPluginWindowAsync(plugin, action, targets)));
                added = true;
            }
        }
        return added;
    }

    private async void DetailPluginActionsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_selectedCard is not { } card) return;
        var active = _pluginSnapshot
            .Where(plugin => plugin.Manifest.BookActions.Count > 0)
            .ToArray();
        if (active.Length == 0) return;

        var menu = new ContextMenu();
        if (!AddPluginBookActionMenuItems(menu, card, active))
        {
            menu.Items.Add(new MenuItem
            {
                Header = T("没有可用的插件操作。请在设置 → 插件中导入插件。"),
                IsEnabled = false
            });
        }
        AttachInstantMenuHover(menu);
        menu.PlacementTarget = sender as Control ?? DetailPluginActionsButton;
        menu.Placement = Avalonia.Controls.PlacementMode.BottomEdgeAlignedRight;
        menu.Open(DetailPluginActionsButton);
    }

    private async Task OpenPluginWindowAsync(
        InstalledPlugin plugin,
        PluginBookAction action,
        params BookCardViewModel[] cards)
    {
        var books = cards
            .Select(card => card.Book)
            .Where(book => book is not null)
            .ToArray();
        if (books.Length == 0)
        {
            await ShowMessageAsync(T("书籍不存在"), T("无法读取所选书籍。"));
            return;
        }
        if (!action.AllowsMultipleBooks && books.Length > 1)
        {
            await ShowMessageAsync(T("插件操作"), T("“{0}”一次只能处理一本书。", action.Title));
            return;
        }
        await ShowPluginWindowAsync(
            plugin,
            action.Page,
            action.Title,
            action.Width,
            action.Height,
            action.Id,
            books.Select(book => book.Id).ToArray());
    }

    private async Task OpenPluginAppPageAsync(InstalledPlugin plugin)
    {
        if (plugin.Manifest.AppPage is not { } page) return;
        await ShowPluginWindowAsync(plugin, page.Page, page.Title, page.Width, page.Height, null, []);
    }

    private async Task ShowPluginWindowAsync(
        InstalledPlugin plugin,
        string page,
        string? pageTitle,
        int width,
        int height,
        string? actionId,
        IReadOnlyList<Guid> bookIds,
        PluginSelectionContext? selection = null)
    {
        try
        {
            var window = new PluginWindow(
                plugin,
                page,
                pageTitle,
                width,
                height,
                CreatePluginPageContext(plugin, actionId, bookIds, selection),
                GetPluginTheme(),
                _library,
                _paths,
                new PluginStorage(_pluginManager.GetDataDirectory(plugin.Id)),
                _secretProtector,
                () => _appSettings.NetworkEnabled,
                enableDevTools: _appSettings.PluginDeveloperMode,
                GetPluginBookTaskService(),
                _readerData);
            window.BooksUpdated += PluginWindow_BooksUpdated;
            _openPluginWindows.Add(window);
            window.Closed += (_, _) =>
            {
                window.BooksUpdated -= PluginWindow_BooksUpdated;
                _openPluginWindows.Remove(window);
            };
            await window.ShowDialog(this);
            if (bookIds.Count > 0) await RefreshLibraryAsync();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await ShowMessageAsync(T("无法打开插件"), UiText.Localize(exception.Message));
        }
    }

    private PluginBookTaskService GetPluginBookTaskService() =>
        _pluginBookTasks ??= new PluginBookTaskService(
            _paths,
            _library,
            _epubTranslationService,
            _formatConverter,
            _aiChatClient,
            _aiSettingsStore,
            _g2pwModelDownloader,
            () => _appSettings,
            EnsureBookFileAvailableAsync);

    private async void PluginWindow_BooksUpdated(object? sender, IReadOnlyCollection<Guid> bookIds)
    {
        if (bookIds.Count > 0)
            SetTaskStatus(T("插件已更新 {0} 本书籍信息。", bookIds.Count));
        try
        {
            await RefreshLibraryAsync();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
    }

    private PluginPageContext CreatePluginPageContext(
        InstalledPlugin plugin,
        string? actionId,
        IReadOnlyList<Guid> bookIds,
        PluginSelectionContext? selection = null) => new(
        plugin.Id,
        plugin.Manifest.Version,
        actionId,
        bookIds,
        UiText.CurrentLanguage,
        ApplicationVersion.GetDisplayVersion(typeof(MainWindow).Assembly),
        selection,
        _appSettings.PluginDeveloperMode);

    private static PluginTheme GetPluginTheme()
    {
        // Avalonia formats colours as #AARRGGBB, which CSS reads as
        // #RRGGBBAA; hand the page plain #RRGGBB instead.
        static string Resource(string key, string fallback)
        {
            if (Application.Current?.TryGetResource(key, Application.Current.ActualThemeVariant, out var value) != true)
                return fallback;
            return value switch
            {
                Avalonia.Media.Color color => ToCss(color),
                Avalonia.Media.ISolidColorBrush brush => ToCss(brush.Color),
                _ => fallback
            };
        }

        static string ToCss(Avalonia.Media.Color color) => color.A == 255
            ? $"#{color.R:X2}{color.G:X2}{color.B:X2}"
            : string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"rgba({color.R}, {color.G}, {color.B}, {color.A / 255d:0.###})");

        var isDark = Application.Current?.ActualThemeVariant == ThemeVariant.Dark;
        var paper = Resource("PaperColor", "#FFFFFF");
        return new PluginTheme(
            isDark || IsDarkCss(paper),
            paper,
            Resource("InkColor", "#000000"),
            Resource("MutedInkColor", "#5A5A5A"),
            Resource("HairlineColor", "#E2E2E2"),
            Resource("PressedColor", "#D9D9D9"),
            Resource("SoftHoverColor", "#F2F2F2"),
            Resource("StrongBorderColor", "#BFBFBF"),
            Resource("AccentColor", "#000000"),
            Resource("AccentHoverColor", "#333333"),
            Resource("AccentPressedColor", "#1A1A1A"),
            Resource("OnAccentColor", "#FFFFFF"),
            Resource("DangerColor", isDark ? "#EAA59B" : "#A7342F"));
    }

    // Night palettes can be selected while the Avalonia theme variant stays
    // Light; judge by the paper colour like AppAppearanceResources does.
    private static bool IsDarkCss(string color) =>
        color.Length == 7 && color[0] == '#'
        && int.TryParse(color.AsSpan(1, 2), System.Globalization.NumberStyles.HexNumber, null, out var red)
        && red < 90;

    // ---- settings ------------------------------------------------------

    private void RefreshPluginSettingsUi()
    {
        if (!_pluginsInitialized && PluginsPanel is null) return;
        var installed = _pluginManager.List();
        PluginsEmptyText.IsVisible = installed.Count == 0;
        PluginsPanel.Children.Clear();
        foreach (var plugin in installed)
            PluginsPanel.Children.Add(BuildPluginRow(plugin));
    }

    private Control BuildPluginRow(InstalledPlugin plugin)
    {
        var manifest = plugin.Manifest;
        var title = new TextBlock
        {
            Text = manifest.Name,
            FontSize = 14,
            FontWeight = Avalonia.Media.FontWeight.SemiBold,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        var meta = new TextBlock
        {
            Text = T("版本 {0}", manifest.Version),
            FontSize = 12,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(GetPluginTheme().MutedInk)),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        };
        var description = new TextBlock
        {
            Text = manifest.Description,
            FontSize = 12,
            Foreground = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(GetPluginTheme().MutedInk)),
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            IsVisible = !string.IsNullOrWhiteSpace(manifest.Description)
        };
        // The bare switch, without the settings row's hover background: this
        // one only occupies its own 24px in the action strip.
        var enabledToggle = new ToggleSwitch
        {
            IsChecked = plugin.Enabled,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
        };
        ToolTip.SetTip(enabledToggle, T("启用"));
        Avalonia.Automation.AutomationProperties.SetName(enabledToggle, T("启用"));
        enabledToggle.IsCheckedChanged += (_, _) =>
        {
            if (enabledToggle.IsChecked == plugin.Enabled) return;
            _pluginManager.SetEnabled(plugin.Id, enabledToggle.IsChecked == true);
        };

        var openButton = CreatePluginIconButton(
            this.FindResource("SidebarReadingGeometry") as Avalonia.Media.Geometry
                ?? Avalonia.Media.Geometry.Parse(
                    "M 12,5 V 21 M 20,19 A 2,2 0 0 0 22,17 V 5 A 2,2 0 0 0 20,3 H 16 A 5,5 0 0 0 12,5 A 5,5 0 0 0 8,3 H 4 A 2,2 0 0 0 2,5 V 17 A 2,2 0 0 0 4,19 H 8 A 5,5 0 0 1 12,21 A 5,5 0 0 1 16,19 Z"),
            T("打开"),
            () => OpenPluginAppPageAsync(plugin));
        openButton.IsVisible = manifest.AppPage is not null;

        var settingsButton = CreatePluginIconButton(
            this.FindResource("SidebarSettingsGeometry") as Avalonia.Media.Geometry
                ?? Avalonia.Media.Geometry.Parse("M 4,6 H 20 M 4,12 H 20 M 4,18 H 20 M 8,4 V 8 M 16,10 V 14 M 10,16 V 20"),
            T("设置…"),
            () => OpenPluginSettingsAsync(plugin));
        settingsButton.IsVisible = manifest.SettingsPage is not null;

        var removeButton = CreatePluginIconButton(
            Avalonia.Media.Geometry.Parse(
                "M 4,6 H 20 M 9,6 V 3 H 15 V 6 M 6,6 L 7,21 H 17 L 18,6 M 10,10 V 17 M 14,10 V 17"),
            T("卸载"),
            () => UninstallPluginAsync(plugin));

        var actions = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Children = { enabledToggle, openButton, settingsButton, removeButton }
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Children = { new StackPanel { Spacing = 4, Children = { title, meta } }, actions }
        };
        Grid.SetColumn(actions, 1);

        return new Border
        {
            Classes = { "flatSubsection" },
            Padding = new Thickness(12),
            Child = new StackPanel
            {
                Spacing = 6,
                Children = { header, description }
            }
        };
    }

    private static Button CreatePluginIconButton(
        Avalonia.Media.Geometry glyph,
        string label,
        Func<Task> action)
    {
        var button = new Button
        {
            Classes = { "quiet", "libraryToolbar" },
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Content = new Avalonia.Controls.Shapes.Path
            {
                Classes = { "libraryGlyph" },
                Data = glyph
            }
        };
        ToolTip.SetTip(button, label);
        Avalonia.Automation.AutomationProperties.SetName(button, label);
        button.Click += async (_, _) => await action();
        return button;
    }

    private static string DescribePluginPermissions(PluginManifest manifest)
    {
        var parts = new List<string>();
        if (manifest.LibraryAccess >= PluginLibraryAccess.Read) parts.Add(UiText.Get("读取书籍信息"));
        if (manifest.LibraryAccess >= PluginLibraryAccess.Write) parts.Add(UiText.Get("修改书籍信息"));
        if (manifest.HasLibraryScopeAll) parts.Add(UiText.Get("搜索整个书库并读取阅读数据"));
        if (manifest.CanImportLibrary) parts.Add(UiText.Get("下载并导入书籍"));
        if (manifest.AllowsTranslation) parts.Add(UiText.Get("翻译书籍"));
        if (manifest.AllowsPinyin) parts.Add(UiText.Get("生成注音书"));
        if (manifest.UsesSecrets) parts.Add(UiText.Get("加密保存账号信息"));
        if (manifest.NetworkOrigins.Count > 0)
        {
            var hosts = manifest.NetworkOrigins
                .Select(origin => origin.Host)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            parts.Add(UiText.Get("访问网站：{0}", string.Join("、", hosts)));
        }
        if (manifest.UsesStorage) parts.Add(UiText.Get("保存自己的设置"));
        return parts.Count == 0
            ? UiText.Get("不需要任何权限")
            : UiText.Get("权限：{0}", string.Join("；", parts));
    }

    private async Task OpenPluginSettingsAsync(InstalledPlugin plugin)
    {
        if (plugin.Manifest.SettingsPage is not { } page) return;
        try
        {
            await ShowPluginWindowAsync(plugin, page.Page, T("设置"), page.Width, page.Height, null, []);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await ShowMessageAsync(T("无法打开插件设置"), UiText.Localize(exception.Message));
        }
    }

    private async Task UninstallPluginAsync(InstalledPlugin plugin)
    {
        var confirmed = await ConfirmAsync(
            T("卸载插件"),
            T("确定要卸载“{0}”吗？插件保存的设置也会一并删除。", plugin.Manifest.Name),
            T("卸载"));
        if (!confirmed) return;
        _pluginManager.Uninstall(plugin.Id);
        SetTaskStatus(T("已卸载插件“{0}”。", plugin.Manifest.Name));
    }

    private async void ImportPluginButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = T("导入插件"),
                AllowMultiple = false,
                FileTypeFilter = [PluginPackageFileType]
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;
            await InstallPluginFromPathAsync(path);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await ShowMessageAsync(T("导入插件失败"), UiText.Localize(exception.Message));
        }
    }

    private async void InstallPluginFromFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = T("从文件夹安装插件"),
                AllowMultiple = false
            });
            if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } path) return;
            await InstallPluginFromDirectoryAsync(path);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            await ShowMessageAsync(T("导入插件失败"), UiText.Localize(exception.Message));
        }
    }

    /// <summary>Inspects a package, asks for confirmation, then installs it.</summary>
    public async Task<InstalledPlugin> InstallPluginFromPathAsync(string packagePath)
    {
        PluginInstallPreview preview;
        try
        {
            preview = _pluginManager.Inspect(packagePath);
        }
        catch (Exception exception) when (exception is PluginPackageException or IOException or UnauthorizedAccessException)
        {
            throw new PluginPackageException(UiText.Localize(exception.Message));
        }
        return await ConfirmAndInstallPluginAsync(preview, () => _pluginManager.Install(packagePath));
    }

    /// <summary>Installs straight from an unpacked development folder.</summary>
    public async Task<InstalledPlugin> InstallPluginFromDirectoryAsync(string directory)
    {
        PluginInstallPreview preview;
        try
        {
            preview = _pluginManager.InspectDirectory(directory);
        }
        catch (Exception exception) when (exception is PluginPackageException or IOException or UnauthorizedAccessException)
        {
            throw new PluginPackageException(UiText.Localize(exception.Message));
        }
        return await ConfirmAndInstallPluginAsync(preview, () => _pluginManager.InstallFromDirectory(directory));
    }

    private async Task<InstalledPlugin> ConfirmAndInstallPluginAsync(
        PluginInstallPreview preview,
        Func<InstalledPlugin> install)
    {
        var manifest = preview.Manifest;
        var lines = new List<string>
        {
            preview.IsUpdate
                ? T("将把“{0}”从 {1} 更新到 {2}。", manifest.Name, preview.Existing!.Manifest.Version, manifest.Version)
                : T("将安装插件“{0}”{1}。", manifest.Name, string.IsNullOrWhiteSpace(manifest.Version) ? string.Empty : $" {manifest.Version}"),
            string.Empty,
            DescribePluginPermissions(manifest),
            string.Empty,
            T("插件页面运行在受限的网页视图中，只能通过应用提供的接口访问网络和书库。请只安装来自可信来源的插件。")
        };
        if (preview.IsDowngrade)
            lines.Add(T("注意：这是较旧的插件版本。"));
        if (!await ConfirmAsync(T("导入插件"), string.Join('\n', lines), T("安装"), cancelText: T("取消")))
            throw new PluginPackageException(T("已取消导入。"));

        var installed = install();
        SetTaskStatus(T("已安装插件“{0}”。", installed.Manifest.Name));
        RefreshPluginContributions();
        return installed;
    }

    private void OpenPluginsFolderButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(_pluginManager.PluginsDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _pluginManager.PluginsDirectory,
                UseShellExecute = true
            });
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _ = ShowMessageAsync(T("无法打开插件目录"), UiText.Localize(exception.Message));
        }
    }
}
