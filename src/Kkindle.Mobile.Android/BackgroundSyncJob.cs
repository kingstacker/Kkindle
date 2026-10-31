using Android.App;
using Android.App.Job;
using Android.Content;
using Kkindle.Mobile;

namespace Kkindle.AndroidHead;

/// <summary>
/// Periodic sync while the app is closed. Android decides the exact time, only
/// on an unmetered or any network as the system allows. Deletions that need
/// confirmation are left for the sync page, as for every background run.
/// </summary>
[Service(Name = "com.kingstacker.kkindle.BackgroundSyncJob",
    Permission = "android.permission.BIND_JOB_SERVICE",
    Exported = false)]
public sealed class BackgroundSyncJob : JobService
{
    private const int JobId = 0x4B4B;
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private CancellationTokenSource? _cancellation;

    public static void Schedule(Context context)
    {
        if (context.GetSystemService(JobSchedulerService) is not JobScheduler scheduler) return;
        if (scheduler.GetPendingJob(JobId) is not null) return;
        var component = new ComponentName(context, Java.Lang.Class.FromType(typeof(BackgroundSyncJob)));
        var job = new JobInfo.Builder(JobId, component)
            .SetRequiredNetworkType(NetworkType.Any)!
            .SetPeriodic((long)Interval.TotalMilliseconds)!
            .Build();
        scheduler.Schedule(job);
    }

    public override bool OnStartJob(JobParameters? parameters)
    {
        // A visible app syncs on its own schedule.
        if (MainActivity.IsInForeground || MobileApp.Services is not { } services) return false;
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        _ = Task.Run(async () =>
        {
            var reschedule = false;
            try
            {
                await MobileSync.RunInBackgroundAsync(services, token);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                reschedule = !token.IsCancellationRequested;
            }
            JobFinished(parameters, reschedule);
        }, token);
        return true;
    }

    public override bool OnStopJob(JobParameters? parameters)
    {
        _cancellation?.Cancel();
        return true;
    }
}
