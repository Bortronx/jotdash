using Blinkjot.Services;

namespace Blinkjot;

/// <summary>
/// App start-up + "when to sync": shortly after every edit, when the app opens or regains internet,
/// every 30 s while open, and via Android WorkManager when in the background / screen locked.
/// </summary>
public partial class App : Application
{
    private readonly SyncService _sync;
    private readonly LocalStore _store;
    private CancellationTokenSource? _debounce;
    private bool _visible = true;

    public App(SyncService sync, LocalStore store)
    {
        InitializeComponent();
        _sync = sync;
        _store = store;

        store.Edited += () => SyncSoon(TimeSpan.FromSeconds(3));
        Connectivity.ConnectivityChanged += (_, e) => { if (e.NetworkAccess == NetworkAccess.Internet) SyncSoon(TimeSpan.Zero); };
        Dispatcher.StartTimer(TimeSpan.FromSeconds(30), () => { if (_visible) SyncSoon(TimeSpan.Zero); return true; });
        BackgroundSync.SchedulePeriodic();
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(new MainPage()) { Title = "Blinkjot" };
        window.Resumed += (_, _) => { _visible = true; SyncSoon(TimeSpan.Zero); };
        window.Stopped += (_, _) =>
        {
            _visible = false;
            if (_store.PendingCount > 0) BackgroundSync.SyncSoon(); // finish uploading even if Android pauses the app
        };
        SyncSoon(TimeSpan.Zero);
        return window;
    }

    /// <summary>Runs a sync after a short pause (typing several letters only causes one sync).</summary>
    private void SyncSoon(TimeSpan delay)
    {
        _debounce?.Cancel();
        var cts = _debounce = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            try { await Task.Delay(delay, cts.Token); } catch (OperationCanceledException) { return; }
            await _sync.SyncAsync();
        });
    }
}
