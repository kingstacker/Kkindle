using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Views;
using Avalonia.Android;
using AndroidUri = Android.Net.Uri;

namespace Kkindle.AndroidHead;

/// <summary>
/// Hosts the Avalonia single view. Document import uses the Storage Access
/// Framework (ACTION_OPEN_DOCUMENT) because the library only understands local
/// file paths; picked URIs are copied into app-private storage before import.
/// </summary>
[Activity(
    Label = "Kkindle",
    Theme = "@style/KkindleTheme",
    MainLauncher = true,
    Exported = true,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation
        | ConfigChanges.ScreenSize
        | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout
        | ConfigChanges.SmallestScreenSize
        | ConfigChanges.Density
        // Without these a font-size, language, keyboard or RTL change recreates
        // the activity, which tears down the open reader mid-page.
        | ConfigChanges.FontScale
        | ConfigChanges.Locale
        | ConfigChanges.LayoutDirection
        | ConfigChanges.Keyboard
        | ConfigChanges.KeyboardHidden
        | ConfigChanges.Navigation)]
public sealed class MainActivity : AvaloniaMainActivity
{
    private const int PickDocumentsRequest = 0x4B4;

    // Static: if the system recreates the activity while the picker is open,
    // the result arrives at the new instance and must still finish the import.
    private static TaskCompletionSource<IReadOnlyList<AndroidUri>>? _pendingPick;

    public static MainActivity? Current { get; private set; }

    public static bool IsInForeground { get; private set; }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Current = this;
        BackgroundSyncJob.Schedule(this);
    }

    protected override void OnResume()
    {
        base.OnResume();
        IsInForeground = true;
    }

    protected override void OnPause()
    {
        IsInForeground = false;
        base.OnPause();
    }

    protected override void OnDestroy()
    {
        if (ReferenceEquals(Current, this)) Current = null;
        base.OnDestroy();
    }

    public Task<IReadOnlyList<AndroidUri>> PickDocumentsAsync()
    {
        if (_pendingPick is not null) return _pendingPick.Task;

        _pendingPick = new TaskCompletionSource<IReadOnlyList<AndroidUri>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var intent = new Intent(Intent.ActionOpenDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType("*/*");
        intent.PutExtra(Intent.ExtraAllowMultiple, true);
        StartActivityForResult(intent, PickDocumentsRequest);
        return _pendingPick.Task;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode != PickDocumentsRequest) return;

        var completion = _pendingPick;
        _pendingPick = null;
        if (completion is null) return;

        var uris = new List<AndroidUri>();
        if (resultCode == Result.Ok && data is not null)
        {
            if (data.Data is { } single) uris.Add(single);
            if (data.ClipData is { } clip)
            {
                for (var index = 0; index < clip.ItemCount; index++)
                {
                    if (clip.GetItemAt(index)?.Uri is { } uri
                        && !uris.Exists(existing => existing.Equals(uri)))
                    {
                        uris.Add(uri);
                    }
                }
            }
        }

        completion.TrySetResult(uris);
    }

    public string? GetDisplayName(AndroidUri uri)
    {
        try
        {
            using var cursor = ContentResolver?.Query(
                uri,
                [OpenableColumns.DisplayName],
                null,
                null,
                null);
            if (cursor is not null && cursor.MoveToFirst())
            {
                var index = cursor.GetColumnIndex(OpenableColumns.DisplayName);
                if (index >= 0) return cursor.GetString(index);
            }
        }
        catch
        {
            // Provider may not expose a display name; fall back to the path.
        }

        return uri.LastPathSegment;
    }
}
