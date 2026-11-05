using Avalonia;
using Kkindle.ApplicationServices;
using Kkindle.Core;
using Kkindle.Infrastructure;
using Kkindle.Mobile;
using Kkindle.Platform.Common;

namespace Kkindle.Mobile.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Preview data lives apart from the desktop app's library so both can run
        // side by side without touching the same database.
        // KKINDLE_MOBILE_PREVIEW_ROOT points a review run (for example --shots,
        // which adds bookmarks and collections) at a throwaway library.
        var root = Environment.GetEnvironmentVariable("KKINDLE_MOBILE_PREVIEW_ROOT") is { Length: > 0 } overrideRoot
            ? Path.GetFullPath(overrideRoot)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Kkindle",
                "mobile-preview");
        var paths = new AppPaths(root);
        paths.EnsureDirectories();

        var services = new MobileServices(
            paths,
            new PreviewSecretProtector(Path.Combine(root, "keys")),
            new BookMetadataService(),
            new UnavailableBookFormatConverter(),
            new NoFileImportSource());

        MobileApp.Services = services;

        // Dev-only: seed the preview library from the command line so the mobile
        // screens can be reviewed with real books before import UI exists on device.
        if (args.Length > 1 && args[0] == "--seed")
        {
            SeedAsync(services, args[1..]).GetAwaiter().GetResult();
            return;
        }

        // Dev-only: act as a second device so mobile sync can be tested end to end.
        if (args.Length == 5 && args[0] == "--sync")
        {
            SyncAsync(services, args).GetAwaiter().GetResult();
            return;
        }

        var builder = AppBuilder.Configure(() => new MobileApp())
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        // Dev-only: walk every screen and save it as a PNG, for design review.
        if (args.Length > 1 && args[0] == "--shots")
            builder = builder.AfterSetup(_ => PreviewScreenshots.Schedule(args[1]));

        builder.StartWithClassicDesktopLifetime(args);
    }

    private static async Task SeedAsync(MobileServices services, string[] files)
    {
        var modules = MobileComposition.Create(services);
        // A fresh preview root has no schema until the library is initialized.
        await modules.Library.Books.InitializeAsync();
        var result = await modules.Library.Books.ImportAsync(files);
        Console.WriteLine($"seeded: added {result.AddedCount}, failed {result.FailureCount}");
        foreach (var item in result.Items.Where(item => !item.Succeeded))
            Console.WriteLine($"  failed: {item.Message}");
    }

    private static async Task SyncAsync(MobileServices services, string[] args)
    {
        var modules = MobileComposition.Create(services);
        await modules.Library.Books.InitializeAsync();
        await modules.Reader.Data.InitializeAsync();
        var sync = modules.Sync.Service;
        var stored = await sync.LoadSettingsAsync();
        // Passed only for this run, so test credentials are never written to disk.
        var settings = S3SyncSettings.Normalize(new S3SyncSettings
        {
            Enabled = true,
            Endpoint = args[1],
            Bucket = args[2],
            AccessKey = args[3],
            SecretKey = args[4],
            PathStyle = true
        });
        await sync.InitializeDeletionTrackingAsync(default, stored.DeviceId);
        Console.WriteLine(await sync.SyncAsync(stored.DeviceId, settings));
    }

    private sealed class PreviewSecretProtector(string directory) : AesGcmSecretProtector
    {
        private readonly string _keyPath = Path.Combine(directory, "secret.key");

        protected override byte[] GetOrCreateKey()
        {
            Directory.CreateDirectory(directory);
            if (File.Exists(_keyPath))
            {
                try
                {
                    return ParseStoredKey(File.ReadAllText(_keyPath));
                }
                catch (System.Security.Cryptography.CryptographicException)
                {
                    // Regenerate; only previously stored preview secrets are lost.
                }
            }

            var key = CreateKey();
            File.WriteAllText(_keyPath, Convert.ToBase64String(key));
            return key;
        }
    }
}
