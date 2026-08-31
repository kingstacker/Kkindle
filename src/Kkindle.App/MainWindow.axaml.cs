using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kkindle.Core;
using Kkindle.Infrastructure;

namespace Kkindle;

public partial class MainWindow : Window
{
    private const string MaximizeGlyphData = "M 0.5,0.5 H 9.5 V 9.5 H 0.5 Z";
    private const string RestoreGlyphData = "M 2.5,0.5 H 9.5 V 7.5 M 0.5,2.5 H 7.5 V 9.5 H 0.5 Z";
    private const string SidebarChevronDownData = "M 1,2 L 5,6 L 9,2";
    private const string SidebarChevronRightData = "M 2,1 L 6,5 L 2,9";
    private const string LibraryGridGlyphData = "M 3,3 H 9 V 9 H 3 Z M 15,3 H 21 V 9 H 15 Z M 3,15 H 9 V 21 H 3 Z M 15,15 H 21 V 21 H 15 Z";
    private const string LibraryListGlyphData = "M 4,6 H 6 M 10,6 H 20 M 4,12 H 6 M 10,12 H 20 M 4,18 H 6 M 10,18 H 20";
    private const string LibraryCollectionsGlyphData = "M 3,7 H 9 L 11,9 H 21 V 20 H 3 Z";
    private const double LibraryDetailWidth = 320;
    private const double BookGridSlotWidth = 166;
    private const double RubberBandDragThreshold = 8;
    // Gallery mode trims the card to its cover, so the wrap-panel slot shrinks
    // with it (cover 214 + card margin 12) instead of leaving a blank strip.
    private double BookGridSlotHeight => _appSettings.GridGalleryDisplay ? 226 : 304;
    private const string InstantMenuHoverClass = "instantMenuHover";
    private const int LibraryDetailSlideDurationMs = 520;
    // Supersedes in-flight detail-pane animations whenever a newer show/hide
    // command arrives, so a close that was interrupted by a re-select can
    // never collapse a freshly re-opened pane.
    private int _detailPaneAnimationVersion;
    private double _authorPopupWidth = double.NaN;
    private DispatcherTimer? _dropOverlayHideTimer;
    private DispatcherTimer? _bookDetailClickTimer;
    private BookCardViewModel? _pendingBookDetailCard;
    private CancellationTokenSource? _detailPaneAnimationCancellation;

    private readonly AppPaths _paths;
    private readonly string _rootConfigurationDirectory;
    private readonly IBookLibraryService _library;
    private readonly IBookFormatConverter _formatConverter;
    private readonly ReaderFormatCacheService _readerFormatCache;
    private readonly DoubanMetadataService _douban;
    private readonly IKindleDeviceService? _kindle;
    private readonly DeviceModelStore _deviceModelStore;
    private readonly KindleDeviceAuxiliaryCacheStore _kindleAuxiliaryCacheStore;
    private readonly ISecretProtector _secretProtector;
    private readonly AppBackupService _backupService;
    private readonly AppSettingsStore _appSettingsStore;
    private readonly FontLibraryService _fontLibrary;
    private readonly DictionaryService _dictionaryService;
    private readonly ReaderDataService _readerData;
    private readonly EpubBookContentService _bookContent;
    private readonly EpubFootnoteResolver _footnotes;
    private readonly PdfTextService _pdfTextService;
    private readonly AiSettingsStore _aiSettingsStore;
    private readonly AiChatClient _aiChatClient;
    private readonly EpubReaderPreparationService _epubReader;
    private readonly Func<IReaderHost> _readerHostFactory;
    private readonly ZLibraryService _zLibraryService;
    private readonly ZLibrarySettingsStore _zLibrarySettingsStore;
    private readonly KindleEmailSettingsStore _kindleEmailSettingsStore;
    private readonly KindleEmailSender _kindleEmailSender;
    private readonly UpdateService? _updateService;
    private AppSettings _appSettings = new();
    private ZLibrarySettings _zLibrarySettings = new();
    private KindleEmailSettings _kindleEmailSettings = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private string? _deviceDisplayName;
    private Button? _activeNavigationSectionButton;
    private IReaderHost? _readerActiveHost;
    private IReaderHost? _readerPreloadHost;
    private bool _filterControlsReady;
    private bool _updatingFilterControls;
    private bool _updatingDetails;
    private LibraryViewMode _libraryViewMode = LibraryViewMode.Grid;
    private AnimatedWrapPanel? _bookGridPanel;
    private BookCardViewModel? _selectedCard;
    private BookCardViewModel? _multiSelectAnchor;
    private readonly HashSet<Guid> _selectedBookIds = [];
    private TaskCompletionSource<bool>? _confirmationCompletion;
    private TaskCompletionSource<string?>? _collectionNameCompletion;
    private TaskCompletionSource<bool>? _messageCompletion;
    private bool _conversionInProgress;
    private bool _automaticReaderFormatGenerationInProgress;
    private CancellationTokenSource? _conversionCancellation;
    private BookCardViewModel? _conversionCard;
    private bool _conversionMinimized;
    private FormatConversionProgress _conversionLastProgress = new(0, T("正在转换…"));
    private TaskCompletionSource<DoubanBookCandidate?>? _doubanCandidateCompletion;
    private TaskCompletionSource<DoubanUpdateChoices?>? _doubanApplyCompletion;
    private DoubanBookCandidate? _doubanSelectedCandidate;
    private DoubanBookMetadata? _doubanPreviewMetadata;
    private CancellationTokenSource? _doubanMatchCancellation;
    private bool _doubanResearchPending;
    private TaskCompletionSource<IReadOnlyDictionary<string, IReadOnlyCollection<string>>?>? _importFormatSelectionCompletion;
    private readonly List<(string FilePath, ToggleSwitch Toggle)> _importFormatSelectionRows = [];
    private bool _rubberBandSelecting;
    private bool _rubberBandPointerSequenceHandled;
    private Point _rubberBandStart;
    private Point _rubberBandCurrent;
    private bool _rubberBandPressedOnCard;
    private bool _rubberBandGestureActive;

    public MainWindow()
        : this(CreateDefaultDependencies())
    {
    }

    private MainWindow((AppPaths Paths, IBookLibraryService Library, IBookFormatConverter FormatConverter, DoubanMetadataService Douban) dependencies)
        : this(dependencies.Paths, dependencies.Library, dependencies.FormatConverter, dependencies.Douban)
    {
    }

