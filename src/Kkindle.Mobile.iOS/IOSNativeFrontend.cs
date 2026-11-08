#if KKINDLE_SWIFT_UI
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.iOS;
using Avalonia.Threading;
using Foundation;
using Kkindle.Core;
using Kkindle.Mobile;
using Kkindle.Mobile.Views;
using UIKit;
using static Kkindle.iOSHead.IOSNativeJson;

namespace Kkindle.iOSHead;

/// <summary>
/// In-process boundary between SwiftUI and the existing application services.
/// Requests and replies contain data only; native controls never forward taps
/// to hidden Avalonia buttons or share their scrolling/clip geometry.
/// </summary>
internal sealed partial class IOSNativeFrontend
{
    private static readonly NativeRequest Callback = OnRequest;
    private static IOSNativeFrontend? _current;
    private readonly MobileShell _shell;
    private readonly AvaloniaView _readerSurface;
    private readonly Dictionary<long, CancellationTokenSource> _requests = [];
    private readonly Dictionary<long, TaskCompletionSource<bool>> _prompts = [];
    private readonly List<NSObject> _observers = [];
    private readonly SemaphoreSlim _readerGate = new(1, 1);
    private readonly AsyncLocal<CancellationToken> _requestToken = new();
    private readonly UIViewController _readerController;
    private long _promptId;
    private ReaderPage? _reader;
    private CancellationTokenSource? _readerCancellation;

    internal static bool IsAttached => _current is not null;

    private IOSNativeFrontend(MobileShell shell, AvaloniaView readerSurface, UIViewController readerController)
    {
        _shell = shell;
        _readerSurface = readerSurface;
        _readerController = readerController;
        shell.NativeToast = message => Publish(Data(("type", "toast"), ("message", message)));
        shell.NativeConfirmation = ConfirmAsync;
        shell.NativeRestartRequired = () => Publish(Data(("type", "restart")));
        shell.NativeLibraryChanged = () => Publish(Data(("type", "libraryChanged")));
        shell.DeveloperModeChanged += (_, _) => Publish(Data(("type", "developer"), ("enabled", shell.IsDeveloperMode)));
        _observers.Add(UIApplication.Notifications.ObserveDidBecomeActive((_, _) => _reader?.Resume()));
        _observers.Add(UIApplication.Notifications.ObserveWillResignActive((_, _) =>
        {
            if (_reader is not { } reader) return;
            if (shell.Services.BackgroundWork is { } background) background.Run(reader.PauseAsync);
            else _ = reader.PauseAsync();
        }));
    }

