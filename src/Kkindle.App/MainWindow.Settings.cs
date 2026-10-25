using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Automation;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Infrastructure;
using System.Xml.Linq;

namespace Kkindle;

public sealed record SettingsSearchResult(
    string Title,
    string Detail,
    string CategoryLabel,
    string AccessibleName,
    string Category,
    Control Target,
    Expander? Expander,
    string SearchCorpus);

public partial class MainWindow
{
    private async void AboutUserGroupButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (Clipboard is not { } clipboard)
            {
                ShowSettingsCapsule(T("剪贴板不可用。"), 4000);
                return;
            }
            await clipboard.SetTextAsync("1109898894");
            ShowSettingsCapsule(Application.Current?.FindResource("Ui.About.UserGroupCopied")?.ToString() ?? "群号已复制到剪贴板。", 2000, success: true);
        }
        catch
        {
            ShowSettingsCapsule(Application.Current?.FindResource("Ui.About.UserGroupCopyFailed")?.ToString() ?? "复制群号失败。", 4000);
        }
    }

    private string _activeSettingsCategory = "General";
    private bool _mainReaderAiSettingsLoaded;
    private Task? _mainReaderAiSettingsLoadTask;
    private readonly SemaphoreSlim _appSettingsSaveGate = new(1, 1);
    private long _appSettingsEditVersion;
    private long _appSettingsSavedVersion;
    private bool _skipS3SyncOnExit;
    private bool _s3SettingsDirty;
    private bool _s3SettingsSaving;
    private long _s3SettingsEditVersion;
    private Task<bool>? _s3SettingsSaveTask;
    private bool _suppressKindleEmailProviderChange;
    private bool _kindleEmailTestBusy;
    private CancellationTokenSource? _settingsSearchCancellation;
    private readonly Dictionary<string, List<SettingsSearchResult>> _settingsSearchCache = new();
    private int _settingsSearchSelectedIndex;

    private static readonly string[][] SettingsSearchSynonyms =
    [
        ["同步", "sync", "synchronization", "cloud", "云端", "设备同步"],
        ["字体", "font", "typeface", "字号", "字形"],
        ["邮箱", "email", "mail", "邮件", "smtp", "收件地址"],
        ["人工智能", "ai", "模型", "问答", "密钥", "api"],
        ["翻译", "translation", "translate", "译文"],
        ["拼音", "pinyin", "注音"],
        ["外观", "theme", "主题"],
        ["kindle", "推送", "发送", "send to kindle", "usb"],
        ["备份", "backup", "恢复", "restore", "导出备份"],
        ["更新", "update", "upgrade", "检查更新", "软件版本"],
        ["代理", "proxy", "代理服务器", "google proxy"],
        ["快捷键", "shortcut", "keyboard shortcut", "热键"],
        ["calibre", "格式转换", "电子书转换", "converter"],
        ["zlibrary", "z-library", "图书下载", "下载账号"],
        ["目录", "directory", "folder", "存储路径", "数据路径"],
        ["诊断", "diagnostics", "日志", "log export"],
        ["高级", "advanced", "mcp", "集成", "integration"],
        ["账户", "account", "账号", "登录"]
    ];

    private static readonly Lazy<IReadOnlyDictionary<string, string[]>> SettingsLocalizedSearchTerms =
        new(BuildSettingsLocalizedSearchTerms);

    private sealed record KindleEmailProviderPreset(string Id, string SmtpHost, int SmtpPort, bool EnableSsl);

    private static readonly KindleEmailProviderPreset[] KindleEmailProviderPresets =
    [
        new("gmail", "smtp.gmail.com", 587, true),
        new("qq", "smtp.qq.com", 587, true),
        // 163 exposes STARTTLS on port 25. Its 587 endpoint is not
        // consistently available and closes the connection before the SMTP
        // greeting on some networks.
        new("163", "smtp.163.com", 25, true),
        new("outlook", "smtp-mail.outlook.com", 587, true)
    ];

    private void SettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (BlockNavigationWhileTransferring()) return;
        ShowStage3Page(SettingsPage, SettingsNavigationButton);
        SettingsDataPathText.Text = _paths.Data;
        InitializeMcpSettings();
        ShowSettingsSection(_activeSettingsCategory);
    }

    private void SystemBackupNavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (BlockNavigationWhileTransferring()) return;
        OpenSettingsExpander("Data", SettingsBackupExpander);
    }

    private void SystemS3SyncNavigationButton_Click(object? sender, RoutedEventArgs e)
    {
        if (BlockNavigationWhileTransferring()) return;
        OpenSettingsExpander("Data", SettingsS3Expander);
    }

    private void KindleEmailSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (BlockNavigationWhileTransferring()) return;
        OpenSettingsExpander("Kindle", SettingsSendToKindleExpander);
        FocusSettingsControl(KindleEmailRecipientBox);
    }

    private async void ReaderAiSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (BlockNavigationWhileTransferring()) return;
        OpenSettingsExpander("Reading", SettingsAiExpander);
        await EnsureMainReaderAiSettingsLoadedAsync();
        if (SettingsReadingSection.IsVisible && SettingsAiExpander.IsExpanded)
            FocusSettingsControl(MainReaderAiBaseUrlBox);
    }

    private async void ReaderAiVectorStatusButton_Click(object? sender, RoutedEventArgs e)
    {
        if (BlockNavigationWhileTransferring()) return;
        if (ReaderRoot.IsVisible) await CloseReaderAsync();
        OpenSettingsExpander("Reading", SettingsAiExpander);
        await EnsureMainReaderAiSettingsLoadedAsync();
        Dispatcher.UIThread.Post(() =>
        {
            if (!MainReaderEmbeddingModelSelectorBox.IsEffectivelyVisible) return;
            MainReaderEmbeddingModelSelectorBox.BringIntoView();
            MainReaderEmbeddingModelSelectorBox.Focus();
        }, DispatcherPriority.Loaded);
    }

    private void SettingsCategoryButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag }) ShowSettingsSection(tag);
    }

    private void ShowSettingsSection(string tag)
    {
        var sections = new Dictionary<string, Control>(StringComparer.OrdinalIgnoreCase)
        {
            ["General"] = SettingsGeneralSection,
            ["Library"] = SettingsLibrarySection,
            ["Reading"] = SettingsReadingSection,
            ["Kindle"] = SettingsKindleSection,
            ["Data"] = SettingsDataSection,
            ["About"] = SettingsAboutSection,
            ["Advanced"] = SettingsAdvancedSection
        };
        if (!sections.TryGetValue(tag, out var activeSection))
        {
            tag = "General";
            activeSection = SettingsGeneralSection;
        }

        var focused = FocusManager?.GetFocusedElement() as Control;
        var focusWasInContent = focused is not null && focused.GetVisualAncestors()
            .Any(ancestor => ReferenceEquals(ancestor, SettingsScrollViewer) || ReferenceEquals(ancestor, SettingsS3ActionBar));
        foreach (var section in sections.Values)
            section.IsVisible = ReferenceEquals(section, activeSection);
        if (!string.Equals(_activeSettingsCategory, tag, StringComparison.OrdinalIgnoreCase))
            SettingsScrollViewer.Offset = default;
        _activeSettingsCategory = tag;
        UpdateS3SettingsActions();
        Button[] buttons = [SettingsGeneralButton, SettingsLibraryButton, SettingsReadingButton, SettingsKindleButton, SettingsDataButton, SettingsAdvancedButton, SettingsAboutButton];
        foreach (var button in buttons)
        {
            var active = string.Equals(button.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase);
            button.Classes.Set("active", active);
            if (active && focusWasInContent && focused?.IsEffectivelyVisible == false) button.Focus();
        }

    }

    private async void SettingsSearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        _settingsSearchCancellation?.Cancel();
        _settingsSearchCancellation = null;
        var query = SettingsSearchBox.Text?.Trim() ?? string.Empty;
        var searching = query.Length > 0;
        SettingsCategoryButtons.IsVisible = !searching;
        SettingsScrollViewer.IsVisible = !searching;
        SettingsSearchNoResultsText.IsVisible = false;
        SettingsSearchResultsPanel.IsVisible = false;
        SettingsSearchResults.ItemsSource = null;

        if (!searching) return;

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _settingsSearchCancellation = cancellation;
        try
        {
        await Task.Delay(180, cancellation.Token);
        var key = UiText.Get("常规") + "|" + query;
        if (!_settingsSearchCache.TryGetValue(key, out var results))
        {
            results = BuildSettingsSearchResults(query);
            if (_settingsSearchCache.Count >= 64) _settingsSearchCache.Clear();
            _settingsSearchCache[key] = results;
        }
        _settingsSearchSelectedIndex = 0;
        SettingsSearchResults.ItemsSource = results;
        SettingsSearchResultsPanel.IsVisible = results.Count > 0;
        SettingsSearchNoResultsText.IsVisible = results.Count == 0;
        Dispatcher.UIThread.Post(UpdateSettingsSearchSelection, DispatcherPriority.Loaded);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_settingsSearchCancellation, cancellation)) _settingsSearchCancellation = null;
            cancellation.Dispose();
        }
    }

    private void SettingsSearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !string.IsNullOrWhiteSpace(SettingsSearchBox.Text))
        {
            SettingsSearchBox.Text = string.Empty;
            e.Handled = true;
        }
        else if (e.Key is Key.Down or Key.Up && SettingsSearchResults.Items.Count > 0)
        {
            _settingsSearchSelectedIndex = Math.Clamp(
                _settingsSearchSelectedIndex + (e.Key == Key.Down ? 1 : -1),
                0, SettingsSearchResults.Items.Count - 1);
            UpdateSettingsSearchSelection();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SettingsSearchResults.Items
                     .ElementAtOrDefault(_settingsSearchSelectedIndex) is SettingsSearchResult result)
        {
            NavigateToSettingsSearchResult(result);
            e.Handled = true;
        }
    }

    private void UpdateSettingsSearchSelection()
    {
        var buttons = SettingsSearchResults.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Tag is SettingsSearchResult).ToArray();
        for (var index = 0; index < buttons.Length; index++)
        {
            var selected = index == _settingsSearchSelectedIndex;
            buttons[index].Classes.Set("selected", selected);
            if (selected) buttons[index].BringIntoView();
        }
    }

    private void SettingsSearchResultButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: SettingsSearchResult result })
            NavigateToSettingsSearchResult(result);
    }

    private void NavigateToSettingsSearchResult(SettingsSearchResult result)
    {
        if (BlockNavigationWhileTransferring()) return;

        SettingsSearchBox.Text = string.Empty;
        ShowStage3Page(SettingsPage, SettingsNavigationButton);
        ShowSettingsSection(result.Category);

        foreach (var expander in result.Target.GetSelfAndLogicalAncestors()
                     .OfType<Expander>()
                     .Reverse())
            expander.IsExpanded = true;

        Dispatcher.UIThread.Post(() =>
        {
            if (!result.Target.IsEffectivelyVisible) return;
            result.Target.BringIntoView();

            var focusTarget = result.Target.GetSelfAndLogicalDescendants()
                .OfType<Control>()
                .FirstOrDefault(control =>
                    control.IsEffectivelyVisible && control.IsEnabled && control.Focusable &&
                    control is Button or TextBox or ComboBox or ToggleSwitch or CheckBox or RadioButton or NumericUpDown);
            focusTarget?.Focus();
        }, DispatcherPriority.Loaded);
    }

    private List<SettingsSearchResult> BuildSettingsSearchResults(string query)
    {
        var sections = new[]
        {
            ("General", SettingsGeneralButton.Content?.ToString() ?? T("常规"), SettingsGeneralSection),
            ("Library", SettingsLibraryButton.Content?.ToString() ?? T("书库"), SettingsLibrarySection),
            ("Reading", SettingsReadingButton.Content?.ToString() ?? T("阅读"), SettingsReadingSection),
            ("Kindle", SettingsKindleButton.Content?.ToString() ?? T("Kindle"), SettingsKindleSection),
            ("Data", SettingsDataButton.Content?.ToString() ?? T("数据"), SettingsDataSection),
            ("Advanced", SettingsAdvancedButton.Content?.ToString() ?? T("高级"), SettingsAdvancedSection),
            ("About", SettingsAboutButton.Content?.ToString() ?? T("关于"), SettingsAboutSection)
        };
        var results = new List<SettingsSearchResult>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (category, categoryLabel, section) in sections)
        {
            var descendants = section.GetSelfAndLogicalDescendants().OfType<Control>().ToArray();
            foreach (var expander in descendants.OfType<Expander>())
            {
                var title = DisplayValue(expander.Header) ?? "";
                if (string.IsNullOrWhiteSpace(title)) continue;
                var detail = DisplayValue(expander.Tag) ?? string.Empty;
                var corpus = CollectSettingsText(expander.GetSelfAndLogicalDescendants().OfType<Control>());
                AddResult(title, detail, categoryLabel, category, expander, expander, corpus);
            }

            // Settings outside an expander still need a useful destination.
            // Index each direct section child as one destination so a matching
            // label or description can focus its associated input without
            // exposing hundreds of individual controls in the result list.
            foreach (var child in section.GetLogicalChildren().OfType<Control>())
            {
                if (child is Expander) continue;
                var controls = child.GetSelfAndLogicalDescendants().OfType<Control>().ToArray();
                var corpus = CollectSettingsText(controls);
                var title = controls.Select(GetSettingsControlTitle).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
                if (string.IsNullOrWhiteSpace(title)) continue;
                var detail = controls.Select(GetSettingsControlDescription).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
                AddResult(title, detail, categoryLabel, category, child, null, corpus);
            }

            // The category title and explanatory copy are searchable too,
            // including sections currently hidden by the category switcher.
            var sectionCorpus = CollectSettingsText(descendants);
            var sectionTitle = descendants.OfType<TextBlock>()
                .FirstOrDefault(text => text.Classes.Contains("settingsSectionTitle"))?.Text;
            if (!string.IsNullOrWhiteSpace(sectionTitle))
                AddResult(DisplayValue(sectionTitle)!, string.Empty, categoryLabel, category, section, null, sectionCorpus);
        }

        var terms = ExpandSettingsSearchTerms(query);
        return results
            .Select(result => (Result: result, Score: GetSearchScore(result, terms)))
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Result.Title, StringComparer.CurrentCultureIgnoreCase)
            .Select(item => item.Result)
            .Take(18)
            .ToList();

        void AddResult(string title, string detail, string categoryLabel, string category, Control target, Expander? expander, string corpus)
        {
            var expandedCorpus = ExpandCorpus(corpus + " " + title + " " + detail + " " + categoryLabel);
            if (GetSearchScore(new SettingsSearchResult(title, detail, categoryLabel, "", category, target, expander, expandedCorpus), ExpandSettingsSearchTerms(query)) <= 0)
                return;

            var key = $"{category}|{target.GetType().FullName}|{title}|{detail}";
            if (!seen.Add(key)) return;
            var accessibleName = string.Join(", ", new[] { title, detail, categoryLabel }.Where(value => !string.IsNullOrWhiteSpace(value)));
            results.Add(new SettingsSearchResult(title, detail, categoryLabel, accessibleName, category, target, expander, expandedCorpus));
        }
    }

    private static string CollectSettingsText(IEnumerable<Control> controls)
    {
        var values = new List<string>();
        foreach (var control in controls)
        {
            switch (control)
            {
                case TextBlock { Text: { Length: > 0 } text }:
                    Add(text);
                    break;
                case TextBox { PlaceholderText: { Length: > 0 } placeholder }:
                    Add(placeholder);
                    break;
            }

            if (control is Expander expander)
            {
                Add(DisplayValue(expander.Header));
                Add(DisplayValue(expander.Tag));
            }
            if (control is ContentControl contentControl)
                Add(DisplayValue(contentControl.Content));
            Add(DisplayValue(control.Tag));
            Add(DisplayValue(ToolTip.GetTip(control)));
            Add(AutomationProperties.GetName(control));
        }

        return string.Join(" ", values);

        void Add(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            values.Add(value);
            var normalized = NormalizeSettingsSearchText(value);
            if (SettingsLocalizedSearchTerms.Value.TryGetValue(normalized, out var translations))
                values.AddRange(translations);
        }
    }

    private static string? GetSettingsControlTitle(Control control)
    {
        if (control is Expander expander && DisplayValue(expander.Header) is { } header) return header;
        if (AutomationProperties.GetName(control) is { Length: > 0 } accessibleName) return accessibleName;
        if (control is ContentControl { Content: { } content } && DisplayValue(content) is { } displayContent) return displayContent;
        if (control is TextBlock { Text: { Length: > 0 } text }) return DisplayValue(text);
        return null;
    }

    private static string? GetSettingsControlDescription(Control control)
    {
        if (control is Expander expander && DisplayValue(expander.Tag) is { Length: > 0 } description) return description;
        if (DisplayValue(ToolTip.GetTip(control)) is { Length: > 0 } tip) return tip;
        return null;
    }

    private static string? DisplayValue(object? value)
    {
        var text = value switch
        {
            string valueText => valueText,
            TextBlock textBlock => textBlock.Text,
            TextBox textBox => textBox.PlaceholderText,
            ContentControl { Content: string content } => content,
            Control control => AutomationProperties.GetName(control),
            _ => null
        };
        text = text?.Trim();
        return string.IsNullOrWhiteSpace(text) ? null : UiText.Localize(text);
    }

    private static string ExpandCorpus(string corpus)
    {
        var normalized = NormalizeSettingsSearchText(corpus);
        var variants = new List<string> { corpus };
        foreach (var group in SettingsSearchSynonyms)
            if (group.Any(term => MatchesSettingsTerm(normalized, NormalizeSettingsSearchText(term))))
                variants.Add(string.Join(" ", group));
        return string.Join(" ", variants);
    }

    private static string[] ExpandSettingsSearchTerms(string query)
    {
        var normalized = NormalizeSettingsSearchText(query);
        var variants = new HashSet<string>(StringComparer.Ordinal) { normalized };
        if (SettingsLocalizedSearchTerms.Value.TryGetValue(normalized, out var translations))
            foreach (var translation in translations)
                variants.Add(NormalizeSettingsSearchText(translation));
        foreach (var group in SettingsSearchSynonyms)
            if (group.Any(term => MatchesSettingsTerm(normalized, NormalizeSettingsSearchText(term))))
                foreach (var term in group)
                    variants.Add(NormalizeSettingsSearchText(term));
        return variants.Where(term => term.Length > 0).ToArray();
    }

    private static int GetSearchScore(SettingsSearchResult result, IReadOnlyCollection<string> terms)
    {
        var title = NormalizeSettingsSearchText(result.Title);
        var corpus = NormalizeSettingsSearchText(result.SearchCorpus);
        var score = 0;
        foreach (var term in terms)
        {
            if (MatchesSettingsTerm(title, term)) score = Math.Max(score, term.Length >= 3 ? 100 : 80);
            else if (MatchesSettingsTerm(corpus, term)) score = Math.Max(score, 50);
        }
        return score;
    }

    private static bool MatchesSettingsTerm(string source, string term)
    {
        if (source.Length == 0 || term.Length == 0) return false;
        var isLatinWord = term.Any(char.IsLetter) && term.All(character => character <= 0x7F && (char.IsLetterOrDigit(character) || character == ' '));
        if (!isLatinWord) return source.Contains(term, StringComparison.Ordinal);

        var pattern = $"(?<![A-Za-z0-9]){Regex.Escape(term).Replace("\\ ", "\\s+")}(?![A-Za-z0-9])";
        return Regex.IsMatch(source, pattern, RegexOptions.CultureInvariant);
    }

    private static IReadOnlyDictionary<string, string[]> BuildSettingsLocalizedSearchTerms()
    {
        const string xamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
        const string appAssembly = "Kkindle.App";
        try
        {
            var dictionaries = new[] { "zh-CN", "en-US" }
                .Select(language => LoadUiStringDictionary(appAssembly, language, xamlNamespace))
                .ToArray();
            if (dictionaries[0].Count == 0 || dictionaries[1].Count == 0) return new Dictionary<string, string[]>();

            var variants = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (var (key, chinese) in dictionaries[0])
            {
                if (!dictionaries[1].TryGetValue(key, out var english)) continue;
                Add(chinese, english);
                Add(english, chinese);
            }

            return variants.ToDictionary(
                pair => pair.Key,
                pair => pair.Value.ToArray(),
                StringComparer.Ordinal);

            void Add(string source, string translation)
            {
                var normalized = NormalizeSettingsSearchText(source);
                if (normalized.Length < 2 || normalized.Length > 220) return;
                if (!variants.TryGetValue(normalized, out var values))
                    variants[normalized] = values = new HashSet<string>(StringComparer.Ordinal);
                values.Add(translation);
            }
        }
        catch
        {
            // The explicit settings vocabulary below remains available if an
            // app host does not expose compiled Avalonia assets (for example,
            // a very early startup or a stripped-down test host).
            return new Dictionary<string, string[]>();
        }

        static Dictionary<string, string> LoadUiStringDictionary(string assembly, string language, string xamlNamespace)
        {
            var uri = new Uri($"avares://{assembly}/Resources/Strings.{language}.axaml");
            using var stream = AssetLoader.Open(uri);
            var document = XDocument.Load(stream, LoadOptions.None);
            var keyName = XName.Get("Key", xamlNamespace);
            return document.Root?.Elements()
                       .Where(element => element.Name.LocalName == "String")
                       .Select(element => (Key: element.Attribute(keyName)?.Value, Value: element.Value.Trim()))
                       .Where(item => !string.IsNullOrWhiteSpace(item.Key) && !string.IsNullOrWhiteSpace(item.Value))
                       .GroupBy(item => item.Key!, StringComparer.Ordinal)
                       .ToDictionary(group => group.Key, group => group.Last().Value, StringComparer.Ordinal)
                   ?? new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }

    private static string NormalizeSettingsSearchText(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value.Normalize(NormalizationForm.FormKD))
        {
            if (char.IsLetterOrDigit(character))
            {
                if (pendingSpace && builder.Length > 0) builder.Append(' ');
                builder.Append(char.ToLowerInvariant(character));
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }
        return builder.ToString();
    }

    private static void FocusSettingsControl(Control control) => Dispatcher.UIThread.Post(() =>
    {
        if (control.IsEffectivelyVisible && TopLevel.GetTopLevel(control) is not null) control.Focus();
    }, DispatcherPriority.Loaded);

    private void InitializeKindleEmailClipboardSupport()
    {
        KindleEmailProviderBox.SelectionChanged += KindleEmailProviderBox_SelectionChanged;

        TextBox[] fields =
        [
            KindleEmailRecipientBox,
            KindleEmailSenderBox,
            KindleEmailSmtpHostBox,
            KindleEmailSmtpPortBox,
            KindleEmailUsernameBox,
            KindleEmailPasswordBox
        ];

        foreach (var field in fields)
        {
            // Keep paste working even if the platform theme does not route its
            // default TextBox hotkey/context flyout through this custom window.
            field.AddHandler(
                InputElement.KeyDownEvent,
                KindleEmailTextBox_KeyDown,
                RoutingStrategies.Tunnel,
                handledEventsToo: true);
            field.ContextFlyout = CreateKindleEmailTextBoxContextFlyout(field);
        }

        UpdateKindleEmailProviderControls();
    }

    private static void KindleEmailTextBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not TextBox textBox || textBox.IsReadOnly) return;

        var modifiers = e.KeyModifiers;
        var pasteShortcut = e.Key == Key.V
            && (modifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        var shiftInsertShortcut = e.Key == Key.Insert
            && (modifiers & KeyModifiers.Shift) != 0;
        if (!pasteShortcut && !shiftInsertShortcut) return;

        e.Handled = true;
        textBox.Paste();
    }

    private MenuFlyout CreateKindleEmailTextBoxContextFlyout(TextBox textBox)
    {
        var cut = new MenuItem { Header = T("剪切") };
        var copy = new MenuItem { Header = T("复制") };
        var paste = new MenuItem { Header = T("粘贴") };
        var selectAll = new MenuItem { Header = T("全选") };

        cut.Click += (_, _) =>
        {
            textBox.Focus();
            textBox.Cut();
        };
        copy.Click += (_, _) =>
        {
            textBox.Focus();
            textBox.Copy();
        };
        paste.Click += (_, _) =>
        {
            textBox.Focus();
            textBox.Paste();
        };
        selectAll.Click += (_, _) =>
        {
            textBox.Focus();
            textBox.SelectAll();
        };

        var menu = new MenuFlyout();
        menu.Items.Add(cut);
        menu.Items.Add(copy);
        menu.Items.Add(paste);
        menu.Items.Add(new Separator());
        menu.Items.Add(selectAll);
        menu.Opened += (_, _) =>
        {
            cut.IsEnabled = textBox.CanCut;
            copy.IsEnabled = textBox.CanCopy;
            // Do not use CanPaste here: the fallback exists specifically for
            // platforms where that theme command state is stale.
            paste.IsEnabled = !textBox.IsReadOnly;
            selectAll.IsEnabled = !string.IsNullOrEmpty(textBox.Text);
        };
        return menu;
    }

    private void KindleEmailProviderBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressKindleEmailProviderChange
            || KindleEmailProviderBox.SelectedItem is not ComboBoxItem { Tag: string providerId })
            return;

        ApplyKindleEmailProviderPreset(providerId);
    }

    private void ApplyKindleEmailProviderPreset(string providerId)
    {
        var preset = KindleEmailProviderPresets.FirstOrDefault(provider =>
            string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase));
        KindleEmailSmtpAdvancedPanel.IsVisible = preset is null;
        if (preset is null) return;

        KindleEmailSmtpHostBox.Text = preset.SmtpHost;
        KindleEmailSmtpPortBox.Text = preset.SmtpPort.ToString(CultureInfo.InvariantCulture);
        KindleEmailSslCheck.IsChecked = preset.EnableSsl;
    }

    private void UpdateKindleEmailProviderControls()
    {
        if (KindleEmailProviderBox.SelectedItem is not ComboBoxItem { Tag: string providerId })
        {
            KindleEmailSmtpAdvancedPanel.IsVisible = true;
            return;
        }

        var preset = KindleEmailProviderPresets.FirstOrDefault(provider =>
            string.Equals(provider.Id, providerId, StringComparison.OrdinalIgnoreCase));
        KindleEmailSmtpAdvancedPanel.IsVisible = preset is null;
        if (preset is not null)
            ApplyKindleEmailProviderPreset(providerId);
    }

    private static int GetKindleEmailProviderIndex(KindleEmailSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SmtpHost)) return 0;

        var index = Array.FindIndex(KindleEmailProviderPresets, provider =>
            string.Equals(provider.SmtpHost, settings.SmtpHost, StringComparison.OrdinalIgnoreCase)
            && provider.SmtpPort == settings.SmtpPort
            && provider.EnableSsl == settings.EnableSsl);
        // The first preset build used 587 for 163. Treat that value as the
        // old built-in preset so existing users get the corrected preset
        // instead of being pushed into the custom SMTP form.
        if (index < 0
            && string.Equals(settings.SmtpHost, "smtp.163.com", StringComparison.OrdinalIgnoreCase)
            && settings.SmtpPort == 587
            && settings.EnableSsl)
            return Array.FindIndex(KindleEmailProviderPresets, provider => provider.Id == "163");
        return index >= 0 ? index : KindleEmailProviderPresets.Length;
    }

    private void OpenSettingsExpander(string category, Expander expander)
    {
        if (BlockNavigationWhileTransferring()) return;
        ShowStage3Page(SettingsPage, SettingsNavigationButton);
        ShowSettingsSection(category);
        expander.IsExpanded = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (expander.IsEffectivelyVisible) expander.BringIntoView();
        }, DispatcherPriority.Loaded);
        UpdateS3SettingsActions();
    }

    private async void SettingsExpander_Expanded(object? sender, RoutedEventArgs e)
    {
        if (!ReferenceEquals(sender, e.Source)) return;
        UpdateS3SettingsActions();
        try
        {
            if (ReferenceEquals(sender, SettingsAiExpander))
                await EnsureMainReaderAiSettingsLoadedAsync();
            else if (ReferenceEquals(sender, SettingsTrashExpander))
                await RefreshTrashItemsAsync();
            else if (ReferenceEquals(sender, SettingsDiagnosticsExpander))
                await RefreshPlatformDiagnosticsAsync();
            else if (ReferenceEquals(sender, SettingsLocalResourcesExpander))
                await RefreshManagedResourcesAsync(_lifetimeCancellation.Token);
            else if (ReferenceEquals(sender, SettingsMcpExpander))
                RefreshMcpExecutableStatus();
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested) { }
        catch (Exception exception)
        {
            SettingsStatusText.Text = T("读取设置失败：{0}", UiText.Localize(exception.Message));
        }
    }

    private void SettingsExpander_Collapsed(object? sender, RoutedEventArgs e)
    {
        if (sender is not Expander expander || !ReferenceEquals(sender, e.Source)) return;
        if (FocusManager?.GetFocusedElement() is Control focused
            && (!focused.IsEffectivelyVisible
                || (ReferenceEquals(expander, SettingsS3Expander) && focused.GetVisualAncestors().Contains(SettingsS3ActionBar))))
            expander.GetVisualDescendants().OfType<ToggleButton>().FirstOrDefault()?.Focus();
        UpdateS3SettingsActions();
    }

    private async Task EnsureMainReaderAiSettingsLoadedAsync()
    {
        if (_mainReaderAiSettingsLoaded) return;
        if (_mainReaderAiSettingsLoadTask is { IsCompleted: false } pending)
        {
            await pending;
            return;
        }
        _mainReaderAiSettingsLoadTask = LoadMainReaderAiSettingsAsync();
        await _mainReaderAiSettingsLoadTask;
    }

    private void ConfigureS3SettingsDraftTracking()
    {
        ToggleSwitch[] switches = [S3SyncEnabledCheck, S3AutomaticSyncCheck, S3DownloadBookFilesCheck, S3PathStyleCheck, S3SkipTlsVerifyCheck];
        foreach (var control in switches) control.IsCheckedChanged += (_, _) => S3SettingsFieldChanged();
        TextBox[] fields = [S3EndpointBox, S3AccessKeyBox, S3SecretKeyBox, S3BucketBox, S3RegionBox, S3PrefixBox, S3EncryptionKeyBox,
            WebDavEndpointBox, WebDavUsernameBox, WebDavPasswordBox];
        foreach (var control in fields)
            control.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBox.TextProperty) S3SettingsFieldChanged();
            };
        NumericUpDown[] numbers = [S3SyncIntervalBox, S3TimeoutBox, S3ConcurrencyBox];
        foreach (var control in numbers) control.ValueChanged += (_, _) => S3SettingsFieldChanged();
        SyncProviderBox.SelectionChanged += (_, _) =>
        {
            UpdateSyncProviderControls();
            S3SettingsFieldChanged();
        };
        UpdateSyncProviderControls();
        UpdateS3SettingsDraftState();
    }

    private void UpdateSyncProviderControls()
    {
        var webDav = SyncProviderBox.SelectedIndex == (int)SyncProvider.WebDav;
        S3ConnectionPanel.IsVisible = !webDav;
        WebDavConnectionPanel.IsVisible = webDav;
        S3RegionRow.IsVisible = !webDav;
        S3PathStyleCheck.IsVisible = !webDav;
    }

    private void S3SettingsFieldChanged()
    {
        if (_suppressS3SettingsDraftTracking) return;
        _s3TestConnectionCancellation?.Cancel();
        _s3SettingsEditVersion++;
        S3SyncStatusText.Text = string.Empty;
        UpdateS3SettingsDraftState();
    }

    private void UpdateS3SettingsDraftState()
    {
        _s3SettingsDirty = ReadS3SyncSettingsFromControls() != S3SyncSettings.Normalize(_s3SyncStoredSettings.Settings);
        UpdateS3SettingsActions();
    }

    private void UpdateS3SettingsActions()
    {
        if (SettingsS3ActionBar is null) return;
        SettingsS3ActionBar.IsVisible = _activeSettingsCategory.Equals("Data", StringComparison.OrdinalIgnoreCase)
            && SettingsS3Expander.IsExpanded;
        S3DraftStatusText.Text = ReadS3SyncSettingsFromControls().ProviderName + " · " + (_s3SettingsSaving ? T("正在保存同步设置…")
            : _s3SettingsDirty ? T("有未保存的修改 · 保存后生效")
            : _s3SyncStoredSettings.Settings.IsConfigured ? T("配置已保存 · 立即同步使用当前保存的配置")
            : T("尚未配置 · 填写连接信息后保存"));
        S3SaveSettingsButton.IsEnabled = !_s3SyncBusy && !_s3SettingsSaving && _s3SettingsDirty;
        S3DiscardSettingsButton.IsEnabled = !_s3SyncBusy && !_s3SettingsSaving && _s3SettingsDirty;
        S3TestConnectionButton.IsEnabled = !_s3SyncBusy && !_s3SettingsSaving && _s3TestConnectionCancellation is null;
        S3SyncNowButton.IsEnabled = !_s3SyncBusy && !_s3SettingsSaving && !_s3SettingsDirty
            && _s3SyncStoredSettings.Settings.IsConfigured;
    }

    private void S3DiscardSettingsButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_s3SyncBusy || _s3SettingsSaving) return;
        PopulateS3SyncControls();
        S3ConnectionProfileStatusText.Text = string.Empty;
        S3SyncStatusText.Text = T("已放弃修改，恢复为上次保存的配置。");
    }

    private void CancelAppSettingsDebounce()
    {
        var pending = _appSettingsAutoSaveCancellation;
        _appSettingsAutoSaveCancellation = null;
        pending?.Cancel();
        // The debounce owns disposal, including when its save is already running.
    }

    private async Task DebounceAppSettingsAsync(CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(600, cancellation.Token);
            if (!cancellation.IsCancellationRequested) await SaveAppSettingsCoreAsync();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(_appSettingsAutoSaveCancellation, cancellation))
                _appSettingsAutoSaveCancellation = null;
            cancellation.Dispose();
        }
    }

    private async Task<bool> FlushAppSettingsAsync()
    {
        do
        {
            CancelAppSettingsDebounce();
            // This also waits for any save already holding the gate. Cancelling
            // the debounce never cancels an in-flight disk write.
            if (!await SaveAppSettingsCoreAsync()) return false;
        } while (_appSettingsSavedVersion < _appSettingsEditVersion);
        return true;
    }

    private async Task<bool> PrepareSettingsForExitAsync()
    {
        if (!await FlushMcpSettingsAsync()) return false;
        if (!await FlushAppSettingsAsync()) return false;
        if (_s3SettingsSaveTask is { IsCompleted: false } saving && !await saving) return false;
        UpdateS3SettingsDraftState();
        if (!_s3SettingsDirty) return true;
        OpenSettingsExpander("Data", SettingsS3Expander);
        if (!await ConfirmAsync(T("同步设置尚未保存"),
                T("同步配置中有未保存的修改。返回设置可保存；继续退出将放弃这些修改。"), T("放弃修改并退出")))
            return false;
        PopulateS3SyncControls();
        return true;
    }

    private KindleEmailSettings ReadKindleEmailDraft() => KindleEmailSettings.Normalize(new KindleEmailSettings
    {
        KindleEmailAddress = KindleEmailRecipientBox.Text ?? string.Empty,
        SenderEmailAddress = KindleEmailSenderBox.Text ?? string.Empty,
        SmtpHost = KindleEmailSmtpHostBox.Text ?? string.Empty,
        SmtpPort = int.TryParse(KindleEmailSmtpPortBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : 587,
        SmtpUsername = KindleEmailUsernameBox.Text ?? string.Empty,
        SmtpPassword = KindleEmailPasswordBox.Text ?? string.Empty,
        EnableSsl = KindleEmailSslCheck.IsChecked != false
    });

    private static bool KindleEmailSettingsEqual(KindleEmailSettings left, KindleEmailSettings right) =>
        (left.KindleEmailAddress, left.SenderEmailAddress, left.SmtpHost, left.SmtpPort, left.SmtpUsername, left.SmtpPassword, left.EnableSsl)
        == (right.KindleEmailAddress, right.SenderEmailAddress, right.SmtpHost, right.SmtpPort, right.SmtpUsername, right.SmtpPassword, right.EnableSsl);

    private void PopulateKindleEmailControls()
    {
        _suppressKindleEmailProviderChange = true;
        try
        {
            KindleEmailRecipientBox.Text = _kindleEmailSettings.KindleEmailAddress;
            KindleEmailSenderBox.Text = _kindleEmailSettings.SenderEmailAddress;
            KindleEmailSmtpHostBox.Text = _kindleEmailSettings.SmtpHost;
            KindleEmailSmtpPortBox.Text = _kindleEmailSettings.SmtpPort.ToString(CultureInfo.InvariantCulture);
            KindleEmailUsernameBox.Text = _kindleEmailSettings.SmtpUsername;
            KindleEmailPasswordBox.Text = _kindleEmailSettings.SmtpPassword;
            KindleEmailSslCheck.IsChecked = _kindleEmailSettings.EnableSsl;
            KindleEmailProviderBox.SelectedIndex = GetKindleEmailProviderIndex(_kindleEmailSettings);
        }
        finally
        {
            _suppressKindleEmailProviderChange = false;
        }

        UpdateKindleEmailProviderControls();
    }

}