    public MainWindow(
        AppPaths paths,
        IBookLibraryService library,
        IBookFormatConverter? formatConverter = null,
        DoubanMetadataService? douban = null,
        AppServices? services = null,
        AppSettings? startupSettings = null)
    {
        _paths = paths;
        _rootConfigurationDirectory = services?.RootConfigurationDirectory ?? AppContext.BaseDirectory;
        _library = library;
        _appSettings = AppSettings.Normalize(startupSettings);
        _formatConverter = formatConverter ?? new BookFormatConversionService();
        _readerFormatCache = new ReaderFormatCacheService(paths, _formatConverter);
        _douban = douban ?? new DoubanMetadataService();
        _kindle = services?.KindleDeviceService;
        _deviceModelStore = new DeviceModelStore(paths);
        _kindleAuxiliaryCacheStore = new KindleDeviceAuxiliaryCacheStore(paths);
        _secretProtector = services?.SecretProtector ?? new PlaintextSecretProtector();
        _backupService = new AppBackupService(paths, _secretProtector);
        _appSettingsStore = new AppSettingsStore(paths);
        _fontLibrary = new FontLibraryService(paths);
        _dictionaryService = new DictionaryService(paths, _formatConverter);
        _readerData = new ReaderDataService(paths);
        _bookContent = new EpubBookContentService(_readerData);
        _footnotes = new EpubFootnoteResolver();
        _pdfTextService = new PdfTextService();
        _aiSettingsStore = new AiSettingsStore(paths, _secretProtector);
        _aiChatClient = new AiChatClient();
        _epubReader = new EpubReaderPreparationService(paths);
        _readerHostFactory = services?.ReaderHostFactory ?? (() => new NativeWebViewReaderHost());
        _zLibraryService = new ZLibraryService();
        _zLibrarySettingsStore = new ZLibrarySettingsStore(paths, _secretProtector);
        _kindleEmailSettingsStore = new KindleEmailSettingsStore(paths, _secretProtector);
        _kindleEmailSender = new KindleEmailSender();
        _updateService = services?.UpdateInstaller is { } updateInstaller
            ? new UpdateService(updateInstaller)
            : null;
        ViewModel = new LibraryViewModel(library, paths.Data);

        InitializeComponent();
        if (startupSettings is not null && !_appSettings.OnboardingCompleted)
        {
            // Choose the first-run surface before the window is presented. The
            // async library/database initialization can continue behind it.
            LibraryRoot.IsVisible = false;
            ShowOnboardingIfNeeded();
        }
        UiText.LanguageChanged += MainWindowLanguageChanged;
        ApplyApplicationIcon();
        InitializeTrayIcon();
        // Linux file managers can mark external drag events handled on the
        // first child under the pointer. Observe the bubbled event even then,
        // at the top-level window that owns the native XDND target.
        AddHandler(
            DragDrop.DragEnterEvent,
            LibraryPane_DragOver,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        AddHandler(
            DragDrop.DragOverEvent,
            LibraryPane_DragOver,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        AddHandler(
            DragDrop.DragLeaveEvent,
            LibraryPane_DragLeave,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        AddHandler(
            DragDrop.DropEvent,
            LibraryPane_Drop,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        // The device resource page contains a ListBox/ScrollViewer which may
        // handle drag events before they reach the page. Register here with
        // handledEventsToo so a resource dropped on any part of that page still
        // reaches the Kindle transfer path. The XAML attached handlers were
        // removed to avoid running the Drop handler twice.
        DeviceResourcePage.AddHandler(
            DragDrop.DragEnterEvent,
            DeviceResourcePage_DragOver,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        DeviceResourcePage.AddHandler(
            DragDrop.DragOverEvent,
            DeviceResourcePage_DragOver,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        DeviceResourcePage.AddHandler(
            DragDrop.DropEvent,
            DeviceResourcePage_Drop,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        // The local dictionary list contains interactive children (ListBox and
        // TextBox), so listen after handled events to make the whole section a
        // drop target instead of only its unused whitespace.
        DictionaryManagementSection.AddHandler(
            DragDrop.DragEnterEvent,
            DictionaryManagementSection_DragOver,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        DictionaryManagementSection.AddHandler(
            DragDrop.DragOverEvent,
            DictionaryManagementSection_DragOver,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        DictionaryManagementSection.AddHandler(
            DragDrop.DropEvent,
            DictionaryManagementSection_Drop,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        // SelectableTextBlock handles PointerPressed while beginning a text
        // selection. Listen after handled events so the reader can freeze its
        // scroll offset before Avalonia brings the selection into view.
        ReaderLinuxTextFallbackOverlay.AddHandler(
            InputElement.PointerPressedEvent,
            ReaderLinuxTextFallback_PointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        ReaderLinuxTextFallbackOverlay.AddHandler(
            InputElement.PointerReleasedEvent,
            ReaderLinuxTextFallback_PointerReleased,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        ReaderRoot.AddHandler(
            InputElement.PointerPressedEvent,
            ReaderRoot_SelectionDismissPointerPressed,
            RoutingStrategies.Tunnel,
            handledEventsToo: true);
        // The vertical fallback page manages its own drag selection and
        // footnote taps; surface them through the shared reader popups.
        ReaderLinuxTextFallbackPageVertical.SelectionCommitted += (_, _) =>
            SyncLinuxReaderTextFallbackSelectionState();
        ReaderLinuxTextFallbackPageVertical.FootnoteActivated += (_, payload) =>
        {
            _readerLinuxVerticalFootnoteHandledRelease = true;
            _ = ObserveReaderTaskAsync(HandleReaderFootnoteHoverAsync(
                payload.Href,
                isFootnote: true,
                ReaderLinuxTextFallbackPageVertical.TranslatePoint(
                    payload.Position,
                    ReaderWebViewHost)));
        };
        ReaderLinuxTextFallbackPageVertical.FootnoteDismissed += (_, _) =>
            HideReaderFootnotePopup();
        BookGrid.ItemsPanel = new FuncTemplate<Panel?>(() =>
        {
            _bookGridPanel = new AnimatedWrapPanel
            {
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                ItemWidth = BookGridSlotWidth,
                ItemHeight = BookGridSlotHeight
            };
            return _bookGridPanel;
        });
        BookGrid.SizeChanged += (_, _) => UpdateBookGridLayout();
        BookGrid.LayoutUpdated += (_, _) => UpdateBookGridLayout();
        LibraryContentHost.SizeChanged += (_, _) => UpdateBookGridLayout();
        LibraryWorkspace.SizeChanged += (_, _) => UpdateBookGridLayout();
        SizeChanged += (_, _) => UpdateBookGridLayout();
        LibraryRoot.AddHandler(
            InputElement.PointerPressedEvent,
            LibraryRoot_PointerPressed,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        // ListBoxItem and its ScrollViewer handle pointer movement/release while
        // dragging. Listen after handled events as well, otherwise a drag that
        // crosses cards can lose the rubber-band update in one direction.
        BookGrid.AddHandler(
            InputElement.PointerPressedEvent,
            BookGrid_PointerPressed,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        BookGrid.AddHandler(
            InputElement.PointerMovedEvent,
            BookGrid_PointerMoved,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        BookGrid.AddHandler(
            InputElement.PointerReleasedEvent,
            BookGrid_PointerReleased,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        BookGrid.AddHandler(
            InputElement.PointerCaptureLostEvent,
            BookGrid_PointerCaptureLost,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        DeviceBookGridScroll.AddHandler(
            InputElement.PointerMovedEvent,
            DeviceBookGrid_PointerMoved,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        DeviceBookGridScroll.AddHandler(
            InputElement.PointerReleasedEvent,
            DeviceBookGrid_PointerReleased,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        DeviceBookGridScroll.AddHandler(
            InputElement.PointerCaptureLostEvent,
            DeviceBookGrid_PointerCaptureLost,
            RoutingStrategies.Bubble,
            handledEventsToo: true);
        DataContext = this;
        Closed += MainWindow_Closed;
        Closing += MainWindow_Closing;
        UpdateMaximizeGlyph();
        UpdateWindowShadowMargin();
        SetSidebarActive(AllBooksButton);
        SetLibraryViewMode(LibraryViewMode.Grid);
        UpdateLibraryUi();
        ConfigureStage3Timer();
        SetEjectButtonsEnabled(false);
        Opened += (_, _) => EnsureInteractiveControlToolTips();
        // The author dropdown must keep one constant popup width; assign it on
        // every open because ApplyTemplate can rebuild the PART_Popup instance.
        AuthorFilterBox.DropDownOpened += (_, _) =>
        {
            if (AuthorFilterBox.GetVisualDescendants().OfType<Popup>().FirstOrDefault() is { } popup)
                popup.Width = _authorPopupWidth;
        };
        AuthorFilterBox.TemplateApplied += (_, e) =>
        {
            if (e.NameScope.Find("PART_Popup") is Popup popup)
                popup.Width = _authorPopupWidth;
        };
        Dispatcher.UIThread.Post(UpdateBookGridLayout, DispatcherPriority.Loaded);
        AttachBookGridAutoHideScrollbar();
    }

    private void ApplyApplicationIcon()
    {
        try
        {
            using var iconStream = AssetLoader.Open(
                new Uri("avares://Kkindle.App/Assets/Icons/kkindle.png"));
            Icon = new WindowIcon(iconStream);
        }
        catch
        {
            // A missing icon must not prevent the app from starting.
        }
    }

    // Marks the book grid's ScrollViewer with the bookScroll/.scrolling classes
    // the App.axaml auto-hide styles key on. Re-runs on TemplateApplied because
    // the ListBox rebuilds its template ScrollViewer.
    private void AttachBookGridAutoHideScrollbar()
    {
        if (BookGrid.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault() is not { } viewer)
        {
            BookGrid.TemplateApplied += (_, _) => AttachBookGridAutoHideScrollbar();
            return;
        }
        if (viewer.Classes.Contains("bookScroll")) return;
        viewer.Classes.Add("bookScroll");
        viewer.Classes.Add("scrolling");
        var idleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        idleTimer.Tick += (_, _) =>
        {
            idleTimer.Stop();
            viewer.Classes.Remove("scrolling");
        };
        viewer.ScrollChanged += (_, _) =>
        {
            viewer.Classes.Add("scrolling");
            idleTimer.Stop();
            idleTimer.Start();
        };
    }

    public LibraryViewModel ViewModel { get; }

    private static string T(string source, params object?[] args) => UiText.Get(source, args);

    private void MainWindowLanguageChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => MainWindowLanguageChanged(sender, e));
            return;
        }

        RefreshInteractiveControlToolTips();
        RefreshOnboardingLocalizedChoices();
        if (_filterControlsReady)
            RefreshLocalizedFilterItems();
        RefreshLocalizedZLibraryFilterItems();
        RefreshLocalizedReadingMaterialsSourceFilter();
        ViewModel.RefreshView();
        UpdateLibraryUi();
        if (_selectedCard is not null)
            UpdateDetailActionIcons(_selectedCard.Book.IsFavorite, _selectedCard.Book.ReadingStatus);
        UpdateDeviceBookSelectionUi();
        UpdateDeviceBookEmptyState();
        UpdateReaderAiLanguageText();
        ApplyReaderAiSettingsToControls();
        UpdateCalibreDetectionStatus();
        UpdateZLibraryAccountStatus();
        if (!string.IsNullOrWhiteSpace(_pendingUpdateVersion))
        {
            var packageReady = TryGetPendingUpdatePackage(out _, out _);
            ShowUpdateBadge(
                _pendingUpdateVersion,
                _appSettings.PendingUpdateReleaseNotes,
                packageReady);
            AboutUpdateStatusText.Text = packageReady
                ? T("更新包已下载，退出应用后安装 {0}", _pendingUpdateVersion)
                : T("发现新版本 {0}", _pendingUpdateVersion);
        }
        if (ReaderRoot.IsVisible)
        {
            UpdateReaderToolbar();
            UpdateReaderZenTocToggle();
            UpdateReaderLayoutSliderLabels();
            UpdateReaderLayoutStatus();
            UpdateReaderStatsDisplay();
        }
        if (_stage3Ready && ReadingMaterialsPage.IsVisible && !_readingMaterialsDirty)
            ApplyReadingMaterialsFilter();
    }

    private void RefreshLocalizedFilterItems()
    {
        var readingStatusIndex = ReadingStatusFilterBox.SelectedIndex;
        var sortIndex = LibrarySortBox.SelectedIndex;
        _updatingFilterControls = true;
        try
        {
            ReadingStatusFilterBox.ItemsSource = new[] { T("待读"), T("阅读中"), T("已读") };
            LibrarySortBox.ItemsSource = new[] { T("最近更新"), T("标题升序"), T("作者升序"), T("创建时间"), T("进度优先") };
            ReadingStatusFilterBox.SelectedIndex = readingStatusIndex;
            LibrarySortBox.SelectedIndex = sortIndex;
        }
        finally
        {
            _updatingFilterControls = false;
        }
    }

    private void RefreshLocalizedZLibraryFilterItems()
    {
        var extensionIndex = ZLibraryExtensionBox.SelectedIndex;
        var languageIndex = ZLibraryLanguageBox.SelectedIndex;
        SetComboBoxItemContent(ZLibraryExtensionBox, 0, T("全部格式"));

        var languageLabels = new[]
        {
            "全部语言",
            "中文",
            "英文",
            "日文",
            "韩文",
            "俄文",
            "德文",
            "法文",
            "西班牙文"
        };

        for (var index = 0; index < languageLabels.Length; index++)
            SetComboBoxItemContent(ZLibraryLanguageBox, index, T(languageLabels[index]));

        RestoreComboBoxSelection(ZLibraryExtensionBox, extensionIndex);
        RestoreComboBoxSelection(ZLibraryLanguageBox, languageIndex);
    }

    private void RefreshLocalizedReadingMaterialsSourceFilter()
    {
        var selectedIndex = ReadingMaterialsSourceBox.SelectedIndex;
        SetComboBoxItemContent(ReadingMaterialsSourceBox, 0, T("全部来源"));
        SetComboBoxItemContent(ReadingMaterialsSourceBox, 1, T("本地书库"));
        SetComboBoxItemContent(ReadingMaterialsSourceBox, 2, "Kindle");
        RestoreComboBoxSelection(ReadingMaterialsSourceBox, selectedIndex);
    }

    private static void SetComboBoxItemContent(ComboBox comboBox, int index, string content)
    {
        if (index >= 0 && index < comboBox.Items.Count && comboBox.Items[index] is ComboBoxItem item)
            item.Content = content;
    }

    private static void RestoreComboBoxSelection(ComboBox comboBox, int selectedIndex)
    {
        if (selectedIndex < 0 || selectedIndex >= comboBox.Items.Count)
            return;

        comboBox.SelectedIndex = -1;
        comboBox.SelectedIndex = selectedIndex;
    }

    public ObservableCollection<BookCollectionFolderViewModel> CollectionFolders { get; } = [];
    public ObservableCollection<DoubanCandidateViewModel> DoubanCandidates { get; } = [];

    private enum LibraryViewMode
    {
        Grid,
        List,
        Collections
    }

    private async Task RunSendDiagnosticAsync()
    {
        try
        {
            await Task.Delay(1500);
            foreach (var card in ViewModel.Books)
            {
                try
                {
                    using var prepared = await PrepareKindleTransferAsync(card.Book, null, CancellationToken.None);
                    Console.WriteLine($"[senddiag] prepare ok 《{card.Title}》 ({prepared.File.Format})");
                }
                catch (Exception exception)
                {
                    Console.WriteLine($"[senddiag] PREPARE FAILED 《{card.Title}》: {exception.Message}");
                    LogSendDiagnostic($"Prepare 《{card.Title}》", exception);
                }
            }
            Console.WriteLine("[senddiag] prepare sweep done");
        }
        catch (Exception exception)
        {
            Console.WriteLine($"[senddiag] outer failure: {exception}");
        }
    }

    public async Task InitializeLibraryAsync()
    {
        try
        {
            SetTaskStatus(T("正在准备本地书库…"));
            await _library.InitializeAsync(_lifetimeCancellation.Token);
            await InitializeStage3Async(_lifetimeCancellation.Token);
            await ViewModel.RefreshAsync(_lifetimeCancellation.Token);
            SetupFilterControls();
            await RefreshCollectionsAsync();
            _filterControlsReady = true;
            UpdateLibraryUi();
            SetTaskStatus(ViewModel.StatusText);
            ShowOnboardingIfNeeded();
            StartAutomaticUpdateCheck();
            if (Environment.GetEnvironmentVariable("KKINDLE_SEND_DIAG") == "1" && ViewModel.Books.Count > 0)
            {
                _selectedCard = ViewModel.Books[0];
                _ = RunSendDiagnosticAsync();
            }
#if DEBUG
            if (Environment.GetEnvironmentVariable("KKINDLE_KREADER_VALIDATE") == "1")
            {
                _ = Dispatcher.UIThread.InvokeAsync(RunKreaderValidationAndExitAsync);
            }
            if (Environment.GetEnvironmentVariable("KKINDLE_ANIMATION_PROBE") == "1")
            {
                _ = Dispatcher.UIThread.InvokeAsync(RunKreaderAnimationProbeAndExitAsync);
            }
#endif
            if (int.TryParse(Environment.GetEnvironmentVariable("KKINDLE_OPEN_BOOK_INDEX"), out var openIndex)
                && openIndex >= 0 && openIndex < ViewModel.Books.Count)
            {
                _ = Dispatcher.UIThread.InvokeAsync(async () =>
                {
                    await OpenBookAsync(ViewModel.Books[openIndex], restoreProgress: false);
                    if (int.TryParse(Environment.GetEnvironmentVariable("KKINDLE_OPEN_CHAPTER_STEPS"), out var steps))
                    {
                        for (var i = 0; i < steps; i++)
                        {
                            await Task.Delay(2500);
                            await MoveReaderChapterAsync(1);
                        }
                    }
                    await Task.Delay(2000);
                    try
                    {
                        var dom = CurrentReaderHost is null
                            ? "<no host>"
                            : await CurrentReaderHost.InvokeScriptAsync(
                                "(() => JSON.stringify({t:(document.body?.innerText||'').slice(0,120), imgs:document.images.length, imgOk:[...document.images].map(i=>i.naturalWidth).join(','), src:[...document.images].map(i=>i.currentSrc||i.src).join('|').slice(0,300)}))();");
                        System.IO.File.WriteAllText("/tmp/reader-state.txt",
                            $"chapterIndex={_readerChapterIndex}\nstatus={ReaderStatusText.Text}\nsource={CurrentReaderHost?.Source}\ndom={dom}\n");
                    }
                    catch (Exception ex)
                    {
                        System.IO.File.WriteAllText("/tmp/reader-state.txt", "probe failed: " + ex);
                    }
                });
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("无法读取本地书库：{0}", UiText.Localize(exception.Message)));
            EmptyLibraryTitleText.Text = T("本地书库暂时不可用");
            EmptyLibraryMessageText.Text = T("请检查数据目录后重启 Kkindle。");
            EmptyLibraryState.IsVisible = true;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            if (change.NewValue is WindowState newState
                && newState == WindowState.Minimized)
            {
                // Minimize parks the single instance in the tray instead of
                // leaving a taskbar button; the tray click brings it back.
                Hide();
            }
            if (MaximizeWindowGlyph is not null
                && MaximizeWindowButton is not null)
            {
                UpdateMaximizeGlyph();
                UpdateWindowShadowMargin();
            }
        }
    }

    // Keep content and its frame flush with the client area in every state.
    // Transparent gutters are rendered as opaque white on some Windows/DPI
    // combinations; resize hit targets are overlays and need no reserved space.
    private void UpdateWindowShadowMargin()
    {
        if (WindowShadowHost is null) return;
        var maximized = WindowState is WindowState.Maximized or WindowState.FullScreen;
        WindowShadowHost.Margin = new Thickness(0);
        if (WindowFrameOverlay is not null)
            WindowFrameOverlay.Margin = new Thickness(0);
        if (WindowResizeLayer is not null)
            WindowResizeLayer.IsVisible = !maximized;
    }

    // Let the platform own the resize gesture. Updating Width, Height and
    // Position from PointerMoved produced several native window updates for a
    // single mouse sample; transparent windows then flashed while Avalonia
    // laid out the surface between those updates.
    private void WindowResize_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control { Tag: string edge }
            || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (!TryGetWindowEdge(edge, out var windowEdge))
            return;

        BeginResizeDrag(windowEdge, e);
        e.Handled = true;
    }

    private static bool TryGetWindowEdge(string edge, out WindowEdge windowEdge)
    {
        windowEdge = edge switch
        {
            "Left" => WindowEdge.West,
            "Right" => WindowEdge.East,
            "Top" => WindowEdge.North,
            "Bottom" => WindowEdge.South,
            "TopLeft" => WindowEdge.NorthWest,
            "TopRight" => WindowEdge.NorthEast,
            "BottomLeft" => WindowEdge.SouthWest,
            "BottomRight" => WindowEdge.SouthEast,
            _ => default
        };
        return edge is "Left" or "Right" or "Top" or "Bottom"
            or "TopLeft" or "TopRight" or "BottomLeft" or "BottomRight";
    }

    private static (AppPaths Paths, IBookLibraryService Library, IBookFormatConverter FormatConverter, DoubanMetadataService Douban) CreateDefaultDependencies()
    {
        var paths = new AppPaths(AppRootConfiguration.ResolveRoot(AppContext.BaseDirectory));
        return (
            paths,
            new SqliteBookLibraryService(paths, new BookMetadataService()),
            new BookFormatConversionService(),
            new DoubanMetadataService());
    }

    private sealed class PlaintextSecretProtector : ISecretProtector
    {
        public byte[] Protect(byte[] value) => value.ToArray();
        public byte[] Unprotect(byte[] value) => value.ToArray();
    }

    // Closing the window while Kreader is open returns to the library (the
    // reader's X / 返回书架 default) instead of exiting the whole application.
    // A second close request on the library then exits for real. Alt+F4 and
    // platform close requests land here too (the window draws its own title
    // bar, so this is the only path besides CloseWindowButton_Click).
    private void MainWindow_Closing(object? sender, WindowClosingEventArgs e)
    {
        if (_allowWindowCloseForPendingUpdate) return;
        if (ReaderRoot.IsVisible)
        {
            e.Cancel = true;
            _ = CloseReaderAsync();
            return;
        }
        if (TryGetPendingUpdatePackage(out var packagePath, out var version))
        {
            e.Cancel = true;
            // The download-complete notice is an informational overlay. If
            // the user closes from it, dismiss it first so the install
            // confirmation is visible instead of being stacked underneath.
            if (MessageOverlay.IsVisible)
                CompleteMessage();
            _ = PromptPendingUpdateInstallAsync(packagePath, version);
        }
    }

    private async void MainWindow_Closed(object? sender, EventArgs e)
    {
        UiText.LanguageChanged -= MainWindowLanguageChanged;
        UiText.LanguageChanged -= TrayLanguageChanged;
        _stage3Timer.Stop();
        _transferToastTimer.Stop();
        _deviceStatusToastTimer.Stop();
        _appSettingsAutoSaveCancellation?.Cancel();
        _appSettingsAutoSaveCancellation?.Dispose();
        _appSettingsAutoSaveCancellation = null;
        _calibreDetectionCancellation?.Cancel();
        _calibreDetectionCancellation?.Dispose();
        _calibreDetectionCancellation = null;
        _conversionCancellation?.Cancel();
        _transferCancellation?.Cancel();
        _isTransferring = false;
        _doubanCandidateCompletion?.TrySetResult(null);
        _doubanCandidateCompletion = null;
        _doubanApplyCompletion?.TrySetResult(null);
        _doubanApplyCompletion = null;
        _doubanMatchCancellation?.Cancel();
        _doubanMatchCancellation?.Dispose();
        _doubanMatchCancellation = null;
        _messageCompletion?.TrySetResult(true);
        _messageCompletion = null;
        _importFormatSelectionCompletion?.TrySetResult(null);
        _importFormatSelectionCompletion = null;
        if (_readerDocument is not null || _readerIsPdf)
            await CloseReaderAsync();
        _readerNavigationCancellation?.Cancel();
        _readerSessionCancellation?.Cancel();
        _zLibrarySearchCancellation?.Cancel();
        _zLibrarySearchCancellation?.Dispose();
        _zLibrarySearchCancellation = null;
        _lifetimeCancellation.Cancel();
        _lifetimeCancellation.Dispose();
        _douban.Dispose();
        _doubanBatchService?.Dispose();
        _zLibraryService.Dispose();
        _aiChatClient.Dispose();
        _updateService?.Dispose();
        _readerNavigationCancellation?.Dispose();
        _readerSessionCancellation?.Dispose();
        _readerActiveHost?.Dispose();
        _readerPreloadHost?.Dispose();
        foreach (var folder in CollectionFolders) folder.Dispose();
        foreach (var item in DoubanCandidates) item.Dispose();
        foreach (var item in ZLibraryBooks) item.Dispose();
        foreach (var item in _allStage3ReadingMaterials) item.Dispose();
        ViewModel.Dispose();
    }

    private void SetupFilterControls()
    {
        _updatingFilterControls = true;
        try
        {
            // The empty selection is represented by each ComboBox's placeholder;
            // keep the "全部..." labels out of the popup item list.
            var authors = ViewModel.AvailableAuthors.ToArray();
            AuthorFilterBox.ItemsSource = authors;
            // The popup resizes itself to the currently realized items, so its
            // width follows whatever author names are on screen while scrolling.
            // Pin the popup to one fixed width, sized by the longest author name.
            _authorPopupWidth = Math.Ceiling(MeasureWidestFilterText(AuthorFilterBox, authors)) + 6;
            TagFilterBox.ItemsSource = ViewModel.AvailableTags.ToArray();
            FormatFilterBox.ItemsSource = ViewModel.AvailableFormats.ToArray();
            CategoryFilterBox.ItemsSource = ViewModel.AvailableCategories.ToArray();
            ReadingStatusFilterBox.ItemsSource = new[] { T("待读"), T("阅读中"), T("已读") };
            LibrarySortBox.ItemsSource = new[] { T("最近更新"), T("标题升序"), T("作者升序"), T("创建时间"), T("进度优先") };

            AuthorFilterBox.SelectedIndex = -1;
            TagFilterBox.SelectedIndex = -1;
            FormatFilterBox.SelectedIndex = -1;
            CategoryFilterBox.SelectedIndex = -1;
            ReadingStatusFilterBox.SelectedIndex = -1;
            LibrarySortBox.SelectedIndex = (int)ViewModel.SortMode;
            FavoritesOnlyCheckBox.IsChecked = ViewModel.FavoritesOnly;
        }
        finally
        {
            _updatingFilterControls = false;
        }
    }

    private static double MeasureWidestFilterText(ComboBox comboBox, IEnumerable<string> labels)
    {
        var probe = new TextBlock
        {
            FontFamily = comboBox.FontFamily,
            FontSize = comboBox.FontSize,
            FontStretch = comboBox.FontStretch,
            FontStyle = comboBox.FontStyle,
            FontWeight = comboBox.FontWeight
        };
        var widestText = 0d;
        foreach (var label in labels)
        {
            probe.Text = label;
            probe.Measure(Size.Infinity);
            widestText = Math.Max(widestText, probe.DesiredSize.Width);
        }

        // ComboBoxItemThemePadding is 7px per side and the popup reserves a
        // 12px scrollbar lane, so both belong in the fixed item width.
        return widestText + 14 + 12;
    }

    private void UpdateLibraryUi()
    {
        LibraryBusyProgress.IsVisible = ViewModel.IsBusy;
        LibrarySummaryText.Text = ViewModel.StatusText;
        TaskStatusText.Text = ViewModel.StatusText;
        SidebarCountText.Text = ViewModel.Books.Count.ToString();
        foreach (var card in ViewModel.Books)
        {
            card.SetGalleryTextVisible(!_appSettings.GridGalleryDisplay);
            card.SetLibraryPresenceVisible(_appSettings.CompareKindleLibraryEnabled);
        }
        foreach (var card in DeviceBooks)
        {
            card.SetGalleryTextVisible(!_appSettings.GridGalleryDisplay);
            card.SetLibraryPresenceVisible(_appSettings.CompareKindleLibraryEnabled);
        }
        RefreshLibraryPresenceState();
        SyncCardSelectionVisuals();

        var showingBooks = _libraryViewMode is LibraryViewMode.Grid or LibraryViewMode.List;
        var hasBooks = ViewModel.Books.Count > 0;
        var showingCollections = _libraryViewMode == LibraryViewMode.Collections;
        var collectionsEmpty = CollectionFolders.Count == 0;
        EmptyLibraryState.IsVisible = !ViewModel.IsBusy
            && ((showingBooks && !hasBooks) || (showingCollections && collectionsEmpty));

        if (showingCollections && collectionsEmpty)
        {
            EmptyLibraryTitleText.Text = T("还没有收藏夹");
            EmptyLibraryMessageText.Text = T("点击“新建收藏夹”创建，之后可在书籍右键菜单中把书籍加入其中。");
        }
        else if (ViewModel.LibraryBooks.Count == 0)
        {
            EmptyLibraryTitleText.Text = T("电脑书库还是空的");
            EmptyLibraryMessageText.Text = T("导入 EPUB、PDF、MOBI 或 AZW3 文件开始阅读。");
        }
        else
        {
            EmptyLibraryTitleText.Text = T("没有符合条件的书籍");
            EmptyLibraryMessageText.Text = T("试试清除筛选条件，或换一个搜索词。");
        }

        CollectionHeader.IsVisible = _libraryViewMode is LibraryViewMode.Grid or LibraryViewMode.List
            && ViewModel.CollectionFilterId is not null;
        ActiveCollectionTitleText.Text = UiText.Localize(ViewModel.CollectionFilterName ?? string.Empty);
        CreateCollectionButton.IsVisible = _libraryViewMode == LibraryViewMode.Collections;

        if (_selectedCard is not null)
        {
            var refreshedCard = ViewModel.Books.FirstOrDefault(card => card.Book.Id == _selectedCard.Book.Id);
            if (refreshedCard is not null)
            {
                // A library refresh replaces every card instance. Preserve the
                // selection without turning that data refresh into a request to
                // open the detail pane (for example after format conversion).
                _selectedCard = refreshedCard;
                if (LibraryDetailPane.IsVisible)
                    SelectBook(refreshedCard);
            }
            else if (!ViewModel.LibraryBooks.Any(book => book.Id == _selectedCard.Book.Id))
                ClearSelectedBook();
        }
    }

    private void SetTaskStatus(string message)
    {
        TaskStatusText.Text = UiText.Localize(message);
        LibrarySummaryText.Text = ViewModel.StatusText;
    }

    private void SetLibraryViewMode(LibraryViewMode mode)
    {
        _libraryViewMode = mode;
        BookGrid.IsVisible = mode == LibraryViewMode.Grid;
        BookList.IsVisible = mode == LibraryViewMode.List;
        CollectionScroll.IsVisible = mode == LibraryViewMode.Collections;
        CollectionHeader.IsVisible = mode is LibraryViewMode.Grid or LibraryViewMode.List
            && ViewModel.CollectionFilterId is not null;
        CreateCollectionButton.IsVisible = mode == LibraryViewMode.Collections;
        LibraryViewToggleIcon.Data = Geometry.Parse(mode switch
        {
            LibraryViewMode.List => LibraryListGlyphData,
            LibraryViewMode.Collections => LibraryCollectionsGlyphData,
            _ => LibraryGridGlyphData
        });
        ToolTip.SetTip(LibraryViewToggleButton, T("当前：{0}，点击切换到{1}", DescribeLibraryViewMode(mode), DescribeLibraryViewMode(NextLibraryViewMode(mode))));
        UpdateLibraryUi();
    }

    // The view button cycles through the modes in display order.
    private static LibraryViewMode NextLibraryViewMode(LibraryViewMode mode) => mode switch
    {
        LibraryViewMode.Grid => LibraryViewMode.List,
        LibraryViewMode.List => LibraryViewMode.Collections,
        _ => LibraryViewMode.Grid
    };

    private static string DescribeLibraryViewMode(LibraryViewMode mode) => mode switch
    {
        LibraryViewMode.Grid => T("网格视图"),
        LibraryViewMode.List => T("列表视图"),
        _ => T("收藏夹视图")
    };

    private void SelectBook(BookCardViewModel card)
    {
        _selectedCard = card;
        DetailCoverImage.Source = card.CoverImage;
        DetailCoverPlaceholder.IsVisible = card.CoverImage is null;
        DetailTitleText.Text = card.Title;
        DetailAuthorsText.Text = card.Authors;
        DetailDoubanRatingBox.Text = card.Book.DoubanRating is null
            ? string.Empty
            : T("{0:0.0}（{1} 人评价）", card.Book.DoubanRating, card.Book.DoubanRatingCount ?? 0);
        DetailStateText.Text = card.ReadingStateLabel;
        DetailOrganizationText.Text = card.OrganizationLabel;
        DetailPublicationText.Text = card.PublicationLabel;
        DetailIdentifierText.Text = card.IdentifierLabel;
        DetailDescriptionText.Text = card.DescriptionLabel;
        DetailTagsBox.Text = card.Book.Tags;
        DetailCategoryBox.Text = card.Book.Category;
        DetailDescriptionBox.Text = card.Book.Description ?? string.Empty;
        DetailSeriesBox.Text = card.Book.Series ?? string.Empty;
        DetailPublisherBox.Text = card.Book.Publisher ?? string.Empty;
        DetailPublishDateBox.Text = card.Book.PublishDate ?? string.Empty;
        DetailIsbnBox.Text = card.Book.Isbn ?? string.Empty;
        DetailPageCountBox.Text = card.Book.PageCount ?? string.Empty;
        DetailBindingBox.Text = card.Book.Binding ?? string.Empty;
        UpdateDetailActionIcons(card.Book.IsFavorite, card.Book.ReadingStatus);
        DetailFiles.ItemsSource = card.Book.Files;

        _updatingDetails = true;
        try
        {
            DetailCollectionBox.ItemsSource = CollectionFolders;
            DetailCollectionBox.SelectedItem = CollectionFolders.FirstOrDefault(folder =>
                card.Book.CollectionIds.Contains(folder.Collection.Id));
            CollectionMembershipButton.Content = DetailCollectionBox.SelectedItem is null ? T("加入收藏夹") : T("移出收藏夹");
        }
        finally
        {
            _updatingDetails = false;
        }

        ShowLibraryDetailPane();
    }

    // The pane uses one render-only translation. Its content is populated
    // before this method runs, avoiding layout and image updates mid-slide.
    private void ShowLibraryDetailPane()
    {
        var wasVisible = LibraryDetailPane.IsVisible;
        var token = BeginDetailPaneAnimation();
        var version = ++_detailPaneAnimationVersion;
        if (!wasVisible)
        {
            var translate = new TranslateTransform(LibraryDetailWidth, 0);
            LibraryDetailPane.RenderTransform = translate;
            LibraryDetailPane.Opacity = 1;
            LibraryDetailPane.IsVisible = true;
            _ = AnimateLibraryDetailPaneInCoreAsync(version, token, translate, LibraryDetailWidth);
        }
        else
        {
            var currentX = (LibraryDetailPane.RenderTransform as TranslateTransform)?.X ?? 0;
            LibraryDetailPane.IsVisible = true;
            LibraryDetailPane.Opacity = 1;
            if (Math.Abs(currentX) > 0.5)
            {
                var translate = new TranslateTransform(currentX, 0);
                LibraryDetailPane.RenderTransform = translate;
                _ = AnimateLibraryDetailPaneInCoreAsync(version, token, translate, currentX);
            }
            else
            {
                LibraryDetailPane.RenderTransform = new TranslateTransform(0, 0);
            }
        }
    }

    private async Task AnimateLibraryDetailPaneInCoreAsync(
        int version,
        CancellationToken token,
        TranslateTransform translate,
        double fromX)
    {
        try
        {
            // Give the newly visible pane one frame to finish measuring before
            // movement starts, so layout work cannot interrupt the first step.
            await Task.Delay(16, token);
            await RunLibraryDetailPaneDoubleAnimationAsync(
                translate,
                TranslateTransform.XProperty,
                fromX,
                0,
                LibraryDetailSlideDurationMs,
                new CubicEaseInOut(),
                token);
        }
        catch
        {
        }
        if (version != _detailPaneAnimationVersion) return;
        LibraryDetailPane.RenderTransform = new TranslateTransform(0, 0);
        LibraryDetailPane.Opacity = 1;
    }

    private void ClearSelectedBook()
    {
        CancelPendingBookDetailClick();
        _selectedCard = null;
        var token = BeginDetailPaneAnimation();
        var version = ++_detailPaneAnimationVersion;
        if (!LibraryDetailPane.IsVisible)
        {
            CompleteClearSelectedBook();
            return;
        }
        var width = LibraryDetailPane.Bounds.Width > 0 ? LibraryDetailPane.Bounds.Width : LibraryDetailWidth;
        if (width <= 0)
        {
            CompleteClearSelectedBook();
            return;
        }
        _ = AnimateLibraryDetailPaneOutCoreAsync(version, token, width);
    }

    // Exit mirrors the entrance so speed and acceleration remain continuous.
    private async Task AnimateLibraryDetailPaneOutCoreAsync(
        int version,
        CancellationToken token,
        double width)
    {
        var currentX = (LibraryDetailPane.RenderTransform as TranslateTransform)?.X ?? 0;
        var translate = new TranslateTransform(currentX, 0);
        LibraryDetailPane.RenderTransform = translate;
        LibraryDetailPane.Opacity = 1;
        try
        {
            await RunLibraryDetailPaneDoubleAnimationAsync(
                translate,
                TranslateTransform.XProperty,
                currentX,
                width,
                LibraryDetailSlideDurationMs,
                new CubicEaseInOut(),
                token);
        }
        catch
        {
        }
        if (version != _detailPaneAnimationVersion) return;
        CompleteClearSelectedBook();
    }

    // Cancels any in-flight detail-pane animation and hands out a fresh token
    // for the next one, so a newer show/hide command never fights an older
    // animation that is still running.
    private CancellationToken BeginDetailPaneAnimation()
    {
        _detailPaneAnimationCancellation?.Cancel();
        _detailPaneAnimationCancellation?.Dispose();
        var cts = new CancellationTokenSource();
        _detailPaneAnimationCancellation = cts;
        return cts.Token;
    }

    private void CancelDetailPaneAnimation()
    {
        _detailPaneAnimationCancellation?.Cancel();
        _detailPaneAnimationCancellation?.Dispose();
        _detailPaneAnimationCancellation = null;
    }

    private static async Task RunLibraryDetailPaneDoubleAnimationAsync(
        Avalonia.Animation.Animatable target,
        AvaloniaProperty property,
        double from,
        double to,
        int durationMs,
        Easing easing,
        CancellationToken token = default)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(durationMs),
            Easing = easing,
            FillMode = FillMode.Forward
        };
        animation.Children.Add(new KeyFrame
        {
            Cue = new Cue(0d),
            Setters = { new Avalonia.Styling.Setter(property, from) }
        });
        animation.Children.Add(new KeyFrame
        {
            Cue = new Cue(1d),
            Setters = { new Avalonia.Styling.Setter(property, to) }
        });
        await animation.RunAsync(target, token);
    }

    // Finishes a deselection: hides the pane, frees the column and resets the
    // detail fields. Kept separate from ClearSelectedBook so an animated exit
    // can run with the old content visible before this resets everything.
    private void CompleteClearSelectedBook()
    {
        CancelDetailPaneAnimation();
        LibraryDetailPane.IsVisible = false;
        LibraryDetailPane.RenderTransform = new TranslateTransform(0, 0);
        LibraryDetailPane.Opacity = 1;
        if (LibraryRoot.ColumnDefinitions.Count >= 3)
            LibraryRoot.ColumnDefinitions[2].Width = new GridLength(0);
        DetailCoverImage.Source = null;
        DetailCoverPlaceholder.IsVisible = true;
        DetailTitleText.Text = T("请选择一本书");
        DetailAuthorsText.Text = string.Empty;
        DetailDoubanRatingBox.Text = string.Empty;
        DetailStateText.Text = string.Empty;
        DetailOrganizationText.Text = string.Empty;
        DetailPublicationText.Text = string.Empty;
        DetailIdentifierText.Text = string.Empty;
        DetailTagsBox.Text = string.Empty;
        DetailCategoryBox.Text = string.Empty;
        DetailDescriptionBox.Text = string.Empty;
        DetailSeriesBox.Text = string.Empty;
        DetailPublisherBox.Text = string.Empty;
        DetailPublishDateBox.Text = string.Empty;
        DetailIsbnBox.Text = string.Empty;
        DetailPageCountBox.Text = string.Empty;
        DetailBindingBox.Text = string.Empty;
        UpdateDetailActionIcons(false, LibraryReadingStatus.Unread);
        DetailDescriptionText.Text = T("暂无简介");
        DetailFiles.ItemsSource = Array.Empty<BookFile>();
        DetailCollectionBox.ItemsSource = CollectionFolders;
        DetailCollectionBox.SelectedItem = null;
        CollectionMembershipButton.Content = T("加入收藏夹");
    }

    private void UpdateDetailActionIcons(bool isFavorite, LibraryReadingStatus readingStatus)
    {
        // Keep one minimal star silhouette; fill conveys the selected state
        // without changing the icon's optical footprint.
        DetailFavoriteIcon.Fill = isFavorite ? Brushes.Black : Brushes.Transparent;
        var favoriteLabel = isFavorite ? T("已收藏；点击取消收藏") : T("未收藏；点击加入收藏");
        ToolTip.SetTip(DetailFavoriteButton, favoriteLabel);
        AutomationProperties.SetName(DetailFavoriteButton, favoriteLabel);

        // Closed book, open book and check share the same centred 24-unit box.
        var (data, label) = readingStatus switch
        {
            LibraryReadingStatus.Reading => (
                "M 3,6 L 12,9 L 21,6 V 19 L 12,22 L 3,19 Z M 12,9 V 22",
                T("阅读中；点击标记为已读")),
            LibraryReadingStatus.Finished => (
                "M 4,12 L 9,17 L 20,6",
                T("已读；点击重置为待读")),
            _ => (
                "M 12,4 A 8,8 0 1 0 12,20 A 8,8 0 1 0 12,4",
                T("待读；点击标记为阅读中"))
        };
        DetailReadingStatusIcon.Data = Geometry.Parse(data);
        ToolTip.SetTip(DetailReadingStatusButton, label);
        AutomationProperties.SetName(DetailReadingStatusButton, label);
    }

    private async Task RefreshLibraryAsync()
    {
        await ViewModel.RefreshAsync(_lifetimeCancellation.Token);
        SetupFilterControls();
        await RefreshCollectionsAsync();
        UpdateLibraryUi();
        SetTaskStatus(ViewModel.StatusText);
    }

    private async Task RefreshCollectionsAsync()
    {
        var collections = await _library.GetCollectionsAsync(_lifetimeCancellation.Token);
        var books = ViewModel.LibraryBooks;

        foreach (var folder in CollectionFolders) folder.Dispose();
        CollectionFolders.Clear();

        foreach (var collection in collections)
        {
            var collectionBooks = books
                .Where(book => book.CollectionIds.Contains(collection.Id))
                .OrderByDescending(book => book.UpdatedAt)
                .ToArray();
            var coverPaths = collectionBooks
                .Select(book => book.CoverPath)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Take(3)
                .ToArray();
            CollectionFolders.Add(new BookCollectionFolderViewModel(
                collection,
                collectionBooks.Length,
                _paths.Data,
                coverPaths));
        }

    }

    private async Task ImportPathsAsync(IEnumerable<string> paths)
    {
        var selectedPaths = paths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (selectedPaths.Length == 0)
        {
            SetTaskStatus(T("没有选择可导入的文件。"));
            return;
        }

        try
        {
            var inputPaths = LibraryDropImportPolicy.ExpandImportableFiles(selectedPaths);
            if (inputPaths.Length == 0)
            {
                SetTaskStatus(T("所选位置没有 EPUB、PDF、MOBI 或 AZW3 文件。"));
                await ShowMessageAsync(T("无法导入"), T("拖入的文件或文件夹中没有 EPUB、PDF、MOBI 或 AZW3 书籍文件。"));
                return;
            }
            var requestedFormats = await ChooseImportFormatsAsync(inputPaths);
            if (requestedFormats is null)
            {
                SetTaskStatus(T("已取消导入。"));
                return;
            }
            SetTaskStatus(T("正在导入 {0} 个位置…", inputPaths.Length));
            ShowTaskProgressPopup();
            TaskProgressPopupText.Text = T("正在导入 {0} 个位置…", inputPaths.Length);
            var progress = new Progress<TransferProgress>(value =>
            {
                var message = string.IsNullOrWhiteSpace(value.Message)
                    ? T("正在导入：{0:0}%", value.Percentage)
                    : UiText.Localize(value.Message);
                SetTaskStatus(message);
                TaskProgressPopupBar.Value = value.Percentage;
                TaskProgressPopupText.Text = message;
            });
            var result = await ViewModel.ImportAsync(inputPaths, progress, _lifetimeCancellation.Token);
            var automaticFormats = await AutoGenerateReaderFormatsForImportsAsync(
                result,
                _lifetimeCancellation.Token,
                requestedFormats);
            HideTaskProgressPopup();
            await RefreshCollectionsAsync();
            UpdateLibraryUi();
            if (_appSettings.AutoDoubanMatchOnImport)
            {
                var importedIds = result.Items
                    .Where(item => item.Succeeded && item.Added && item.Book is not null)
                    .Select(item => item.Book!.Id)
                    .ToHashSet();
                var importedCards = ViewModel.Books
                    .Where(card => card.Book is not null && importedIds.Contains(card.Book.Id))
                    .ToArray();
                if (importedCards.Length > 0)
                    await RunDoubanBatchMatchAsync(importedCards);
            }
            var automaticSuffix = automaticFormats.Failures.Count > 0
                ? T("；格式补齐失败 {0} 项", automaticFormats.Failures.Count)
                : automaticFormats.GeneratedCount > 0
                    ? T("；已补齐 {0} 个 EPUB/AZW3 文件", automaticFormats.GeneratedCount)
                    : string.Empty;
            SetTaskStatus(result.FailureCount == 0
                ? T("已导入 {0} 本书{1}。", result.SuccessCount, automaticSuffix)
                : T("已导入 {0} 本书，{1} 项失败{2}。 ", result.SuccessCount, result.FailureCount, automaticSuffix));
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            HideTaskProgressPopup();
        }
        catch (Exception exception)
        {
            HideTaskProgressPopup();
            SetTaskStatus(T("导入失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("导入失败"), UiText.Localize(exception.Message));
        }
    }

    private Task<IReadOnlyDictionary<string, IReadOnlyCollection<string>>?> ChooseImportFormatsAsync(
        IReadOnlyList<string> files)
    {
        _importFormatSelectionCompletion?.TrySetResult(null);
        _importFormatSelectionRows.Clear();
        ImportFormatSelectionList.Children.Clear();
        foreach (var file in files)
        {
            var toggle = new ToggleSwitch
            {
                // Start consistent with the global "导入后补齐 EPUB 与 AZW3"
                // preference; each row can still be overridden individually.
                IsChecked = _appSettings.AutoGenerateEpubAndAzw3OnImport,
                OnContent = T("补齐"),
                OffContent = T("仅导入")
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 14 };
            row.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock { Text = Path.GetFileName(file), FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                    new TextBlock { Text = Path.GetDirectoryName(file) ?? string.Empty, FontSize = 10, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis }
                }
            });
            Grid.SetColumn(toggle, 1);
            row.Children.Add(toggle);
            ImportFormatSelectionList.Children.Add(new Border
            {
                Padding = new Thickness(10, 8),
                BorderBrush = Brushes.LightGray,
                BorderThickness = new Thickness(1),
                Child = row
            });
            _importFormatSelectionRows.Add((file, toggle));
        }
        ImportFormatSelectionSummaryText.Text = T("共 {0} 个文件。可逐项决定是否在导入后补齐 EPUB 与 AZW3；原始文件始终保留。", files.Count);
        ShowOverlay(ImportFormatSelectionOverlay);
        ImportFormatSelectionOverlay.Focus();
        _importFormatSelectionCompletion = new TaskCompletionSource<IReadOnlyDictionary<string, IReadOnlyCollection<string>>?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return _importFormatSelectionCompletion.Task;
    }

    private void CompleteImportFormatSelection(bool import)
    {
        var completion = _importFormatSelectionCompletion;
        if (completion is null) return;
        _importFormatSelectionCompletion = null;
        ImportFormatSelectionOverlay.IsVisible = false;
        var result = import
            ? _importFormatSelectionRows.ToDictionary(
                row => row.FilePath,
                row => (IReadOnlyCollection<string>)(row.Toggle.IsChecked == true ? new[] { "epub", "azw3" } : Array.Empty<string>()),
                StringComparer.OrdinalIgnoreCase)
            : null;
        _importFormatSelectionRows.Clear();
        ImportFormatSelectionList.Children.Clear();
        completion.TrySetResult(result);
    }

    private void ImportFormatSelectionPrimaryButton_Click(object? sender, RoutedEventArgs e) =>
        CompleteImportFormatSelection(true);

    private void ImportFormatSelectionCancelButton_Click(object? sender, RoutedEventArgs e) =>
        CompleteImportFormatSelection(false);

    private void ImportFormatSelectionOverlay_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.Enter)) return;
        e.Handled = true;
        CompleteImportFormatSelection(e.Key == Key.Enter);
    }

    private void LibraryPane_DragOver(object? sender, DragEventArgs e)
    {
        // This handler is installed on the window so Linux file managers can
        // reach the native XDND target. Do not let it handle other pages: in
        // particular, doing so would overwrite the Kindle resource page's
        // Copy effect with None.
        if (!LibraryWorkspace.IsVisible) return;

        _dropOverlayHideTimer?.Stop();
        // X11/Wayland drag payloads are often delivered lazily. Reading the
        // paths during DragOver can return an empty list for directories and
        // advertising None prevents the file manager from ever sending Drop.
        // The advertised File format is enough here; materialize paths only
        // after the drop is accepted.
        var hasStorageItems = LibraryDropImportPolicy.CanAccept(e.DataTransfer);
        e.DragEffects = hasStorageItems ? DragDropEffects.Copy : DragDropEffects.None;
        LibraryDropOverlay.IsVisible = hasStorageItems;
        e.Handled = true;
    }

    private void LibraryPane_DragLeave(object? sender, RoutedEventArgs e)
    {
        if (!LibraryWorkspace.IsVisible) return;

        // Crossing child element boundaries fires DragLeave immediately
        // followed by DragOver again; hiding instantly makes the overlay
        // flicker while the drag moves across the book grid. Defer the hide
        // and cancel it if another DragOver arrives within the delay.
        _dropOverlayHideTimer ??= CreateDropOverlayHideTimer();
        _dropOverlayHideTimer.Stop();
        _dropOverlayHideTimer.Start();
        e.Handled = true;
    }

    private DispatcherTimer CreateDropOverlayHideTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            LibraryDropOverlay.IsVisible = false;
        };
        return timer;
    }

    private void LibraryRoot_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!LibraryDetailPane.IsVisible
            || !e.GetCurrentPoint(LibraryRoot).Properties.IsLeftButtonPressed
            || IsSourceWithin(e.Source, LibraryDetailPane)
            || IsBookCardSource(e.Source))
            return;

        ClearSelectedBook();
    }

    private static bool IsSourceWithin(object? source, Visual container)
    {
        for (var current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (ReferenceEquals(current, container)) return true;
        }

        return false;
    }

    private static bool IsBookCardSource(object? source)
    {
        for (var current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            if ((current is Grid || current is Border)
                && current is Control control
                && control.DataContext is BookCardViewModel)
                return true;
        }

        return false;
    }

    private static bool IsScrollBarSource(object? source)
    {
        for (var current = source as Visual; current is not null; current = current.GetVisualParent())
        {
            if (current is ScrollBar)
                return true;
        }

        return false;
    }

    private async void LibraryPane_Drop(object? sender, DragEventArgs e)
    {
        if (!LibraryWorkspace.IsVisible) return;

        _dropOverlayHideTimer?.Stop();
        var paths = LibraryDropImportPolicy.GetLocalPaths(e.DataTransfer);
        LibraryDropOverlay.IsVisible = false;
        e.Handled = true;
        if (paths.Length > 0)
            await ImportPathsAsync(paths);
        else
            SetTaskStatus(T("无法读取拖入的文件或文件夹路径。"));
    }

    private async Task OpenBookAsync(
        BookCardViewModel card,
        BookFile? requestedFile = null,
        bool restoreProgress = true)
    {
        var file = requestedFile ?? ReaderBookSelectionPolicy.SelectPreferred(
            card.Book.Files,
            _appSettings.PreferredOpenFormat);
        if (file is null)
        {
            SetTaskStatus(T("这本书没有可打开的文件。"));
            await ShowMessageAsync(T("无法打开书籍"), T("所选格式文件不存在或已不再支持。"));
            return;
        }

        var path = ViewModel.GetAbsoluteFilePath(file);
        if (!File.Exists(path))
        {
            SetTaskStatus(T("找不到文件：{0}", file.RelativePath));
            await ShowMessageAsync(T("无法打开书籍"), T("所选格式文件不存在或已被删除。"));
            return;
        }

        if (string.Equals(file.Format, "epub", StringComparison.OrdinalIgnoreCase))
        {
            await OpenEpubReaderAsync(card, file, path, restoreProgress);
            return;
        }

        if (string.Equals(file.Format, "pdf", StringComparison.OrdinalIgnoreCase))
        {
            await OpenPdfReaderAsync(card, file, path);
            return;
        }

        if (file.Format.Equals("mobi", StringComparison.OrdinalIgnoreCase)
            || file.Format.Equals("azw3", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                SetTaskStatus(T("正在准备《{0}》的阅读缓存…", card.Title));
                var cache = await _readerFormatCache.PrepareEpubAsync(
                    path,
                    file.Sha256,
                    file.Format,
                    _lifetimeCancellation.Token);
                await OpenEpubReaderAsync(card, file, cache.EpubPath, restoreProgress);
            }
            catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
            {
            }
            catch (Exception exception)
            {
                SetTaskStatus(T("准备阅读缓存失败：{0}", UiText.Localize(exception.Message)));
                await ShowMessageAsync(T("无法打开书籍"), UiText.Localize(exception.Message));
            }
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            SetTaskStatus(T("已用系统默认程序打开《{0}》。", card.Title));
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("打开文件失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法打开书籍"), UiText.Localize(exception.Message));
        }
    }

    private async Task DeleteSelectedBookAsync()
    {
        if (_selectedCard is null) return;
        var card = _selectedCard;
        if (_readerDocument is not null || _readerIsPdf)
        {
            await ShowMessageAsync(T("无法删除书籍"), T("当前正在阅读这本书，请先关闭阅读器。"));
            return;
        }
        if (!await ConfirmAsync(T("删除书籍"), T("确定删除《{0}》及其全部文件吗？", card.Title))) return;

        try
        {
            await ViewModel.DeleteBookAsync(card.Book, _lifetimeCancellation.Token);
            ClearSelectedBook();
            await RefreshCollectionsAsync();
            UpdateLibraryUi();
            SetTaskStatus(ViewModel.StatusText);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("删除失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法删除书籍"), UiText.Localize(exception.Message));
        }
    }

    private async Task DeleteFileAsync(BookFile file)
    {
        if (_selectedCard is null) return;
        var card = _selectedCard;
        if (_readerDocument is not null || _readerIsPdf)
        {
            await ShowMessageAsync(T("无法删除格式"), T("当前正在阅读这本书，请先关闭阅读器。"));
            return;
        }
        if (!await ConfirmAsync(T("删除文件"), T("确定从《{0}》中删除 {1} 文件吗？", card.Title, file.Format.ToUpperInvariant()))) return;

        try
        {
            await ViewModel.DeleteFileAsync(card.Book, file, _lifetimeCancellation.Token);
            await RefreshLibraryAsync();
            SetTaskStatus(ViewModel.StatusText);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("删除文件失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法删除格式"), UiText.Localize(exception.Message));
        }
    }

    private IReadOnlyList<BookCardViewModel> GetSelectedCards()
    {
        var selected = ViewModel.Books
            .Where(card => _selectedBookIds.Contains(card.Book.Id))
            .ToArray();
        return selected.Length > 0 || _selectedCard is null
            ? selected
            : [_selectedCard];
    }

    private void UpdateMultiSelectionUi()
    {
        SyncCardSelectionVisuals();
        var selectedCount = GetSelectedCards().Count;
        MultiSelectionBar.IsVisible = selectedCount > 1;
        MultiSelectionText.Text = selectedCount > 0 ? T("已选择 {0} 本书", selectedCount) : string.Empty;
    }

    private void SyncCardSelectionVisuals()
    {
        // 单点选中的书显示黑边；只有真正多选（≥2 本）时才附带 ✓ 徽标。
        var multi = _selectedBookIds.Count > 1;
        foreach (var card in ViewModel.Books)
        {
            var selected = _selectedBookIds.Contains(card.Book.Id);
            card.IsSelected = selected;
            card.IsMultiSelected = selected && multi;
        }
    }

    private ContextMenu BuildBookContextMenu(BookCardViewModel card)
    {
        var menu = new ContextMenu();

        var openMenu = new MenuItem { Header = T("打开书籍") };
        ApplyLegacyMenuItemSize(openMenu);
        openMenu.Resources["FlyoutThemeMinWidth"] = 0d;
        foreach (var format in new[] { "EPUB", "PDF", "AZW3" })
        {
            var item = new MenuItem
            {
                Header = format,
                Width = 64,
                MinWidth = 0,
                IsEnabled = card.Book.Files.Any(file =>
                    string.Equals(file.Format, format, StringComparison.OrdinalIgnoreCase)
                    && ReaderBookSelectionPolicy.GetSupportedFiles([file]).Count > 0)
            };
            ApplyLegacyMenuItemSize(item);
            item.Click += async (_, _) => await OpenBookFormatAsync(card, format);
            openMenu.Items.Add(item);
        }
        openMenu.IsEnabled = openMenu.Items.OfType<MenuItem>().Any(item => item.IsEnabled);
        menu.Items.Add(openMenu);
        menu.Items.Add(new Separator());

        var convertMenu = new MenuItem { Header = T("转换为") };
        ApplyLegacyMenuItemSize(convertMenu);
        convertMenu.Resources["FlyoutThemeMinWidth"] = 0d;
        foreach (var target in new[] { "epub", "azw3", "pdf" })
        {
            var item = new MenuItem
            {
                Header = target.ToUpperInvariant(),
                Width = 64,
                MinWidth = 0,
                Tag = target
            };
            ApplyLegacyMenuItemSize(item);
            item.Click += async (_, _) => await ConvertBookAsync(card, target);
            convertMenu.Items.Add(item);
        }
        menu.Items.Add(convertMenu);
        menu.Items.Add(new Separator());

        menu.Items.Add(CreateMenuItem(T("发送到 Kindle 设备"), SendSelectedBookToKindleCoreAsync));
        menu.Items.Add(CreateMenuItem(T("发送到 Kindle 邮箱"), SendSelectedBooksByEmailAsync));

        var collectionMenu = new MenuItem { Header = T("收藏夹") };
        ApplyLegacyMenuItemSize(collectionMenu);
        collectionMenu.Resources["FlyoutThemeMinWidth"] = 0d;
        foreach (var folder in CollectionFolders)
        {
            var item = new MenuItem
            {
                Header = folder.Name,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = card.Book.CollectionIds.Contains(folder.Collection.Id),
                Tag = folder
            };
            ApplyLegacyMenuItemSize(item);
            item.Click += async (_, _) => await ToggleBookCollectionAsync(card, folder);
            collectionMenu.Items.Add(item);
        }
        if (CollectionFolders.Count > 0)
            collectionMenu.Items.Add(new Separator());
        collectionMenu.Items.Add(CreateMenuItem(T("新建收藏夹…"), async () =>
        {
            var name = await PromptCollectionNameAsync();
            if (string.IsNullOrWhiteSpace(name)) return;
            try
            {
                var collection = await _library.CreateCollectionAsync(name, _lifetimeCancellation.Token);
                await _library.AddBookToCollectionAsync(card.Book.Id, collection.Id, _lifetimeCancellation.Token);
                await RefreshLibraryAsync();
                SetTaskStatus(T("已创建并加入收藏夹“{0}”。", name));
            }
            catch (Exception exception)
            {
                SetTaskStatus(T("创建收藏夹失败：{0}", UiText.Localize(exception.Message)));
            }
        }));
        if (CollectionFolders.Count > 0)
        {
            var deleteCollectionMenu = new MenuItem { Header = T("删除收藏夹") };
            ApplyLegacyMenuItemSize(deleteCollectionMenu);
            foreach (var folder in CollectionFolders)
                deleteCollectionMenu.Items.Add(CreateMenuItem(folder.Name, () => DeleteCollectionAsync(folder)));
            collectionMenu.Items.Add(deleteCollectionMenu);
        }
        menu.Items.Add(collectionMenu);
        menu.Items.Add(new Separator());

        var deleteFormatMenu = new MenuItem { Header = T("删除格式") };
        ApplyLegacyMenuItemSize(deleteFormatMenu);
        deleteFormatMenu.Resources["FlyoutThemeMinWidth"] = 0d;
        foreach (var format in new[] { "EPUB", "PDF", "MOBI", "AZW3" })
        {
            var file = card.Book.Files.FirstOrDefault(candidate =>
                string.Equals(candidate.Format, format, StringComparison.OrdinalIgnoreCase));
            var item = new MenuItem
            {
                Header = format,
                Width = 64,
                MinWidth = 0,
                IsEnabled = file is not null,
                Tag = file
            };
            ApplyLegacyMenuItemSize(item);
            item.Click += async (_, _) =>
            {
                if (item.Tag is BookFile selectedFile)
                    await DeleteFileAsync(selectedFile);
            };
            deleteFormatMenu.Items.Add(item);
        }
        deleteFormatMenu.IsEnabled = deleteFormatMenu.Items.OfType<MenuItem>().Any(item => item.IsEnabled);
        menu.Items.Add(deleteFormatMenu);
        deleteFormatMenu.Items.Add(new Separator());
        deleteFormatMenu.Items.Add(CreateMenuItem(T("全部"), () => DeleteBookFromContextAsync(card)));

        AttachInstantMenuHover(menu);
        return menu;
    }

    private async Task OpenBookFormatAsync(BookCardViewModel card, string format)
    {
        var file = ReaderBookSelectionPolicy.GetSupportedFiles(card.Book.Files)
            .FirstOrDefault(candidate =>
                string.Equals(candidate.Format, format, StringComparison.OrdinalIgnoreCase));
        if (file is null)
        {
            SetTaskStatus(T("所选格式文件不存在或不支持。"));
            return;
        }

        await OpenBookAsync(card, file);
    }

    private static MenuItem CreateMenuItem(string header, Func<Task> action)
    {
        var item = new MenuItem { Header = header };
        ApplyLegacyMenuItemSize(item);
        item.Click += async (_, _) => await action();
        return item;
    }

    private static void ApplyLegacyMenuItemSize(MenuItem item)
    {
        // Avalonia uses device-independent units: 32 units become the legacy
        // menu's 40 physical pixels on a 125%-scaled Windows display.
        item.Height = 32;
        item.MinHeight = 32;
        item.MaxHeight = 32;
        item.Padding = new Thickness(8, 4, 8, 7);
    }

    private static void AttachInstantMenuHover(ContextMenu menu)
    {
        foreach (var item in EnumerateMenuItems(menu.Items))
            item.PointerEntered += (_, _) => ActivateInstantMenuBranch(menu.Items, item);

        menu.Closed += (_, _) => ClearInstantMenuHover(menu.Items);
    }

    private static IEnumerable<MenuItem> EnumerateMenuItems(IEnumerable<object?> items)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            yield return item;
            foreach (var child in EnumerateMenuItems(item.Items))
                yield return child;
        }
    }

    private static bool ActivateInstantMenuBranch(IEnumerable<object?> items, MenuItem activeItem)
    {
        var containsActiveItem = false;
        foreach (var item in items.OfType<MenuItem>())
        {
            var activeChild = ActivateInstantMenuBranch(item.Items, activeItem);
            var isActiveBranch = ReferenceEquals(item, activeItem) || activeChild;
            SetInstantMenuHover(item, isActiveBranch);

            if (!isActiveBranch && item.IsSubMenuOpen)
                item.IsSubMenuOpen = false;

            containsActiveItem |= isActiveBranch;
        }

        return containsActiveItem;
    }

    private static void ClearInstantMenuHover(IEnumerable<object?> items)
    {
        foreach (var item in items.OfType<MenuItem>())
        {
            SetInstantMenuHover(item, false);
            ClearInstantMenuHover(item.Items);
        }
    }

    private static void SetInstantMenuHover(MenuItem item, bool isActive)
    {
        if (isActive)
            item.Classes.Add(InstantMenuHoverClass);
        else
            item.Classes.Remove(InstantMenuHoverClass);
    }

    // 右键菜单流程会同步 ListBox 选中项，但 SelectionChanged 处理器里的
    // SelectBook 会弹出详情页；用该标志在右键期间抑制弹窗行为。
    private bool _suppressSelectionPane;
    // ListBox 原生「右键按下即选中」在我们收到 ContextRequested 之前就会
    // 触发一次 SelectionChanged；此标志把随之而来的弹面板抑制掉。
    private bool _suppressNextSelectionPane;

    private void BookCard_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not BookCardViewModel card) return;
        _suppressSelectionPane = true;
        try
        {
            if (!_selectedBookIds.Contains(card.Book.Id))
            {
                _selectedBookIds.Clear();
                _selectedBookIds.Add(card.Book.Id);
                var activeList = BookGrid.IsVisible ? BookGrid : BookList;
                activeList.SelectedItems?.Clear();
                activeList.SelectedItems?.Add(card);
            }
        }
        finally
        {
            _suppressSelectionPane = false;
        }
        _selectedCard = card;
        _multiSelectAnchor = card;
        // 右键只更新选中与菜单目标，不主动弹出详情页；若详情页已经打开，
        // 顺手把内容切到当前书籍，避免面板与菜单目标不一致。
        if (LibraryDetailPane.IsVisible)
            SelectBook(card);
        UpdateMultiSelectionUi();
        var menu = BuildBookContextMenu(card);
        menu.Open(control);
        _suppressNextSelectionPane = false;
        e.Handled = true;
    }

    private static string GetReadingStatusName(LibraryReadingStatus status) => status switch
    {
        LibraryReadingStatus.Reading => T("阅读中"),
        LibraryReadingStatus.Finished => T("已读"),
        _ => T("待读")
    };

    private async Task DeleteSelectedBooksAsync()
    {
        var cards = GetSelectedCards();
        if (cards.Count == 0) return;
        if (_readerDocument is not null || _readerIsPdf)
        {
            await ShowMessageAsync(T("无法删除书籍"), T("当前正在阅读其中一本书，请先关闭阅读器。"));
            return;
        }
        if (!await ConfirmAsync(T("删除所选书籍"), T("确定删除选中的 {0} 本书及其文件吗？", cards.Count))) return;

        try
        {
            foreach (var card in cards)
                await _library.DeleteAsync(card.Book.Id, _lifetimeCancellation.Token);
            _selectedBookIds.Clear();
            ClearSelectedBook();
            await RefreshLibraryAsync();
            SetTaskStatus(T("已删除 {0} 本书。", cards.Count));
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("批量删除失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法删除书籍"), UiText.Localize(exception.Message));
        }
    }

    private async Task DeleteBookFromContextAsync(BookCardViewModel card)
    {
        if (_readerDocument is not null || _readerIsPdf)
        {
            await ShowMessageAsync(T("无法删除书籍"), T("当前正在阅读这本书，请先关闭阅读器。"));
            return;
        }
        if (!await ConfirmAsync(T("删除书籍"), T("确定删除《{0}》及其全部文件吗？", card.Title))) return;
        try
        {
            await _library.DeleteAsync(card.Book.Id, _lifetimeCancellation.Token);
            _selectedBookIds.Remove(card.Book.Id);
            if (_selectedCard?.Book.Id == card.Book.Id) ClearSelectedBook();
            await RefreshLibraryAsync();
            SetTaskStatus(T("已删除《{0}》。", card.Title));
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("删除失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法删除书籍"), UiText.Localize(exception.Message));
        }
    }

    private async Task ToggleFavoriteAsync(BookCardViewModel card)
    {
        card.Book.IsFavorite = !card.Book.IsFavorite;
        await SaveBookMetadataAsync(card, card.Book.IsFavorite ? T("已加入收藏。") : T("已取消收藏。"));
    }

    private async Task UpdateReadingStatusAsync(BookCardViewModel card, LibraryReadingStatus status)
    {
        card.Book.ReadingStatus = status;
        await SaveBookMetadataAsync(card, T("已标记为“{0}”。", GetReadingStatusName(status)));
    }

    private async Task ToggleBookCollectionAsync(BookCardViewModel card, BookCollectionFolderViewModel folder)
    {
        try
        {
            if (card.Book.CollectionIds.Contains(folder.Collection.Id))
                await _library.RemoveBookFromCollectionAsync(card.Book.Id, folder.Collection.Id, _lifetimeCancellation.Token);
            else
                await _library.AddBookToCollectionAsync(card.Book.Id, folder.Collection.Id, _lifetimeCancellation.Token);
            await RefreshLibraryAsync();
            SetTaskStatus(T("已更新“{0}”中的书籍归属。", folder.Name));
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("更新收藏夹失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法更新收藏夹"), UiText.Localize(exception.Message));
        }
    }

    private async Task SaveBookMetadataAsync(BookCardViewModel card, string successMessage)
    {
        try
        {
            await _library.UpdateMetadataAsync(card.Book, _lifetimeCancellation.Token);
            await RefreshLibraryAsync();
            SetTaskStatus(successMessage);
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("保存书籍信息失败：{0}", UiText.Localize(exception.Message)));
        }
    }

    private async Task ConvertBookAsync(BookCardViewModel card, string targetFormat)
    {
        if (_conversionInProgress)
        {
            await ShowMessageAsync(T("格式转换"), T("已有一本书正在转换，请稍候。"));
            return;
        }

        var target = BookFormatConversionPolicy.Normalize(targetFormat);
        if (!BookFormatConversionPolicy.IsConvertibleFormat(target)) return;
        if (card.Book.Files.Any(file =>
                string.Equals(BookFormatConversionPolicy.Normalize(file.Format), target, StringComparison.OrdinalIgnoreCase)))
        {
            await ShowMessageAsync(T("格式转换"), T("这本书已经有 {0} 格式。", target.ToUpperInvariant()));
            return;
        }

        var sourceFile = BookFormatConversionPolicy.SelectSource(card.Book.Files, target);
        if (sourceFile is null)
        {
            await ShowMessageAsync(T("格式转换"), T("需要 EPUB、AZW3、PDF 或 MOBI 作为转换源。"));
            return;
        }

        var sourcePath = ViewModel.GetAbsoluteFilePath(sourceFile);
        if (!File.Exists(sourcePath))
        {
            SetTaskStatus(T("找不到转换来源：{0}", sourceFile.RelativePath));
            return;
        }

        _conversionInProgress = true;
        _conversionCard = card;
        _conversionMinimized = false;
        _conversionCancellation?.Dispose();
        _conversionCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        var initialProgress = new FormatConversionProgress(
            0,
            T("准备将 {0} 转换为 {1}…", sourceFile.Format.ToUpperInvariant(), target.ToUpperInvariant()));
        _conversionLastProgress = initialProgress;
        ShowBookConversionPopup(card.Title, sourceFile.Format, target, initialProgress);
        SetTaskStatus(UiText.Localize(initialProgress.Message));
        var temporaryDirectory = Path.Combine(Path.GetTempPath(), "KkindleConversions", Guid.NewGuid().ToString("N"));
        var temporaryOutput = Path.Combine(
            temporaryDirectory,
            KindleTransferPolicy.CreateSafeFileName(card.Title, "." + target));
        try
        {
            Directory.CreateDirectory(temporaryDirectory);
            var progress = new Progress<FormatConversionProgress>(ApplyBookConversionProgress);
            await _formatConverter.ConvertAsync(
                sourcePath,
                temporaryOutput,
                progress,
                _conversionCancellation.Token,
                new FormatConversionMetadata(card.Book.Title, card.Book.Authors));
            ApplyBookConversionProgress(new FormatConversionProgress(100, T("正在写入书库…")));
            await _library.AddFileToBookAsync(card.Book.Id, temporaryOutput, _conversionCancellation.Token);
            await RefreshLibraryAsync();
            ApplyBookConversionProgress(new FormatConversionProgress(100, T("转换完成。")));
            SetTaskStatus(T("已为《{0}》添加 {1} 格式。", card.Title, target.ToUpperInvariant()));
        }
        catch (OperationCanceledException) when (_conversionCancellation?.IsCancellationRequested == true)
        {
            SetTaskStatus(T("格式转换已取消。"));
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("格式转换失败：{0}", UiText.Localize(exception.Message)));
            ApplyBookConversionProgress(new FormatConversionProgress(
                _conversionLastProgress.Percentage,
                T("格式转换失败。")));
            await ShowMessageAsync(T("格式转换失败"), UiText.Localize(exception.Message));
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryOutput)) File.Delete(temporaryOutput);
                if (Directory.Exists(temporaryDirectory)) Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch
            {
            }
            BookConversionPopup.IsVisible = false;
            _conversionCard?.ClearConversionProgress();
            _conversionCard = null;
            _conversionMinimized = false;
            _conversionInProgress = false;
            _conversionCancellation?.Dispose();
            _conversionCancellation = null;
        }
    }

    private async Task<AutomaticReaderFormatGenerationResult> AutoGenerateReaderFormatsForImportsAsync(
        ImportBatchResult importResult,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, IReadOnlyCollection<string>>? requestedFormatsBySourcePath = null)
    {
        // The global setting gates flows without an explicit per-file choice;
        // the folder-import dialog always decides explicitly, so its toggles
        // stay authoritative even when the global preference is off.
        if (!_appSettings.AutoGenerateEpubAndAzw3OnImport
            && requestedFormatsBySourcePath is null)
            return new AutomaticReaderFormatGenerationResult(0, []);

        var books = importResult.Items
            .Where(item => item.Succeeded && item.Added && item.Book is not null)
            .Select(item => new
            {
                Book = item.Book!,
                Formats = requestedFormatsBySourcePath is null
                    ? (IReadOnlyCollection<string>)["epub", "azw3"]
                    : requestedFormatsBySourcePath.GetValueOrDefault(Path.GetFullPath(item.SourcePath), Array.Empty<string>())
            })
            .Where(item => item.Formats.Count > 0)
            .GroupBy(item => item.Book.Id)
            .Select(group => new
            {
                Book = group.First().Book,
                Formats = group.SelectMany(item => item.Formats).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            })
            .Where(item => BookFormatConversionPolicy.GetMissingDefaultReaderFormats(item.Book.Files).Count > 0)
            .ToArray();
        if (books.Length == 0)
            return new AutomaticReaderFormatGenerationResult(0, []);
        if (_conversionInProgress || _automaticReaderFormatGenerationInProgress)
            return new AutomaticReaderFormatGenerationResult(
                0,
                [T("已有格式转换正在进行，未启动 EPUB/AZW3 自动补齐。")]);

        _automaticReaderFormatGenerationInProgress = true;
        var temporaryRoot = Path.Combine(Path.GetTempPath(), "Kkindle", "automatic-formats", Guid.NewGuid().ToString("N"));
        var failures = new List<string>();
        var generatedCount = 0;
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            foreach (var item in books)
            {
                var book = item.Book;
                foreach (var targetFormat in BookFormatConversionPolicy.GetMissingDefaultReaderFormats(book.Files)
                             .Where(item.Formats.Contains))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var sourceFile = BookFormatConversionPolicy.SelectSource(book.Files, targetFormat);
                    if (sourceFile is null)
                    {
                        failures.Add(T("《{0}》没有可用于生成 {1} 的源格式。", book.Title, targetFormat.ToUpperInvariant()));
                        continue;
                    }

                    var bookTemporaryDirectory = Path.Combine(temporaryRoot, book.Id.ToString("N"));
                    var temporaryOutput = Path.Combine(
                        bookTemporaryDirectory,
                        KindleTransferPolicy.CreateSafeFileName(book.Title, "." + targetFormat));
                    try
                    {
                        Directory.CreateDirectory(bookTemporaryDirectory);
                        var sourcePath = _library.GetAbsoluteFilePath(sourceFile);
                        SetTaskStatus(T("正在为《{0}》生成 {1}…", book.Title, targetFormat.ToUpperInvariant()));
                        await _formatConverter.ConvertAsync(
                            sourcePath,
                            temporaryOutput,
                            new Progress<FormatConversionProgress>(value =>
                                SetTaskStatus(T("正在生成 {0}：{1}（{2}%）", targetFormat.ToUpperInvariant(), book.Title, value.RoundedPercentage))),
                            cancellationToken,
                            new FormatConversionMetadata(book.Title, book.Authors));
                        var addedFile = await _library.AddFileToBookAsync(book.Id, temporaryOutput, cancellationToken);
                        book.Files.Add(addedFile);
                        generatedCount++;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        failures.Add(T("《{0}》生成 {1}：{2}", book.Title, targetFormat.ToUpperInvariant(), UiText.Localize(exception.Message)));
                    }
                    finally
                    {
                        try { if (File.Exists(temporaryOutput)) File.Delete(temporaryOutput); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                    }
                }
            }

            if (generatedCount > 0)
                await ViewModel.RefreshAsync(cancellationToken);
            return new AutomaticReaderFormatGenerationResult(generatedCount, failures);
        }
        finally
        {
            _automaticReaderFormatGenerationInProgress = false;
            try { if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record AutomaticReaderFormatGenerationResult(
        int GeneratedCount,
        IReadOnlyList<string> Failures);

    private void ShowBookConversionPopup(string title, string sourceFormat, string targetFormat, FormatConversionProgress progress)
    {
        BookConversionPopupTitleText.Text = T("转换《{0}》", title);
        BookConversionPopupFormatText.Text = $"Calibre · {sourceFormat.ToUpperInvariant()} → {targetFormat.ToUpperInvariant()}";
        BookConversionPopup.IsVisible = true;
        ApplyBookConversionProgress(progress);
    }

    private void ApplyBookConversionProgress(FormatConversionProgress progress)
    {
        _conversionLastProgress = progress;
        if (!_conversionInProgress) return;
        var percentage = Math.Clamp(progress.Percentage, 0, 100);
        BookConversionPopupProgress.Value = percentage;
        BookConversionPopupPercentageText.Text = $"{progress.RoundedPercentage}%";
        BookConversionPopupMessageText.Text = GetBookConversionPopupMessage(progress);
        _conversionCard?.SetConversionProgress(progress, _conversionMinimized);
        SetTaskStatus(T("格式转换：{0}%", progress.RoundedPercentage));
    }

    private static string GetBookConversionPopupMessage(FormatConversionProgress progress)
    {
        if (progress.Percentage <= 0 || progress.Percentage >= 100)
            return UiText.Localize(progress.Message);
        return T("Calibre 正在转换…");
    }

    private void MinimizeBookConversionPopup()
    {
        if (!_conversionInProgress) return;
        _conversionMinimized = true;
        _conversionCard?.SetConversionProgress(_conversionLastProgress, showIndicator: true);
        BookConversionPopup.IsVisible = false;
        SetTaskStatus(T("格式转换已在后台进行：{0}%（点击书籍卡片可恢复进度）", _conversionLastProgress.RoundedPercentage));
    }

    private void RestoreBookConversionPopup()
    {
        if (!_conversionInProgress) return;
        _conversionMinimized = false;
        _conversionCard?.SetConversionProgress(_conversionLastProgress, showIndicator: false);
        BookConversionPopup.IsVisible = true;
        ApplyBookConversionProgress(_conversionLastProgress);
    }

    private void BookConversionPopup_Tapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Button) return;
        e.Handled = true;
        MinimizeBookConversionPopup();
    }

    private void BookConversionBackgroundButton_Click(object? sender, RoutedEventArgs e) =>
        MinimizeBookConversionPopup();

    private void BookConversionProgressIndicator_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: BookCardViewModel card }) return;
        if (!_conversionInProgress || _conversionCard?.Book.Id != card.Book.Id) return;
        e.Handled = true;
        _conversionCard = card;
        RestoreBookConversionPopup();
    }

    private void BookConversionCancelButton_Click(object? sender, RoutedEventArgs e) =>
        _conversionCancellation?.Cancel();

    private async Task MatchDoubanAsync(BookCardViewModel card)
    {
        if (_doubanMatchCancellation is not null)
        {
            SetTaskStatus(T("豆瓣匹配正在进行中，请稍候。"));
            return;
        }
        if (!_appSettings.NetworkEnabled)
        {
            await ShowMessageAsync(T("网络功能已关闭"), T("请先在应用设置中允许网络功能，再使用豆瓣匹配。"));
            return;
        }

        // Same cleaning as batch matching: import metadata noise (subtitles,
        // bracketed series info, stuffed author strings) makes Douban's strict
        // keyword search miss otherwise obvious hits.
        var searchTitle = CleanDoubanTitle(card.Book.Title);
        var searchAuthor = CleanDoubanAuthor(card.Book.Authors);
        if (string.IsNullOrWhiteSpace(searchTitle)) searchTitle = card.Book.Title.Trim();
        DoubanSearchBox.Text = searchTitle;
        _doubanResearchPending = false;

        var cancellation = new CancellationTokenSource();
        _doubanMatchCancellation = cancellation;
        try
        {
            SetTaskStatus(T("正在搜索《{0}》的豆瓣信息…", searchTitle));
            ShowTaskProgressPopup();
            TaskProgressPopupBar.IsIndeterminate = true;
            TaskProgressPopupText.Text = T("正在搜索《{0}》的豆瓣信息…", searchTitle);
            await SearchAndShowDoubanCandidatesAsync(searchTitle, searchAuthor, cancellation.Token);

            while (true)
            {
                var candidate = await ChooseDoubanCandidateAsync();

                if (_doubanResearchPending)
                {
                    // The user edited the keywords in the overlay; search again
                    // and keep the dialog open with the refreshed list.
                    _doubanResearchPending = false;
                    var query = DoubanSearchBox.Text?.Trim() ?? string.Empty;
                    if (query.Length > 0)
                        await SearchAndShowDoubanCandidatesAsync(query, null, cancellation.Token);
                    continue;
                }
                if (candidate is null)
                {
                    SetTaskStatus(T("已取消豆瓣匹配。"));
                    return;
                }
                _doubanSelectedCandidate = candidate;

                SetTaskStatus(T("正在读取《{0}》的豆瓣详情…", candidate.Title));
                var metadata = await _douban.GetDetailsAsync(candidate, cancellation.Token);
                var choices = await ConfirmDoubanMetadataAsync(metadata, candidate);
                if (choices?.GoBack == true) continue;
                if (choices is null)
                {
                    SetTaskStatus(T("已取消豆瓣匹配。"));
                    return;
                }

                var book = card.Book;
                if (choices.UpdateTitle && !string.IsNullOrWhiteSpace(metadata.Title)) book.Title = metadata.Title.Trim();
                if (choices.UpdateAuthors && !string.IsNullOrWhiteSpace(metadata.Authors)) book.Authors = metadata.Authors.Trim();
                if (choices.UpdateSeries && !string.IsNullOrWhiteSpace(metadata.Series)) book.Series = metadata.Series.Trim();
                if (choices.UpdateDescription && !string.IsNullOrWhiteSpace(metadata.Description)) book.Description = metadata.Description.Trim();
                if (choices.UpdatePublication)
                {
                    if (!string.IsNullOrWhiteSpace(metadata.Publisher)) book.Publisher = metadata.Publisher.Trim();
                    if (!string.IsNullOrWhiteSpace(metadata.PublishDate)) book.PublishDate = metadata.PublishDate.Trim();
                    if (!string.IsNullOrWhiteSpace(metadata.Isbn)) book.Isbn = metadata.Isbn.Trim();
                    if (!string.IsNullOrWhiteSpace(metadata.Pages)) book.PageCount = metadata.Pages.Trim();
                    if (!string.IsNullOrWhiteSpace(metadata.Binding)) book.Binding = metadata.Binding.Trim();
                    if (metadata.Rating is not null) book.DoubanRating = metadata.Rating;
                    book.DoubanRatingCount = metadata.RatingCount;
                }

                if (choices.UpdateCover && !string.IsNullOrWhiteSpace(metadata.CoverUrl))
                {
                    try
                    {
                        SetTaskStatus(T("正在下载并保存豆瓣封面…"));
                        var coverBytes = await _douban.DownloadCoverAsync(metadata.CoverUrl, cancellation.Token);
                        _paths.EnsureDirectories();
                        var coverName = $"{book.Id:N}-douban.jpg";
                        var coverPath = Path.Combine(_paths.Covers, coverName);
                        var temporaryPath = coverPath + ".tmp";
                        await File.WriteAllBytesAsync(temporaryPath, coverBytes, cancellation.Token);
                        File.Move(temporaryPath, coverPath, overwrite: true);
                        book.CoverPath = Path.GetRelativePath(_paths.Data, coverPath);
                    }
                    catch (Exception exception)
                    {
                        SetTaskStatus(T("豆瓣信息已读取，但封面下载失败：{0}", UiText.Localize(exception.Message)));
                    }
                }

                await _library.UpdateMetadataAsync(book, _lifetimeCancellation.Token);
                await RefreshLibraryAsync();
                SetTaskStatus(T("已用豆瓣信息更新《{0}》。", book.Title));
                return;
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            SetTaskStatus(T("豆瓣匹配已取消。"));
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("豆瓣匹配失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("豆瓣匹配失败"), UiText.Localize(exception.Message));
        }
        finally
        {
            TaskProgressPopupBar.IsIndeterminate = false;
            HideTaskProgressPopup();
            DoubanCandidateOverlay.IsVisible = false;
            DoubanPreviewOverlay.IsVisible = false;
            _doubanApplyCompletion?.TrySetResult(null);
            _doubanApplyCompletion = null;
            _doubanPreviewMetadata = null;
            _doubanSelectedCandidate = null;
            DoubanPreviewCoverImage.Source = null;
            DisposeDoubanCandidates();
            if (ReferenceEquals(_doubanMatchCancellation, cancellation)) _doubanMatchCancellation = null;
            cancellation.Dispose();
        }
    }

    // Runs one Douban query and refreshes the candidate overlay (list plus
    // covers). Empty results or failures keep the dialog open with a status
    // message so the keywords can be corrected and searched again; only
    // access-verification blocks bubble up to abort the whole match.
    private async Task SearchAndShowDoubanCandidatesAsync(
        string title,
        string? authors,
        CancellationToken cancellationToken)
    {
        try
        {
            TaskProgressPopupText.Text = T("正在搜索豆瓣“{0}”…", title);
            SetTaskStatus(T("正在搜索豆瓣“{0}”…", title));
            var candidates = await _douban.SearchAsync(title, authors, cancellationToken);
            if (candidates.Count == 0 && !string.IsNullOrWhiteSpace(authors))
            {
                // Dirty author strings ("【日】伊坂幸太郎", translators mixed
                // in) poison the query; retry with the title alone.
                candidates = await _douban.SearchAsync(title, null, cancellationToken);
            }
            DisposeDoubanCandidates();
            foreach (var item in candidates)
                DoubanCandidates.Add(new DoubanCandidateViewModel(item));
            if (candidates.Count > 0)
            {
                TaskProgressPopupText.Text = T("正在加载豆瓣候选封面…");
                SetTaskStatus(T("正在加载豆瓣候选封面…"));
                await LoadDoubanCandidateCoversAsync(cancellationToken);
                SetTaskStatus(T("豆瓣返回 {0} 条结果，请选择匹配条目。", candidates.Count));
            }
            else
            {
                SetTaskStatus(T("豆瓣没有返回“{0}”的结果，可修改关键词后重新搜索。", title));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidOperationException exception)
            when (exception.Message.Contains("访问验证") || exception.Message.Contains("限制了访问"))
        {
            throw;
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("豆瓣搜索失败：{0}", UiText.Localize(exception.Message)));
        }
    }

    private void DoubanResearchButton_Click(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(DoubanSearchBox.Text)) return;
        _doubanResearchPending = true;
        _doubanCandidateCompletion?.TrySetResult(null);
    }

    private void DoubanSearchBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not Key.Enter) return;
        e.Handled = true;
        DoubanResearchButton_Click(sender, e);
    }

    private Task<DoubanBookCandidate?> ChooseDoubanCandidateAsync()
    {
        DoubanPreviewOverlay.IsVisible = false;
        DoubanCandidateList.SelectedIndex = DoubanCandidates.Count > 0 ? 0 : -1;
        SetDoubanCandidateButtonsEnabled(DoubanCandidateList.SelectedItem is DoubanCandidateViewModel);
        ShowOverlay(DoubanCandidateOverlay);
        _doubanCandidateCompletion?.TrySetResult(null);
        _doubanCandidateCompletion = new TaskCompletionSource<DoubanBookCandidate?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return _doubanCandidateCompletion.Task;
    }

    private Task<DoubanUpdateChoices?> ConfirmDoubanMetadataAsync(
        DoubanBookMetadata metadata,
        DoubanBookCandidate candidate)
    {
        _doubanPreviewMetadata = metadata;
        DoubanCandidateOverlay.IsVisible = false;
        DoubanPreviewSummaryText.Text = BuildDoubanSummary(metadata);
        DoubanPreviewStatusText.Text = T("未勾选的本地字段不会被修改");
        DoubanPreviewCoverImage.Source = DoubanCandidates
            .FirstOrDefault(item => item.Candidate.SubjectId == candidate.SubjectId)
            ?.CoverImage;

        DoubanUpdateTitleCheck.IsChecked = !string.IsNullOrWhiteSpace(metadata.Title);
        DoubanUpdateAuthorsCheck.IsChecked = !string.IsNullOrWhiteSpace(metadata.Authors);
        DoubanUpdateSeriesCheck.IsChecked = !string.IsNullOrWhiteSpace(metadata.Series);
        DoubanUpdateSeriesCheck.IsEnabled = !string.IsNullOrWhiteSpace(metadata.Series);
        DoubanUpdateDescriptionCheck.IsChecked = !string.IsNullOrWhiteSpace(metadata.Description);
        DoubanUpdateDescriptionCheck.IsEnabled = !string.IsNullOrWhiteSpace(metadata.Description);
        DoubanUpdateCoverCheck.IsChecked = !string.IsNullOrWhiteSpace(metadata.CoverUrl);
        DoubanUpdateCoverCheck.IsEnabled = !string.IsNullOrWhiteSpace(metadata.CoverUrl);
        var hasPublicationData = !string.IsNullOrWhiteSpace(metadata.Publisher)
            || !string.IsNullOrWhiteSpace(metadata.PublishDate)
            || !string.IsNullOrWhiteSpace(metadata.Isbn)
            || !string.IsNullOrWhiteSpace(metadata.Pages)
            || !string.IsNullOrWhiteSpace(metadata.Binding)
            || metadata.Rating is not null;
        DoubanUpdatePublicationCheck.IsChecked = hasPublicationData;
        DoubanUpdatePublicationCheck.IsEnabled = hasPublicationData;

        ShowOverlay(DoubanPreviewOverlay);
        DoubanPreviewOverlay.Focus();

        // Candidate covers are decorative; a failure must never block the
        // metadata confirmation flow.
        if (DoubanPreviewCoverImage.Source is null
            && !string.IsNullOrWhiteSpace(candidate.CoverUrl)
            && _doubanMatchCancellation is { } cancellation)
        {
            _ = LoadDoubanPreviewCoverAsync(candidate.CoverUrl, cancellation.Token);
        }

        _doubanApplyCompletion?.TrySetResult(null);
        _doubanApplyCompletion = new TaskCompletionSource<DoubanUpdateChoices?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return _doubanApplyCompletion.Task;
    }

    private async Task LoadDoubanPreviewCoverAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await _douban.DownloadCoverAsync(url, cancellationToken);
            if (bytes.Length == 0) return;
            await using var stream = new MemoryStream(bytes, writable: false);
            var bitmap = new Bitmap(stream);
            if (!ReferenceEquals(_doubanPreviewMetadata, null))
                DoubanPreviewCoverImage.Source = bitmap;
            else
                bitmap.Dispose();
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            // Covers are decorative; never fail the preview for a bad image.
        }
    }

    private async Task LoadDoubanCandidateCoversAsync(CancellationToken cancellationToken)
    {
        var downloads = DoubanCandidates.Select(async item =>
        {
            if (string.IsNullOrWhiteSpace(item.Candidate.CoverUrl))
                return (Item: item, Bytes: (byte[]?)null);
            try
            {
                var bytes = await _douban.DownloadCoverAsync(item.Candidate.CoverUrl, cancellationToken);
                return (Item: item, Bytes: (byte[]?)bytes);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return (Item: item, Bytes: (byte[]?)null);
            }
        });

        var covers = await Task.WhenAll(downloads);
        foreach (var (item, bytes) in covers)
        {
            if (bytes is null || bytes.Length == 0) continue;
            await using var stream = new MemoryStream(bytes, writable: false);
            item.CoverImage = new Bitmap(stream);
        }
    }

    private void DisposeDoubanCandidates()
    {
        foreach (var item in DoubanCandidates) item.Dispose();
        DoubanCandidates.Clear();
    }

    private static string BuildDoubanSummary(DoubanBookMetadata metadata)
    {
        static string Fallback(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value.Trim();
        var rows = new List<string>
        {
            T("书名：{0}", metadata.Title),
            T("作者：{0}", Fallback(metadata.Authors)),
            T("译者：{0}", Fallback(metadata.Translators)),
            T("出版社：{0}", Fallback(metadata.Publisher)),
            T("出版年：{0}", Fallback(metadata.PublishDate)),
            T("ISBN：{0}", Fallback(metadata.Isbn)),
            T("页数 / 装帧：{0} / {1}", Fallback(metadata.Pages), Fallback(metadata.Binding)),
            T("定价：{0}", Fallback(metadata.Price)),
            T("系列：{0}", Fallback(metadata.Series)),
            metadata.Rating is null ? T("豆瓣评分：暂无") : T("豆瓣评分：{0:0.0}（{1} 人评价）", metadata.Rating, metadata.RatingCount),
            string.Empty,
            T("简介：{0}", Fallback(metadata.Description))
        };
        return string.Join(Environment.NewLine, rows);
    }

    private void DoubanCandidateList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DoubanCandidateList.SelectedItem is DoubanCandidateViewModel item)
        {
            _doubanSelectedCandidate = item.Candidate;
            SetDoubanCandidateButtonsEnabled(true);
        }
        else
        {
            SetDoubanCandidateButtonsEnabled(false);
        }
    }

    private void SetDoubanCandidateButtonsEnabled(bool enabled)
    {
        DoubanCandidateApplyButton.IsEnabled = enabled;
        DoubanCandidateSourceButton.IsEnabled = enabled;
    }

    private void DoubanCandidateList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (DoubanCandidateList.SelectedItem is DoubanCandidateViewModel)
            DoubanCandidateApplyButton_Click(sender, new RoutedEventArgs());
    }

    private void DoubanCandidateSourceButton_Click(object? sender, RoutedEventArgs e)
    {
        if (DoubanCandidateList.SelectedItem is not DoubanCandidateViewModel item) return;
        OpenDoubanUrl(item.Candidate.Url);
    }

    private void DoubanPreviewSourceButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_doubanPreviewMetadata is null) return;
        OpenDoubanUrl(_doubanPreviewMetadata.Url);
    }

    private void OpenDoubanUrl(string? url)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(url))
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("无法打开豆瓣详情页：{0}", UiText.Localize(exception.Message)));
        }
    }

    private void DoubanCandidateApplyButton_Click(object? sender, RoutedEventArgs e)
    {
        var candidate = (DoubanCandidateList.SelectedItem as DoubanCandidateViewModel)?.Candidate;
        DoubanCandidateOverlay.IsVisible = false;
        _doubanCandidateCompletion?.TrySetResult(candidate);
        _doubanCandidateCompletion = null;
    }

    private void DoubanCandidateCancelButton_Click(object? sender, RoutedEventArgs e)
    {
        DoubanCandidateOverlay.IsVisible = false;
        DoubanPreviewOverlay.IsVisible = false;
        _doubanResearchPending = false;
        _doubanCandidateCompletion?.TrySetResult(null);
        _doubanApplyCompletion?.TrySetResult(null);
        _doubanCandidateCompletion = null;
        _doubanApplyCompletion = null;
        _doubanMatchCancellation?.Cancel();
    }

    private void DoubanPreviewApplyButton_Click(object? sender, RoutedEventArgs e)
    {
        DoubanPreviewOverlay.IsVisible = false;
        _doubanApplyCompletion?.TrySetResult(new DoubanUpdateChoices(
            GoBack: false,
            UpdateTitle: DoubanUpdateTitleCheck.IsChecked == true,
            UpdateAuthors: DoubanUpdateAuthorsCheck.IsChecked == true,
            UpdateSeries: DoubanUpdateSeriesCheck.IsChecked == true,
            UpdateDescription: DoubanUpdateDescriptionCheck.IsChecked == true,
            UpdateCover: DoubanUpdateCoverCheck.IsChecked == true,
            UpdatePublication: DoubanUpdatePublicationCheck.IsChecked == true));
        _doubanApplyCompletion = null;
    }

    private void DoubanPreviewBackButton_Click(object? sender, RoutedEventArgs e)
    {
        DoubanPreviewOverlay.IsVisible = false;
        _doubanApplyCompletion?.TrySetResult(new DoubanUpdateChoices(
            GoBack: true,
            UpdateTitle: false,
            UpdateAuthors: false,
            UpdateSeries: false,
            UpdateDescription: false,
            UpdateCover: false,
            UpdatePublication: false));
        _doubanApplyCompletion = null;
    }

    private void DoubanPreviewOverlay_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        DoubanCandidateCancelButton_Click(sender, new RoutedEventArgs());
    }

    private sealed record DoubanUpdateChoices(
        bool GoBack,
        bool UpdateTitle,
        bool UpdateAuthors,
        bool UpdateSeries,
        bool UpdateDescription,
        bool UpdateCover,
        bool UpdatePublication);

    private async Task<bool> ConfirmAsync(string title, string message, string? primaryText = null)
    {
        if (Environment.GetEnvironmentVariable("KKINDLE_SEND_DIAG") == "1") return true;
        if (_confirmationCompletion is not null) return false;
        ConfirmationTitleText.Text = title;
        ConfirmationMessageText.Text = message;
        ConfirmationOkButton.Content = primaryText
            ?? (title.Contains(T("删除"), StringComparison.Ordinal) ? T("确认删除") : T("应用"));
        ShowOverlay(ConfirmationOverlay);
        _confirmationCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = _confirmationCompletion;
        var confirmed = await completion.Task;
        if (ReferenceEquals(_confirmationCompletion, completion))
            _confirmationCompletion = null;
        return confirmed;
    }

    // Fade-in helpers: the overlays and pages carry an Opacity transition in
    // XAML; showing them at 0 and restoring 1 on the next frame animates the
    // entrance instead of popping it in.
    private static void ShowOverlay(Control overlay)
    {
        overlay.IsVisible = true;
        overlay.Opacity = 0;
        Dispatcher.UIThread.Post(() => overlay.Opacity = 1);
    }

    private static void FadeInPage(Control page)
    {
        page.IsVisible = true;
        page.Opacity = 0;
        Dispatcher.UIThread.Post(() => page.Opacity = 1);
    }

    // Monochrome information dialog (WinUI ShowMessageAsync). Fire-and-forget
    // callers can use "_ = ShowMessageAsync(...)"; the awaited task completes
    // when the user dismisses the dialog.
    private Task ShowMessageAsync(string title, string message, string? actionText = null)
    {
        MessageTitleText.Text = UiText.Localize(title);
        MessageBodyText.Text = UiText.Localize(message);
        MessageOkButton.Content = actionText ?? T("知道了");
        ShowOverlay(MessageOverlay);
        MessageOverlay.Focus();
        _messageCompletion?.TrySetResult(true);
        _messageCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _messageCompletion.Task;
    }

    private void MessageOkButton_Click(object? sender, RoutedEventArgs e) => CompleteMessage();

    private void MessageOverlay_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Escape or Key.Enter)) return;
        e.Handled = true;
        CompleteMessage();
    }

    private void CompleteMessage()
    {
        MessageOverlay.IsVisible = false;
        var completion = _messageCompletion;
        _messageCompletion = null;
        completion?.TrySetResult(true);
    }

    private Task<string?> PromptCollectionNameAsync()
    {
        if (_collectionNameCompletion is not null) return Task.FromResult<string?>(null);
        CollectionNameBox.Text = string.Empty;
        ShowOverlay(CollectionNameOverlay);
        CollectionNameBox.Focus();
        _collectionNameCompletion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        return _collectionNameCompletion.Task;
    }

    private void CompleteConfirmation(bool confirmed)
    {
        ConfirmationOverlay.IsVisible = false;
        _confirmationCompletion?.TrySetResult(confirmed);
    }

    private void CompleteCollectionName(string? name)
    {
        CollectionNameOverlay.IsVisible = false;
        var completion = _collectionNameCompletion;
        _collectionNameCompletion = null;
        completion?.TrySetResult(name);
    }

    private async Task CreateCollectionAsync()
    {
        var name = await PromptCollectionNameAsync();
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            await _library.CreateCollectionAsync(name, _lifetimeCancellation.Token);
            await RefreshCollectionsAsync();
            SetLibraryViewMode(LibraryViewMode.Collections);
            SetTaskStatus(T("已创建收藏夹“{0}”。", name.Trim()));
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("创建收藏夹失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法创建收藏夹"), UiText.Localize(exception.Message));
        }
    }

    private async Task DeleteCollectionAsync(BookCollectionFolderViewModel folder)
    {
        if (!await ConfirmAsync(T("删除收藏夹"), T("确定删除收藏夹“{0}”吗？书籍文件不会被删除。", folder.Name))) return;

        try
        {
            await _library.DeleteCollectionAsync(folder.Collection.Id, _lifetimeCancellation.Token);
            if (ViewModel.CollectionFilterId == folder.Collection.Id)
            {
                ViewModel.CollectionFilterId = null;
                ViewModel.CollectionFilterName = null;
            }
            await RefreshLibraryAsync();
            SetLibraryViewMode(LibraryViewMode.Collections);
            SetTaskStatus(T("已删除收藏夹“{0}”。", folder.Name));
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("删除收藏夹失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法删除收藏夹"), UiText.Localize(exception.Message));
        }
    }

    private async Task ToggleCollectionMembershipAsync()
    {
        if (_selectedCard is null) return;
        if (DetailCollectionBox.SelectedItem is not BookCollectionFolderViewModel folder)
        {
            SetTaskStatus(T("请先选择一个收藏夹。"));
            return;
        }

        try
        {
            var bookId = _selectedCard.Book.Id;
            if (_selectedCard.Book.CollectionIds.Contains(folder.Collection.Id))
            {
                await _library.RemoveBookFromCollectionAsync(bookId, folder.Collection.Id, _lifetimeCancellation.Token);
                SetTaskStatus(T("已从“{0}”移出。 ", folder.Name));
            }
            else
            {
                await _library.AddBookToCollectionAsync(bookId, folder.Collection.Id, _lifetimeCancellation.Token);
                SetTaskStatus(T("已加入“{0}”。", folder.Name));
            }

            await RefreshLibraryAsync();
        }
        catch (Exception exception)
        {
            SetTaskStatus(T("更新收藏夹失败：{0}", UiText.Localize(exception.Message)));
            await ShowMessageAsync(T("无法更新收藏夹"), UiText.Localize(exception.Message));
        }
    }

    private void ShowAllBooksButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ShowLibraryPage();
        ViewModel.CollectionFilterId = null;
        ViewModel.CollectionFilterName = null;
        ViewModel.RefreshView();
        SetLibraryViewMode(LibraryViewMode.Grid);
    }

    private void SearchBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        ViewModel.SearchText = SearchBox.Text ?? string.Empty;
        ViewModel.RefreshView();
        UpdateLibraryUi();
    }

    private void FilterButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        FilterPanel.IsVisible = !FilterPanel.IsVisible;
    }

    private void LibraryViewToggleButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var next = NextLibraryViewMode(_libraryViewMode);
        if (next == LibraryViewMode.Collections && ViewModel.CollectionFilterId is not null)
        {
            ViewModel.CollectionFilterId = null;
            ViewModel.CollectionFilterName = null;
            ViewModel.RefreshView();
        }
        SetLibraryViewMode(next);
    }

    private void BackToCollectionsButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        ViewModel.CollectionFilterId = null;
        ViewModel.CollectionFilterName = null;
        ViewModel.RefreshView();
        SetLibraryViewMode(LibraryViewMode.Collections);
    }

    private async void CreateCollectionButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await CreateCollectionAsync();

    private void ImportButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var importFiles = new MenuItem { Header = T("导入文件") };
        importFiles.Click += ImportFilesButton_Click;
        menu.Items.Add(importFiles);

        var importFolder = new MenuItem { Header = T("导入文件夹") };
        importFolder.Click += ImportFolderButton_Click;
        menu.Items.Add(importFolder);

        menu.Open(ImportButton);
    }

    private async void ImportFilesButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("导入书籍文件"),
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType(T("电子书"))
                {
                    Patterns = ["*.epub", "*.pdf", "*.mobi", "*.azw3"]
                }
            ]
        });
        await ImportPathsAsync(files
            .Select(file => file.TryGetLocalPath())
            .Where(path => path is not null)
            .Select(path => path!));
    }

    private async void ImportFolderButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null) return;

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = T("导入书籍文件夹"),
            AllowMultiple = false
        });
        await ImportPathsAsync(folders
            .Select(folder => folder.TryGetLocalPath())
            .Where(path => path is not null)
            .Select(path => path!));
    }

    private void FilterComboBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_filterControlsReady || _updatingFilterControls) return;
        ViewModel.AuthorFilter = AuthorFilterBox.SelectedIndex < 0 ? null : AuthorFilterBox.SelectedItem as string;
        ViewModel.TagFilter = TagFilterBox.SelectedIndex < 0 ? null : TagFilterBox.SelectedItem as string;
        ViewModel.FormatFilter = FormatFilterBox.SelectedIndex < 0 ? null : FormatFilterBox.SelectedItem as string;
        ViewModel.CategoryFilter = CategoryFilterBox.SelectedIndex < 0 ? null : CategoryFilterBox.SelectedItem as string;
        ViewModel.ReadingStatusFilter = ReadingStatusFilterBox.SelectedIndex < 0
            ? null
            : (LibraryReadingStatus)ReadingStatusFilterBox.SelectedIndex;
        ViewModel.SortMode = LibrarySortBox.SelectedIndex < 0
            ? LibrarySortMode.UpdatedDescending
            : (LibrarySortMode)LibrarySortBox.SelectedIndex;
        ViewModel.RefreshView();
        UpdateLibraryUi();
    }

    private void FavoritesOnlyCheckBox_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!_filterControlsReady || _updatingFilterControls) return;
        ViewModel.FavoritesOnly = FavoritesOnlyCheckBox.IsChecked == true;
        ViewModel.RefreshView();
        UpdateLibraryUi();
    }

    private void ClearFiltersButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _updatingFilterControls = true;
        try
        {
            ViewModel.SearchText = string.Empty;
            ViewModel.AuthorFilter = null;
            ViewModel.TagFilter = null;
            ViewModel.FormatFilter = null;
            ViewModel.CategoryFilter = null;
            ViewModel.ReadingStatusFilter = null;
            ViewModel.FavoritesOnly = false;
            ViewModel.CollectionFilterId = null;
            ViewModel.CollectionFilterName = null;
            ViewModel.SortMode = LibrarySortMode.UpdatedDescending;
            SearchBox.Text = string.Empty;
            AuthorFilterBox.SelectedIndex = -1;
            TagFilterBox.SelectedIndex = -1;
            FormatFilterBox.SelectedIndex = -1;
            CategoryFilterBox.SelectedIndex = -1;
            ReadingStatusFilterBox.SelectedIndex = -1;
            LibrarySortBox.SelectedIndex = 0;
            FavoritesOnlyCheckBox.IsChecked = false;
            ViewModel.RefreshView();
            SetLibraryViewMode(LibraryViewMode.Grid);
        }
        finally
        {
            _updatingFilterControls = false;
        }
    }

    private void BookList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        foreach (var card in e.RemovedItems.OfType<BookCardViewModel>())
            _selectedBookIds.Remove(card.Book.Id);
        foreach (var card in e.AddedItems.OfType<BookCardViewModel>())
            _selectedBookIds.Add(card.Book.Id);
        var selectedCard = e.AddedItems.OfType<BookCardViewModel>().FirstOrDefault();
        if (selectedCard is not null)
        {
            if (_suppressSelectionPane || _suppressNextSelectionPane)
                _suppressNextSelectionPane = false;
            else
                SelectBook(selectedCard);
        }
        UpdateMultiSelectionUi();
    }

    private void BookCard_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not BookCardViewModel card)
            return;
        var pointerProperties = e.GetCurrentPoint(control).Properties;
        // 右键按下会先触发 ListBox 的原生选中（早于 ContextRequested），
        // 标记并抑制那一次 SelectionChanged 里的弹详情页。
        _suppressNextSelectionPane = pointerProperties.IsRightButtonPressed;
        if (pointerProperties.IsRightButtonPressed)
            CancelPendingBookDetailClick();
        if (!pointerProperties.IsLeftButtonPressed)
            return;

        if (e.ClickCount >= 2 || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0)
            CancelPendingBookDetailClick();

        // 记录框选起点：网格左侧没有空白（卡片从视口左缘开始），从左往右
        // 框选必须允许起点落在卡片上，拖动超过阈值后同样进入框选。
        _rubberBandStart = e.GetPosition(BookGrid);
        _rubberBandCurrent = _rubberBandStart;
        _rubberBandSelecting = false;
        _rubberBandPointerSequenceHandled = false;
        _rubberBandPressedOnCard = true;
        _rubberBandGestureActive = true;
        // Keep the initial gesture on the card so ListBoxItem/ScrollViewer
        // cannot steal movement before the drag crosses the threshold.
        e.Pointer.Capture(control);

        if ((e.KeyModifiers & KeyModifiers.Control) != 0)
        {
            if (_selectedBookIds.Remove(card.Book.Id))
                card.IsMultiSelected = false;
            else
                _selectedBookIds.Add(card.Book.Id);
            _selectedCard = card;
            _multiSelectAnchor = card;
        }
        else if ((e.KeyModifiers & KeyModifiers.Shift) != 0)
        {
            ApplyCardRangeSelection(card);
        }
        else
        {
            _selectedBookIds.Clear();
            _selectedBookIds.Add(card.Book.Id);
            _selectedCard = card;
            _multiSelectAnchor = card;
            if (e.ClickCount == 1)
                ScheduleBookDetailClick(card, e.Pointer.Type);
        }

        UpdateMultiSelectionUi();
        e.Handled = true;
    }

    // 悬停整张卡片（封面或文字区）时显示黑色细边框，与选中态一致。
    private void BookCard_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: BookCardViewModel card })
            card.IsHovered = true;
    }

    private void BookCard_PointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Control { DataContext: BookCardViewModel card })
            card.IsHovered = false;
    }

    private void BookGrid_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!BookGrid.IsVisible
            || !e.GetCurrentPoint(BookGrid).Properties.IsLeftButtonPressed)
            return;

        // handledEventsToo lets this handler observe presses already owned by
        // the embedded ScrollViewer. Never steal the pointer from its ScrollBar
        // thumb, and ignore the rest of that pointer sequence as rubber-band UI.
        if (IsScrollBarSource(e.Source))
        {
            _rubberBandGestureActive = false;
            _rubberBandPressedOnCard = false;
            _rubberBandSelecting = false;
            return;
        }

        // 按下发生在书籍卡片上时由 BookCard_PointerPressed 处理点选/多选，
        // 松开时不得再清空选择（否则点选的黑边在松开瞬间被抹掉）。
        _rubberBandPressedOnCard = IsBookCardSource(e.Source);
        if (_rubberBandPressedOnCard) return;

        _rubberBandStart = e.GetPosition(BookGrid);
        _rubberBandCurrent = _rubberBandStart;
        _rubberBandSelecting = false;
        _rubberBandPointerSequenceHandled = false;
        _rubberBandGestureActive = true;
        e.Pointer.Capture(BookGrid);
        BookGrid.Focus();
    }

    private void BookGrid_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_rubberBandGestureActive
            || !e.GetCurrentPoint(BookGrid).Properties.IsLeftButtonPressed)
            return;
        _rubberBandCurrent = e.GetPosition(BookGrid);
        if (!_rubberBandSelecting)
        {
            // 框选支持任意方向：任一轴拖拽超过阈值就启动，避免点选时
            // 轻微晃动被误识别成多选；对角线拖动不需要额外的距离。
            var deltaX = _rubberBandCurrent.X - _rubberBandStart.X;
            var deltaY = _rubberBandCurrent.Y - _rubberBandStart.Y;
            if (Math.Max(Math.Abs(deltaX), Math.Abs(deltaY)) < RubberBandDragThreshold)
                return;
            CancelPendingBookDetailClick();
            _rubberBandSelecting = true;
            e.Pointer.Capture(BookGrid);
            RubberBandRectangle.IsVisible = true;
            // 框选从卡片上起步时，按下那一刻已弹出详情页；进入框选即收起，
            // 避免面板挡住右侧被圈选的书籍。
            if (_rubberBandPressedOnCard && LibraryDetailPane.IsVisible)
                ClearSelectedBook();
        }
        UpdateRubberBandSelection();
        e.Handled = true;
    }

    private void BookGrid_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_rubberBandGestureActive) return;
        _rubberBandGestureActive = false;
        if (_rubberBandPointerSequenceHandled) return;
        _rubberBandPointerSequenceHandled = true;
        if (!_rubberBandSelecting)
        {
            // 按在卡片上松开：点选结果已由 BookCard_PointerPressed 维护。
            // 按在空白处松开：取消全部选择。
            if (!_rubberBandPressedOnCard)
            {
                _selectedBookIds.Clear();
                _multiSelectAnchor = null;
                UpdateMultiSelectionUi();
            }
            _rubberBandPressedOnCard = false;
            return;
        }
        _rubberBandCurrent = e.GetPosition(BookGrid);
        UpdateRubberBandSelection();
        FinishRubberBandSelection(e.Pointer);
        _rubberBandPressedOnCard = false;
        e.Handled = true;
    }

    private void BookGrid_PointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (ReferenceEquals(e.Pointer.Captured, BookGrid))
            return;
        if (_rubberBandSelecting)
            FinishRubberBandSelection(null);
        _rubberBandGestureActive = false;
        _rubberBandPressedOnCard = false;
    }

    private void BookGrid_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        _selectedBookIds.Clear();
        _multiSelectAnchor = null;
        BookGrid.SelectedItems?.Clear();
        UpdateMultiSelectionUi();
    }

    private void UpdateRubberBandSelection()
    {
        var left = Math.Min(_rubberBandStart.X, _rubberBandCurrent.X);
        var top = Math.Min(_rubberBandStart.Y, _rubberBandCurrent.Y);
        var width = Math.Abs(_rubberBandCurrent.X - _rubberBandStart.X);
        var height = Math.Abs(_rubberBandCurrent.Y - _rubberBandStart.Y);
        Canvas.SetLeft(RubberBandRectangle, left);
        Canvas.SetTop(RubberBandRectangle, top);
        RubberBandRectangle.Width = width;
        RubberBandRectangle.Height = height;

        var selection = new Rect(left, top, width, height);
        _selectedBookIds.Clear();
        foreach (var card in ViewModel.Books)
        {
            if (BookGrid.ContainerFromItem(card) is not Control container) continue;
            var origin = container.TranslatePoint(default, BookGrid);
            if (origin is not { } point) continue;
            var bounds = new Rect(point, container.Bounds.Size);
            if (bounds.Intersects(selection)) _selectedBookIds.Add(card.Book.Id);
        }
        _multiSelectAnchor = ViewModel.Books.FirstOrDefault(card => _selectedBookIds.Contains(card.Book.Id));
        UpdateMultiSelectionUi();
    }

    private void FinishRubberBandSelection(IPointer? pointer)
    {
        _rubberBandSelecting = false;
        _rubberBandPointerSequenceHandled = true;
        pointer?.Capture(null);
        RubberBandRectangle.IsVisible = false;
        UpdateMultiSelectionUi();
    }

    private void ApplyCardRangeSelection(BookCardViewModel clicked)
    {
        var cards = ViewModel.Books.ToList();
        var clickedIndex = cards.FindIndex(card => ReferenceEquals(card, clicked));
        if (clickedIndex < 0) return;

        var anchorIndex = _multiSelectAnchor is null
            ? -1
            : cards.FindIndex(card => ReferenceEquals(card, _multiSelectAnchor));
        _selectedBookIds.Clear();

        var start = anchorIndex < 0 ? clickedIndex : Math.Min(anchorIndex, clickedIndex);
        var end = anchorIndex < 0 ? clickedIndex : Math.Max(anchorIndex, clickedIndex);
        for (var index = start; index <= end; index++)
            _selectedBookIds.Add(cards[index].Book.Id);

        _selectedCard = clicked;
        _multiSelectAnchor = clicked;
        SelectBook(clicked);
    }

    private void ScheduleBookDetailClick(BookCardViewModel card, PointerType pointerType)
    {
        CancelPendingBookDetailClick();
        _pendingBookDetailCard = card;
        var interval = this.GetPlatformSettings()?.GetDoubleTapTime(pointerType)
            ?? TimeSpan.FromMilliseconds(500);
        if (interval <= TimeSpan.Zero)
            interval = TimeSpan.FromMilliseconds(500);

        _bookDetailClickTimer = new DispatcherTimer { Interval = interval };
        _bookDetailClickTimer.Tick += BookDetailClickTimer_Tick;
        _bookDetailClickTimer.Start();
    }

    private void BookDetailClickTimer_Tick(object? sender, EventArgs e)
    {
        var card = _pendingBookDetailCard;
        CancelPendingBookDetailClick();
        if (card is null
            || !LibraryRoot.IsVisible
            || !ReferenceEquals(_selectedCard, card)
            || !_selectedBookIds.Contains(card.Book.Id))
            return;

        SelectBook(card);
    }

    private void CancelPendingBookDetailClick()
    {
        if (_bookDetailClickTimer is not null)
        {
            _bookDetailClickTimer.Stop();
            _bookDetailClickTimer.Tick -= BookDetailClickTimer_Tick;
            _bookDetailClickTimer = null;
        }
        _pendingBookDetailCard = null;
    }

    private async void BookCard_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not BookCardViewModel card) return;
        CancelPendingBookDetailClick();
        _selectedBookIds.Clear();
        _selectedBookIds.Add(card.Book.Id);
        _selectedCard = card;
        _multiSelectAnchor = card;
        UpdateMultiSelectionUi();
        e.Handled = true;
        await OpenBookAsync(card);
    }

    private void CollectionFolder_Tapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { DataContext: BookCollectionFolderViewModel folder }) return;
        ViewModel.CollectionFilterId = folder.Collection.Id;
        ViewModel.CollectionFilterName = folder.Collection.Name;
        ViewModel.RefreshView();
        SetLibraryViewMode(LibraryViewMode.Grid);
        e.Handled = true;
    }

    // Right-clicking a collection card opens the delete action (WinUI
    // reference); right-clicking empty space in the collections view offers
    // the create action.
    private void CollectionFolder_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (sender is not Control { DataContext: BookCollectionFolderViewModel folder } control) return;
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem(T("删除收藏夹"), () => DeleteCollectionAsync(folder)));
        menu.Open(control);
        e.Handled = true;
    }

    private void CollectionScroll_ContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is Control { DataContext: BookCollectionFolderViewModel })
            return;
        if (sender is not Control source) return;
        var menu = new ContextMenu();
        menu.Items.Add(CreateMenuItem(T("创建收藏夹"), CreateCollectionAsync));
        menu.Open(source);
        e.Handled = true;
    }

    private async void OpenSelectedBookButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedCard is not null)
            await OpenBookAsync(_selectedCard);
    }

    private async void DeleteSelectedBookButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await DeleteSelectedBookAsync();

    private async void DeleteSelectedBooksButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => await DeleteSelectedBooksAsync();

    private void ClearMultiSelectionButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _selectedBookIds.Clear();
        _multiSelectAnchor = null;
        BookGrid.SelectedItems?.Clear();
        BookList.SelectedItems?.Clear();
        UpdateMultiSelectionUi();
    }

    private async void OpenFileButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedCard is not null && (sender as Button)?.Tag is BookFile file)
            await OpenBookAsync(_selectedCard, file);
    }

    private async void DeleteFileButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is BookFile file)
            await DeleteFileAsync(file);
    }

    private async void CollectionMembershipButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (!_updatingDetails)
            await ToggleCollectionMembershipAsync();
    }

    private async void DetailFavoriteButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedCard is not null)
            await ToggleFavoriteAsync(_selectedCard);
    }

    private async void DetailReadingStatusButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedCard is null) return;
        var nextStatus = _selectedCard.Book.ReadingStatus switch
        {
            LibraryReadingStatus.Unread => LibraryReadingStatus.Reading,
            LibraryReadingStatus.Reading => LibraryReadingStatus.Finished,
            _ => LibraryReadingStatus.Unread
        };
        await UpdateReadingStatusAsync(_selectedCard, nextStatus);
    }

    private async void SaveDetailsButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedCard is null) return;
        var book = _selectedCard.Book;
        book.Title = string.IsNullOrWhiteSpace(DetailTitleText.Text) ? book.Title : DetailTitleText.Text.Trim();
        book.Authors = string.IsNullOrWhiteSpace(DetailAuthorsText.Text) ? book.Authors : DetailAuthorsText.Text.Trim();
        book.Tags = DetailTagsBox.Text?.Trim() ?? string.Empty;
        book.Category = DetailCategoryBox.Text?.Trim() ?? string.Empty;
        book.Description = DetailDescriptionBox.Text?.Trim();
        book.Series = DetailSeriesBox.Text?.Trim();
        book.Publisher = DetailPublisherBox.Text?.Trim();
        book.PublishDate = DetailPublishDateBox.Text?.Trim();
        book.Isbn = DetailIsbnBox.Text?.Trim();
        book.PageCount = DetailPageCountBox.Text?.Trim();
        book.Binding = DetailBindingBox.Text?.Trim();
        await SaveBookMetadataAsync(_selectedCard, T("书籍信息已保存。"));
    }

    private async void DoubanMatchButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_selectedCard is not null)
            await MatchDoubanAsync(_selectedCard);
    }

    private void ConfirmationCancelButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => CompleteConfirmation(false);

    private void ConfirmationOkButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => CompleteConfirmation(true);

    private void CollectionNameCancelButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => CompleteCollectionName(null);

    private async void CollectionNameOkButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var name = CollectionNameBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            SetTaskStatus(T("收藏夹名称不能为空。"));
            await ShowMessageAsync(T("名称不能为空"), T("请输入收藏夹名称。"));
            return;
        }
        CompleteCollectionName(name);
    }

    private void TitleBarDragRegion_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (e.ClickCount == 2)
        {
            ToggleMaximized();
            return;
        }
        BeginMoveDrag(e);
    }

    private void MinimizeWindowButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeWindowButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => ToggleMaximized();

    // While Kreader is open the caption X acts as "close the reader" and
    // returns to the main interface (same default as the reader's 返回书架
    // button); only a second click on the library exits the application.
    private async void CloseWindowButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ReaderRoot.IsVisible)
        {
            await CloseReaderAsync();
            return;
        }
        Close();
    }

    private void ToggleMaximized()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateMaximizeGlyph()
    {
        var isMaximized = WindowState == WindowState.Maximized;
        MaximizeWindowGlyph.Data = Geometry.Parse(isMaximized ? RestoreGlyphData : MaximizeGlyphData);
        AutomationProperties.SetName(MaximizeWindowButton, isMaximized ? T("还原") : T("最大化"));
    }

    private void LibraryRoot_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (LibraryRoot.ColumnDefinitions.Count < 3) return;
        // Book details overlay the library workspace; the third column remains
        // available only for full-page settings surfaces.
        SetGridColumnWidth(LibraryRoot.ColumnDefinitions[0], new GridLength(200));
        if (_settingsPanelVisible)
        {
            SetGridColumnWidth(LibraryRoot.ColumnDefinitions[1], new GridLength(0));
            SetGridColumnWidth(LibraryRoot.ColumnDefinitions[2], new GridLength(1, GridUnitType.Star));
            return;
        }
        SetGridColumnWidth(LibraryRoot.ColumnDefinitions[1], new GridLength(1, GridUnitType.Star));
        SetGridColumnWidth(LibraryRoot.ColumnDefinitions[2], new GridLength(0));
    }

    // Cards keep their fixed 166x304 wrap slot. The panel's minimum width
    // follows the viewport so Avalonia measures row breaks from the visible
    // shelf width instead of holding onto an old six-card desired width.
    private void UpdateBookGridLayout()
    {
        if (_bookGridPanel is null) return;
        _bookGridPanel.ItemWidth = BookGridSlotWidth;
        _bookGridPanel.ItemHeight = BookGridSlotHeight;
        BookGrid.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;

        var viewportWidth = LibraryContentHost.Bounds.Width > 0
            ? LibraryContentHost.Bounds.Width
            : LibraryWorkspace.Bounds.Width > 0
                ? LibraryWorkspace.Bounds.Width
                : BookGrid.Bounds.Width;
        if (viewportWidth > 0 && Math.Abs(_bookGridPanel.ViewportWidth - viewportWidth) > 0.5)
        {
            BookGrid.Width = viewportWidth;
            BookGrid.MinWidth = 0;
            _bookGridPanel.ViewportWidth = viewportWidth;
            _bookGridPanel.Width = viewportWidth;
            _bookGridPanel.MinWidth = 0;
        }
    }

    private static void SetGridColumnWidth(ColumnDefinition column, GridLength width)
    {
        if (!column.Width.Equals(width))
            column.Width = width;
    }

    private void SidebarSectionButton_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, BookManagementSectionButton))
            BookManagementChildren.IsVisible = !BookManagementChildren.IsVisible;
        else if (ReferenceEquals(sender, DeviceManagementSectionButton))
            DeviceManagementChildren.IsVisible = !DeviceManagementChildren.IsVisible;
        else if (ReferenceEquals(sender, ReadingSectionButton))
            ReadingChildren.IsVisible = !ReadingChildren.IsVisible;
        else if (ReferenceEquals(sender, SystemSectionButton))
            SystemChildren.IsVisible = !SystemChildren.IsVisible;

        UpdateSidebarSectionVisuals();
    }

    private void UpdateSidebarSectionVisuals()
    {        BookManagementChevron.Data = Geometry.Parse(
            BookManagementChildren.IsVisible ? SidebarChevronDownData : SidebarChevronRightData);
        DeviceManagementChevron.Data = Geometry.Parse(
            DeviceManagementChildren.IsVisible ? SidebarChevronDownData : SidebarChevronRightData);
        ReadingChevron.Data = Geometry.Parse(
            ReadingChildren.IsVisible ? SidebarChevronDownData : SidebarChevronRightData);
        SystemChevron.Data = Geometry.Parse(
            SystemChildren.IsVisible ? SidebarChevronDownData : SidebarChevronRightData);

        var sectionButtons = new[]
        {
            BookManagementSectionButton,
            DeviceManagementSectionButton,
            ReadingSectionButton,
            SystemSectionButton
        };
        foreach (var button in sectionButtons)
        {
            var active = ReferenceEquals(button, _activeNavigationSectionButton);
            button.Classes.Set("active", active);
        }
    }

    // Interactive-control tool tips, mirroring the WinUI ToolTips.cs pass: the
    // first time the window opens, every Button/TextBox/ComboBox/NumberBox/
    // Slider/ToggleSwitch/CheckBox without an explicit tool tip gets one built
    // from its accessible name, content text or placeholder.
    private bool _interactiveToolTipsApplied;
    private readonly Dictionary<Control, string> _interactiveAutoToolTips = [];

    private void EnsureInteractiveControlToolTips()
    {
        if (_interactiveToolTipsApplied) return;
        _interactiveToolTipsApplied = true;
        ApplyInteractiveControlToolTips(this);
    }

    private void ApplyInteractiveControlToolTips(Visual root)
    {
        if (root is Control control)
        {
            if (!control.Classes.Contains("noAutoToolTip") && ToolTip.GetTip(control) is null)
            {
                var text = BuildControlToolTip(control);
                if (!string.IsNullOrWhiteSpace(text))
                {
                    ToolTip.SetTip(control, text);
                    _interactiveAutoToolTips[control] = text;
                }
            }
            foreach (var child in root.GetVisualChildren())
                ApplyInteractiveControlToolTips(child);
        }
    }

    private void RefreshInteractiveControlToolTips()
    {
        foreach (var entry in _interactiveAutoToolTips.ToArray())
        {
            var control = entry.Key;
            if (ToolTip.GetTip(control) is not string current
                || !string.Equals(current, entry.Value, StringComparison.Ordinal))
            {
                // A later feature-specific tooltip owns this control now.
                _interactiveAutoToolTips.Remove(control);
                continue;
            }

            var text = BuildControlToolTip(control);
            if (string.IsNullOrWhiteSpace(text))
            {
                ToolTip.SetTip(control, null);
                _interactiveAutoToolTips.Remove(control);
            }
            else
            {
                ToolTip.SetTip(control, text);
                _interactiveAutoToolTips[control] = text;
            }
        }
    }

    private static string? BuildControlToolTip(Control control)
    {
        var accessibleName = AutomationProperties.GetName(control);
        return control switch
        {
            ToggleSwitch toggleSwitch => DescribeField(accessibleName, T("切换开关")),
            CheckBox checkBox => DescribeField(accessibleName, T("切换选项")),
            Button button => FirstNonEmpty(accessibleName, ReadContentText(button.Content)),
            ComboBox comboBox => DescribeField(accessibleName, T("选择选项")),
            NumericUpDown numberBox => DescribeField(accessibleName, T("输入或调整数值")),
            TextBox textBox => DescribeField(
                accessibleName,
                string.IsNullOrWhiteSpace(textBox.PlaceholderText) ? T("输入文本") : textBox.PlaceholderText),
            Slider slider => DescribeField(accessibleName, T("拖动以调整数值")),
            _ => null
        };
    }

    private static string? DescribeField(string? accessibleName, string action)
    {
        return string.IsNullOrWhiteSpace(accessibleName) ? action : $"{accessibleName}：{action}";
    }

    private static string? ReadContentText(object? content) => content switch
    {
        string text => text.Trim(),
        TextBlock textBlock => textBlock.Text?.Trim(),
        _ => null
    };

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
