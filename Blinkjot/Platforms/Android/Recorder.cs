using System.Diagnostics;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Media;
using Android.OS;
using AndroidX.Core.App;

namespace Blinkjot;

/// <summary>
/// Voice recorder. Records WebM/Opus (small files that Parakeet decodes reliably).
/// A foreground service + wake lock keeps recording when the screen is locked or the app is in the background.
/// </summary>
public class Recorder
{
    private MediaRecorder? _rec;
    private readonly Stopwatch _clock = new();

    public string? FilePath { get; private set; }
    public string? TargetTaskId { get; set; }  // add the recording to this task (null = create a new voice-note task)
    public bool IsRecording => _rec is not null;
    public bool IsPaused { get; private set; }
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>Asks for the microphone permission and starts recording. Returns an error message or null.</summary>
    public async Task<string?> StartAsync()
    {
        if (IsRecording) return null;
        if (await Permissions.RequestAsync<Permissions.Microphone>() != PermissionStatus.Granted)
            return "Blinkjot needs the microphone permission to record.";
        if (OperatingSystem.IsAndroidVersionAtLeast(33))
            await Permissions.RequestAsync<Permissions.PostNotifications>(); // optional: shows the "Recording…" notification

        var ctx = Platform.AppContext;
        ctx.StartForegroundService(new Intent(ctx, typeof(RecordingService)));

        FilePath = Path.Combine(FileSystem.CacheDirectory, $"voice-{DateTime.Now:yyyyMMdd-HHmmss}.webm");
        try
        {
            _rec = OperatingSystem.IsAndroidVersionAtLeast(31) ? new MediaRecorder(ctx) : new MediaRecorder();
            _rec.SetAudioSource(AudioSource.Mic);
            _rec.SetOutputFormat(OutputFormat.Webm);
            _rec.SetAudioEncoder(AudioEncoder.Opus);
            _rec.SetAudioChannels(1);
            _rec.SetAudioSamplingRate(48000);
            _rec.SetAudioEncodingBitRate(32000);
            _rec.SetOutputFile(FilePath);
            _rec.Prepare();
            _rec.Start();
        }
        catch (Exception e)
        {
            Release();
            return "Could not start the microphone: " + e.Message;
        }
        IsPaused = false;
        _clock.Restart();
        return null;
    }

    public void Pause()
    {
        if (_rec is null || IsPaused) return;
        _rec.Pause();
        _clock.Stop();
        IsPaused = true;
    }

    public void Resume()
    {
        if (_rec is null || !IsPaused) return;
        _rec.Resume();
        _clock.Start();
        IsPaused = false;
    }

    /// <summary>Stops and returns the finished file (null if nothing usable was recorded).</summary>
    public string? Stop()
    {
        if (_rec is null) return null;
        bool ok = true;
        try { _rec.Stop(); } catch { ok = false; } // throws when stopped right after start (no audio yet)
        Release();
        return ok && File.Exists(FilePath) ? FilePath : null;
    }

    public void Cancel()
    {
        Stop();
        if (FilePath is not null) try { File.Delete(FilePath); } catch { }
        FilePath = null;
    }

    private void Release()
    {
        _rec?.Release();
        _rec = null;
        _clock.Reset();
        IsPaused = false;
        var ctx = Platform.AppContext;
        ctx.StopService(new Intent(ctx, typeof(RecordingService)));
    }
}

/// <summary>Keeps the app alive (and the microphone allowed) while recording, with a visible notification.</summary>
[Service(ForegroundServiceType = ForegroundService.TypeMicrophone, Exported = false)]
public class RecordingService : Service
{
    private const string Channel = "recording";
    private PowerManager.WakeLock? _wake;

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var nm = (NotificationManager)GetSystemService(NotificationService)!;
        nm!.CreateNotificationChannel(new NotificationChannel(Channel, "Recording", NotificationImportance.Low));

        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop)!,
            PendingIntentFlags.Immutable);
        var builder = new NotificationCompat.Builder(this, Channel);
        builder.SetContentTitle("Blinkjot is recording");
        builder.SetContentText("Tap to open, pause or stop");
        builder.SetSmallIcon(Resource.Mipmap.appicon_foreground);
        builder.SetOngoing(true);
        builder.SetContentIntent(open);
        var notification = builder.Build()!;

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
            StartForeground(1, notification, ForegroundService.TypeMicrophone);
        else
            StartForeground(1, notification); // Android 10 has no "microphone" service type yet

        _wake ??= ((PowerManager)GetSystemService(PowerService)!).NewWakeLock(WakeLockFlags.Partial, "blinkjot:recording")!;
        if (!_wake.IsHeld) _wake.Acquire(TimeSpan.FromHours(4).Ticks / TimeSpan.TicksPerMillisecond);
        return StartCommandResult.NotSticky;
    }

    public override void OnDestroy()
    {
        if (_wake?.IsHeld == true) _wake.Release();
        base.OnDestroy();
    }
}
