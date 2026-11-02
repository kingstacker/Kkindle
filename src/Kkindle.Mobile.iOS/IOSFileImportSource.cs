using Foundation;
using Kkindle.Mobile;
using UIKit;
using UniformTypeIdentifiers;

namespace Kkindle.iOSHead;

/// <summary>
/// Presents the system document picker and copies the picked files into the app
/// cache so the library, which works with local paths, can import them.
/// </summary>
public sealed class IOSFileImportSource : IFileImportSource
{
    public async Task<IReadOnlyList<string>> PickFilesAsync(CancellationToken cancellationToken = default)
    {
        var picked = new TaskCompletionSource<IReadOnlyList<NSUrl>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        NSRunLoop.Main.BeginInvokeOnMainThread(() =>
        {
            var picker = new UIDocumentPickerViewController([UTTypes.Item], asCopy: true)
            {
                AllowsMultipleSelection = true
            };
            // asCopy hands over sandbox-local copies, so the URLs stay readable
            // after the picker is dismissed.
            picker.DidPickDocumentAtUrls += (_, args) =>
                picked.TrySetResult(args.Urls ?? []);
            picker.WasCancelled += (_, _) => picked.TrySetResult([]);

            if (TopViewController() is not { } presenter)
            {
                picked.TrySetResult([]);
                return;
            }

            presenter.PresentViewController(picker, animated: true, completionHandler: null);
        });

        var urls = await picked.Task.WaitAsync(cancellationToken);
        return await CopyToCacheAsync(urls, cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> CopyToCacheAsync(
        IReadOnlyList<NSUrl> urls,
        CancellationToken cancellationToken)
    {
        if (urls.Count == 0) return [];

        var directory = Path.Combine(Path.GetTempPath(), "imports");
        Directory.CreateDirectory(directory);

        var files = new List<string>(urls.Count);
        try
        {
            foreach (var url in urls)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (url.Path is not { } source || !File.Exists(source)) continue;

                var access = url.StartAccessingSecurityScopedResource();
                try
                {
                    var fileDirectory = Path.Combine(directory, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(fileDirectory);
                    var target = Path.Combine(fileDirectory, SafeFileName(url.LastPathComponent));
                    files.Add(target);
                    await using var input = File.OpenRead(source);
                    await using var output = File.Create(target);
                    await input.CopyToAsync(output, cancellationToken);
                }
                finally
                {
                    if (access) url.StopAccessingSecurityScopedResource();
                }
            }
            return files;
        }
        catch
        {
            DeleteFiles(files);
            throw;
        }
    }

    public void ReleaseFiles(IReadOnlyList<string> files) => DeleteFiles(files);

    private static void DeleteFiles(IEnumerable<string> files)
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "imports")) + Path.DirectorySeparatorChar;
        foreach (var file in files)
        {
            if (!Path.GetFullPath(file).StartsWith(root, StringComparison.Ordinal)) continue;
            try { File.Delete(file); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            var parent = Path.GetDirectoryName(Path.GetFullPath(file));
            if (parent is null || !parent.StartsWith(root, StringComparison.Ordinal)) continue;
            try { Directory.Delete(parent); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static UIViewController? TopViewController()
    {
        var windows = UIApplication.SharedApplication.ConnectedScenes
            .OfType<UIWindowScene>()
            .SelectMany(scene => scene.Windows)
            .ToArray();
        var window = windows.FirstOrDefault(candidate => candidate.IsKeyWindow)
            ?? windows.FirstOrDefault();
        var controller = window?.RootViewController;
        while (controller?.PresentedViewController is { } presented)
            controller = presented;
        return controller;
    }

    private static string SafeFileName(string? name)
    {
        var trimmed = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(trimmed)) trimmed = $"import-{Guid.NewGuid():N}";
        foreach (var invalid in Path.GetInvalidFileNameChars())
            trimmed = trimmed.Replace(invalid, '_');
        return trimmed;
    }

}