    internal static void Attach(UIWindow window)
    {
        if (_current is not null || Application.Current is not MobileApp { Shell: { } shell,
                ApplicationLifetime: ISingleViewApplicationLifetime lifetime }) return;

        // This surface is only a child of the native reader. It is never the
        // window root, so opening a book doesn't transfer a visible controller
        // between hierarchies or leave an appearance transition unfinished.
        var surface = new AvaloniaView();
        var readerController = new DefaultAvaloniaViewController { View = surface };
        surface.InitWithController(readerController);
        var frontend = new IOSNativeFrontend(shell, surface, readerController);
        NativeLibrary.SetDllImportResolver(typeof(IOSNativeFrontend).Assembly, (library, _, _) =>
            library == "__Internal" ? NativeLibrary.GetMainProgramHandle() : IntPtr.Zero);
        _current = frontend;
        var pointer = CreateShell(Marshal.GetFunctionPointerForDelegate(Callback), readerController.Handle);
        if (pointer == IntPtr.Zero)
            throw new InvalidOperationException("无法创建 SwiftUI 界面。");
        var controller = ObjCRuntime.Runtime.GetNSObject<UIViewController>(pointer)
            ?? throw new InvalidOperationException("无法绑定 SwiftUI 界面。");
        window.RootViewController = controller;
        window.BackgroundColor = UIColor.SystemBackground;
        // The managed preferences are already loaded. Publish them before the
        // first frame, so an early native tap never saves Swift's placeholders
        // over the user's existing reading and appearance settings.
        Publish(Data(("type", "preferences"), ("preferences", shell.Preferences.Current)));
        Publish(Data(("type", "developer"), ("enabled", shell.IsDeveloperMode)));
#if DEBUG
        Console.WriteLine("Kkindle native: SwiftUI root created.");
#endif
        ReleaseShell(pointer);
        lifetime.MainView = null;
        if (MobileDataCoordinator.IsRestartRequired) Publish(Data(("type", "restart")));
        else shell.RequestAutoSync(MobileShell.AutoSyncGap.OnStart);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeRequest(long id, IntPtr json);

    [ObjCRuntime.MonoPInvokeCallback(typeof(NativeRequest))]
    private static void OnRequest(long id, IntPtr json)
    {
        // Swift's UTF-8 buffer is borrowed only for the duration of this call.
        var request = Marshal.PtrToStringUTF8(json);
        if (request is null || _current is not { } frontend) return;
        Dispatcher.UIThread.Post(() => frontend.HandleRequest(id, request));
    }

    private async void HandleRequest(long id, string request)
    {
        CancellationTokenSource? cancellation = null;
        try
        {
            using var document = JsonDocument.Parse(request);
            var command = document.RootElement.GetProperty("command").GetString() ?? string.Empty;
            if (command == "cancel")
            {
                if (_requests.TryGetValue(id, out var pending)) pending.Cancel();
                return;
            }
            cancellation = new CancellationTokenSource();
            _requests.Add(id, cancellation);
            _requestToken.Value = cancellation.Token;
            var args = document.RootElement.TryGetProperty("args", out var value) ? value : default;
            var result = await DispatchAsync(command, args, cancellation.Token);
            ResolveRequest(id, Serialize(Data(("ok", true), ("result", result))));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ResolveRequest(id, Serialize(Data(("ok", false), ("error", UiText.Localize(exception.Message)))));
        }
        finally
        {
            if (cancellation is not null)
            {
                _requests.Remove(id);
                cancellation.Dispose();
            }
        }
    }

    private async Task<object> DispatchAsync(string command, JsonElement args, CancellationToken token)
    {
        if (command == "ui.confirm")
        {
            if (_prompts.Remove(args.GetProperty("id").GetInt64(), out var decision))
                decision.TrySetResult(args.GetProperty("accepted").GetBoolean());
            return Data();
        }
        if (MobileDataCoordinator.IsRestartRequired)
            throw new InvalidOperationException("请完全关闭并重新打开应用后继续使用。");
        switch (command)
        {
            case "library.get":
                return await GetLibraryAsync(token);
            case "library.import":
                await ImportBooksAsync(token);
                return Data();
            case "preferences.save":
                var preferences = Read(args.GetProperty("preferences"), new MobilePreferences(), IOSNativeJsonContext.Default.MobilePreferences);
                await _readerGate.WaitAsync(token);
                try
                {
                    _shell.Preferences.Update(_ => NormalizePreferences(preferences));
                    _shell.ApplyThemeMode();
                    if (_reader is { } currentReader) await currentReader.NativeApplySettingsAsync();
                }
                finally { _readerGate.Release(); }
                return _shell.Preferences.Current;
            case "reader.open":
                _readerCancellation?.Cancel();
                await _readerGate.WaitAsync(token);
                try
                {
                    await CloseReaderAsync();
                    var book = await _shell.Modules.Library.Books.GetBookAsync(Id(args, "bookId"), token)
                        ?? throw new InvalidOperationException("书籍已不存在。");
                    var jump = args.TryGetProperty("location", out var location)
                        ? location.Deserialize(IOSNativeJsonContext.Default.ReaderProgressRow) : null;
                    _readerCancellation = new CancellationTokenSource();
                    _reader = new ReaderPage(book, _shell.Modules, _shell, jump);
                    _reader.NativeStateChanged += state => Publish(Data(("type", "reader"), ("reader", state)));
                    _reader.NativeDialogRequested += dialog => Publish(Data(("type", "readerDialog"), ("dialog", dialog)));
                    _reader.UseNativeFrontend();
                    _readerSurface.Content = _reader;
                }
                finally { _readerGate.Release(); }
                return Data();
            case "reader.close":
                _readerCancellation?.Cancel();
                await _readerGate.WaitAsync(token);
                try { await CloseReaderAsync(); }
                finally { _readerGate.Release(); }
                return Data();
            default:
                if (command.StartsWith("reader.", StringComparison.Ordinal))
                    return await DispatchReaderAsync(command, args, token);
                if (command.StartsWith("book.", StringComparison.Ordinal)
                    || command.StartsWith("collection.", StringComparison.Ordinal)
                    || command.StartsWith("materials.", StringComparison.Ordinal)
                    || command.StartsWith("trash.", StringComparison.Ordinal)
                    || command.StartsWith("backup.", StringComparison.Ordinal)
                    || command.StartsWith("txt.", StringComparison.Ordinal)
                    || command == "dashboard.get")
                    return await DispatchLibraryAsync(command, args, token);
                if (command.StartsWith("settings.", StringComparison.Ordinal)
                    || command.StartsWith("dictionary.", StringComparison.Ordinal))
                    return await DispatchSettingsAsync(command, args, token);
                throw new InvalidOperationException("未知操作：" + command);
        }
    }

    private async Task CloseReaderAsync()
    {
        if (_reader is not { } reader) return;
        await reader.PauseAsync();
        _readerSurface.Content = null;
        _reader = null;
        _readerCancellation?.Dispose();
        _readerCancellation = null;
        _shell.RequestAutoSync(MobileShell.AutoSyncGap.AfterReading);
    }

    private async Task<object> GetLibraryAsync(CancellationToken token)
    {
        using var operation = BeginOperation();
        await _shell.Modules.Library.Books.InitializeAsync(token);
        await _shell.EnsureReaderDataAsync();
        var books = await _shell.Modules.Library.Books.SearchAsync(cancellationToken: token);
        var collections = await _shell.Modules.Library.Books.GetCollectionSummariesAsync(token);
        var dashboard = await _shell.Modules.Reader.Data.GetReadingDashboardAsync(dayCount: 1, cancellationToken: token);
        var progress = dashboard.Books.GroupBy(item => item.BookId)
            .ToDictionary(group => group.Key, group => group.Max(item => item.ProgressPercent));
        var continueBookId = dashboard.Books.Where(item => item.IsInLibrary && item.ProgressPercent < 99.5)
            .OrderByDescending(item => item.UpdatedAt).Select(item => (Guid?)item.BookId).FirstOrDefault();
        return Data(
            ("books", books.Select(book => BookData(book, progress.GetValueOrDefault(book.Id))).ToArray()),
            ("collections", collections.Select(item => Data(
                ("id", item.Collection.Id), ("name", item.Collection.Name), ("count", item.BookCount),
                ("automatic", MobileCollections.IsUncollected(item.Collection)))).ToArray()),
            ("preferences", _shell.Preferences.Current), ("continueBookId", continueBookId));
    }

    private object BookData(Book book, double progress) => Data(
        ("id", book.Id), ("title", book.Title), ("authors", book.Authors), ("formats", book.FormatSummary),
        ("size", MobileFormat.Size(book.Files.Sum(file => file.Size))),
        ("coverPath", string.IsNullOrWhiteSpace(book.CoverPath) ? null
            : Path.IsPathRooted(book.CoverPath) ? book.CoverPath : Path.Combine(_shell.Modules.Paths.Data, book.CoverPath)),
        ("progress", progress), ("favorite", book.IsFavorite), ("readingStatus", (int)book.ReadingStatus),
        ("collectionIds", book.CollectionIds.ToArray()), ("updatedAt", book.UpdatedAt), ("createdAt", book.CreatedAt),
        ("description", book.Description), ("series", book.Series), ("publisher", book.Publisher),
        ("publishDate", book.PublishDate), ("isbn", book.Isbn), ("pageCount", book.PageCount),
        ("binding", book.Binding), ("tags", book.Tags), ("category", book.Category), ("onlineRating", book.OnlineRating));

    private async Task ImportBooksAsync(CancellationToken token)
    {
        var files = await _shell.Services.ImportSource.PickFilesAsync(token);
        try
        {
            if (files.Count == 0) return;
            var supported = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { ".epub", ".pdf", ".mobi", ".azw3", ".txt" };
            if (files.Any(path => !supported.Contains(Path.GetExtension(path))))
                throw new InvalidOperationException("请选择 EPUB、PDF、MOBI、AZW3 或 TXT 文件。");
            var rules = new Dictionary<string, TxtImportRules>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in files.Where(path => Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase)))
            {
                var selected = await ChooseTxtRulesAsync(file, token);
                if (selected is null) return;
                rules.Add(file, selected);
            }
            using var operation = BeginOperation();
            await _shell.Modules.Library.Books.InitializeAsync(token);
            var result = await Task.Run(() => _shell.Modules.Library.Books.ImportAsync(files,
                cancellationToken: token, txtImportRules: rules), token);
            _shell.ShowToast(result.FailureCount > 0
                ? $"已导入 {result.AddedCount} 本，{result.FailureCount} 个文件失败"
                : result.AddedCount == 0 ? "所选书籍已在书库中" : $"已导入 {result.AddedCount} 本");
            if (result.Items.FirstOrDefault(item => !item.Succeeded) is { } failed)
                throw new InvalidOperationException(failed.Message);
        }
        finally { _shell.Services.ImportSource.ReleaseFiles(files); }
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirm)
    {
        var id = ++_promptId;
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _prompts.Add(id, decision);
        Publish(Data(("type", "confirm"), ("prompt", Data(("id", id), ("title", title), ("message", message), ("confirm", confirm)))));
        try { return await decision.Task.WaitAsync(_requestToken.Value); }
        finally
        {
            _prompts.Remove(id);
            Publish(Data(("type", "confirmDismissed"), ("id", id)));
        }
    }

    private static MobileDataCoordinator.OperationLease BeginOperation() =>
        MobileDataCoordinator.TryBeginOperation() ?? throw new InvalidOperationException("正在备份或恢复数据，请稍后重试。");

    private static Guid Id(JsonElement args, string name) => args.GetProperty(name).GetGuid();
    private static string Text(JsonElement args, string name, string fallback = "") =>
        args.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? fallback : fallback;
    private static bool Flag(JsonElement args, string name, bool fallback = false) =>
        args.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : fallback;
    private static int Number(JsonElement args, string name, int fallback = 0) =>
        args.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? number : fallback;
    private static MobilePreferences NormalizePreferences(MobilePreferences value) => value with
    {
        ThemeMode = Enum.IsDefined(value.ThemeMode) ? value.ThemeMode : MobileThemeMode.System,
        Sort = Enum.IsDefined(value.Sort) ? value.Sort : MobileBookSort.Recent,
        ReaderTheme = Enum.IsDefined(value.ReaderTheme) ? value.ReaderTheme : ReaderTheme.Classic,
        ReaderPageAnimation = Enum.IsDefined(value.ReaderPageAnimation) ? value.ReaderPageAnimation : MobilePageAnimation.Fade,
        PdfZoom = double.IsFinite(value.PdfZoom) ? Math.Clamp(value.PdfZoom, 1, 4) : 1,
        ReaderFontScale = double.IsFinite(value.ReaderFontScale)
            ? Math.Clamp(value.ReaderFontScale, ReaderLayoutDefaults.MinFontScale, ReaderLayoutDefaults.MaxFontScale) : 1.35,
        ReaderLineHeight = double.IsFinite(value.ReaderLineHeight) ? Math.Clamp(value.ReaderLineHeight, 1.2, 3) : 1.75,
        ReaderParagraphSpacing = double.IsFinite(value.ReaderParagraphSpacing)
            ? Math.Clamp(value.ReaderParagraphSpacing, 0, ReaderLayoutDefaults.MaxParagraphSpacing) : ReaderLayoutDefaults.DefaultParagraphSpacing,
        ReaderBodyPadding = double.IsFinite(value.ReaderBodyPadding) ? Math.Clamp(value.ReaderBodyPadding, 16, 64) : 24
    };
    private static void Publish(object value)
    {
        var json = Serialize(value);
        if (Dispatcher.UIThread.CheckAccess()) PublishEvent(json);
        else Dispatcher.UIThread.Post(() => PublishEvent(json));
    }

    [DllImport("__Internal", EntryPoint = "kk_ios_create_shell")]
    private static extern IntPtr CreateShell(IntPtr handler, IntPtr readerController);
    [DllImport("__Internal", EntryPoint = "kk_ios_release_shell")]
    private static extern void ReleaseShell(IntPtr pointer);
    [DllImport("__Internal", EntryPoint = "kk_ios_resolve_request")]
    private static extern void ResolveRequest(long id, [MarshalAs(UnmanagedType.LPUTF8Str)] string json);
    [DllImport("__Internal", EntryPoint = "kk_ios_publish_event")]
    private static extern void PublishEvent([MarshalAs(UnmanagedType.LPUTF8Str)] string json);
}
#endif
