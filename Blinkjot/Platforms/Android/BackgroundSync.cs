using Android.Content;
using AndroidX.Work;
using Blinkjot.Services;

namespace Blinkjot;

/// <summary>
/// Background sync through Android WorkManager. Android runs it even when the app is closed
/// or the screen is locked (whenever the phone has internet; at least every ~15 minutes).
/// </summary>
public static class BackgroundSync
{
    private static WorkManager Work => WorkManager.GetInstance(Platform.AppContext);
    private static Constraints Online => new Constraints.Builder().SetRequiredNetworkType(NetworkType.Connected!).Build();

    /// <summary>Every 15 minutes (the Android minimum), only with internet.</summary>
    public static void SchedulePeriodic()
    {
        var request = new PeriodicWorkRequest.Builder(typeof(SyncWorker), TimeSpan.FromMinutes(15))
            .SetConstraints(Online).Build();
        Work.EnqueueUniquePeriodicWork("blinkjot-periodic", ExistingPeriodicWorkPolicy.Keep!, (PeriodicWorkRequest)request);
    }

    /// <summary>Sync as soon as there is internet (e.g. after the app goes to the background with unsent notes).</summary>
    public static void SyncSoon()
    {
        var request = new OneTimeWorkRequest.Builder(typeof(SyncWorker))
            .SetConstraints(Online)
            .SetBackoffCriteria(BackoffPolicy.Exponential!, TimeSpan.FromSeconds(30))
            .Build();
        Work.EnqueueUniqueWork("blinkjot-soon", ExistingWorkPolicy.Replace!, (OneTimeWorkRequest)request);
    }
}

/// <summary>The job Android runs in the background: one normal sync.</summary>
[Android.Runtime.Register("com.bortronx.blinkjot.SyncWorker")]
public class SyncWorker(Context context, WorkerParameters args) : Worker(context, args)
{
    public override Result DoWork()
    {
        var sync = IPlatformApplication.Current?.Services.GetService<SyncService>();
        if (sync is null) return Result.InvokeRetry();
        string? error = sync.SyncAsync().GetAwaiter().GetResult();
        if (error is null) return Result.InvokeSuccess();
        return RunAttemptCount < 5 ? Result.InvokeRetry() : Result.InvokeFailure();
    }
}
