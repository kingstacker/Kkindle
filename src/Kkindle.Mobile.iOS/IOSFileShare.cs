using CoreGraphics;
using Foundation;
using Kkindle.Mobile;
using UIKit;

namespace Kkindle.iOSHead;

/// <summary>Shares a completed temporary file through the iOS activity sheet.</summary>
internal sealed class IOSFileShare : IMobileFileShare
{
    public async Task<bool> ShareAsync(string fileName, byte[] content)
    {
        var directory = Path.Combine(Path.GetTempPath(), "exports", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, Path.GetFileName(fileName));
            await File.WriteAllBytesAsync(path, content);
            return await ShareFileAsync(path);
        }
        finally
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async Task<bool> ShareFileAsync(string path)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        NSRunLoop.Main.BeginInvokeOnMainThread(() =>
        {
            try
            {
                var windows = UIApplication.SharedApplication.ConnectedScenes
                    .OfType<UIWindowScene>()
                    .SelectMany(scene => scene.Windows)
                    .ToArray();
                var controller = (windows.FirstOrDefault(window => window.IsKeyWindow) ?? windows.FirstOrDefault())
                    ?.RootViewController;
                while (controller?.PresentedViewController is { } presented)
                    controller = presented;
                if (controller?.View is not { } view)
                    throw new InvalidOperationException("无法打开系统分享面板。");

                var activity = new UIActivityViewController([NSUrl.FromFilename(path)], null);
                if (activity.PopoverPresentationController is { } popover)
                {
                    popover.SourceView = view;
                    popover.SourceRect = new CGRect(view.Bounds.GetMidX(), view.Bounds.GetMidY(), 1, 1);
                    popover.PermittedArrowDirections = 0;
                }
                activity.CompletionWithItemsHandler = (_, completed, _, error) =>
                {
                    if (error is not null) completion.TrySetException(new NSErrorException(error));
                    else completion.TrySetResult(completed);
                };
                controller.PresentViewController(activity, true, null);
            }
            catch (Exception exception)
            {
                completion.TrySetException(exception);
            }
        });
        return await completion.Task;
    }
}
