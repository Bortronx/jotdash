using Blinkjot.Services;

namespace Blinkjot;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();
        builder.Services.AddMauiBlazorWebView();

        // One shared instance of each: the pages, the sync triggers and the Android background worker all use them.
        builder.Services.AddSingleton<LocalStore>();
        builder.Services.AddSingleton<SettingsService>();
        builder.Services.AddSingleton<SyncService>();
        builder.Services.AddSingleton<Recorder>();

#if DEBUG
        builder.Services.AddBlazorWebViewDeveloperTools();
#endif
        return builder.Build();
    }
}
