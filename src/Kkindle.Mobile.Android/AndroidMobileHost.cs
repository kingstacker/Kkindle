using Android.Content;
using Kkindle.ApplicationServices;
using Kkindle.Infrastructure;
using Kkindle.Mobile;

namespace Kkindle.AndroidHead;

/// <summary>Chooses the Android implementations of the Core platform contracts.</summary>
internal static class AndroidMobileHost
{
    public static MobileServices Create(Context context)
    {
        var root = context.FilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Android did not provide a files directory.");
        var paths = new AppPaths(root);
        paths.EnsureDirectories();

        return new MobileServices(
            paths,
            new AndroidSecretProtector(Path.Combine(root, "keys")),
            new BookMetadataService(),
            // Calibre is an external process; conversion is unavailable on Android
            // until replaced by an in-process converter.
            new UnavailableBookFormatConverter(),
            new AndroidFileImportSource(),
            new AndroidSystemBars(),
            () => new AndroidTtsEngine(context, Path.Combine(context.CacheDir!.AbsolutePath, "tts")),
            () => new AndroidTtsAudioPlayer());
    }
}
