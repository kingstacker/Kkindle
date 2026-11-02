using Kkindle.Mobile;
using UIKit;

namespace Kkindle.iOSHead;

/// <summary>Lets a pending reader save finish when iOS backgrounds the app.</summary>
internal sealed class IOSBackgroundWork : IMobileBackgroundWork
{
    public void Run(Func<Task> work)
    {
        var application = UIApplication.SharedApplication;
        nint taskId = UIApplication.BackgroundTaskInvalid;
        void End()
        {
            var current = Interlocked.Exchange(ref taskId, UIApplication.BackgroundTaskInvalid);
            if (current != UIApplication.BackgroundTaskInvalid)
                application.EndBackgroundTask(current);
        }

        taskId = application.BeginBackgroundTask("Save reading progress", End);
        _ = RunAsync();

        async Task RunAsync()
        {
            try { await work(); }
            finally { End(); }
        }
    }
}
