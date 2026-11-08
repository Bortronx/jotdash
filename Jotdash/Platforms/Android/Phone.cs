using Android.Content;
using Android.Graphics;
using Android.Provider;
using Android.Views.InputMethods;
using Microsoft.AspNetCore.Components.WebView.Maui;
using Path = System.IO.Path;

namespace Jotdash;

/// <summary>Small Android helpers used by the pages.</summary>
public static class Phone
{
    /// <summary>Pops up the on-screen keyboard (the WebView doesn't do this by itself for a focused text box).</summary>
    public static void ShowKeyboard() => MainThread.BeginInvokeOnMainThread(() =>
    {
        if (Application.Current?.Windows.FirstOrDefault()?.Page is not MainPage page) return;
        if (page.WebView.Handler?.PlatformView is not Android.Webkit.WebView web) return;
        web.RequestFocus();
        var imm = (InputMethodManager?)Platform.AppContext.GetSystemService(Context.InputMethodService);
        imm?.ShowSoftInput(web, ShowFlags.Implicit);
    });

    /// <summary>Opens the camera. Returns (path, kind, contentType) or null when cancelled.</summary>
    public static async Task<(string Path, string Kind, string ContentType)?> CaptureAsync(bool video)
    {
        if (!MediaPicker.Default.IsCaptureSupported)
            throw new NotSupportedException("No camera app is available on this phone.");
        if (await Permissions.RequestAsync<Permissions.Camera>() != PermissionStatus.Granted)
            throw new PermissionException("Jotdash needs the camera permission to take photos.");
        var result = video ? await MediaPicker.Default.CaptureVideoAsync() : await MediaPicker.Default.CapturePhotoAsync();
        if (result is null) return null;

        string ext = Path.GetExtension(result.FileName) is { Length: > 0 } e ? e.ToLowerInvariant() : video ? ".mp4" : ".jpg";
        string copy = Path.Combine(FileSystem.CacheDirectory, Guid.NewGuid() + ext);
        await using (var src = await result.OpenReadAsync())
        await using (var dst = File.Create(copy))
            await src.CopyToAsync(dst);
        string type = result.ContentType is { Length: > 0 } ct ? ct : video ? "video/mp4" : "image/jpeg";
        return (copy, video ? "video" : "image", type);
    }

    /// <summary>A small JPEG preview as a data: URL (cached next to the file). Null when not possible.</summary>
    public static string? Thumbnail(string path, string kind)
    {
        try
        {
            string thumb = path + ".thumb.jpg";
            if (!File.Exists(thumb))
            {
                Bitmap? bmp = kind == "video"
                    ? Android.Media.ThumbnailUtils.CreateVideoThumbnail(new Java.IO.File(path), new Android.Util.Size(320, 320), null)
                    : Android.Media.ThumbnailUtils.CreateImageThumbnail(new Java.IO.File(path), new Android.Util.Size(320, 320), null);
                if (bmp is null) return null;
                using var fs = File.Create(thumb);
                bmp.Compress(Bitmap.CompressFormat.Jpeg!, 75, fs);
                bmp.Recycle();
            }
            return "data:image/jpeg;base64," + Convert.ToBase64String(File.ReadAllBytes(thumb));
        }
        catch { return null; }
    }

    /// <summary>Opens a file in the phone's own viewer/player.</summary>
    public static Task OpenFileAsync(string path, string contentType) =>
        Launcher.Default.OpenAsync(new OpenFileRequest { File = new ReadOnlyFile(path, contentType) });

    public static void ValidateUpdatePackage(string path)
    {
        var manager = Platform.AppContext.PackageManager
            ?? throw new InvalidOperationException("Android's package manager is unavailable.");
        var flags = Android.Content.PM.PackageInfoFlags.SigningCertificates;
        using var candidate = manager.GetPackageArchiveInfo(path, flags)
            ?? throw new InvalidOperationException("The downloaded APK is invalid.");
        using var installed = manager.GetPackageInfo(Platform.AppContext.PackageName!, flags)
            ?? throw new InvalidOperationException("Could not verify the installed app.");
        if (candidate.PackageName != installed.PackageName || candidate.LongVersionCode <= installed.LongVersionCode)
            throw new InvalidOperationException("The APK is not a newer Jotdash update.");
        var currentSigners = installed.SigningInfo?.GetApkContentsSigners();
        var updateSigners = candidate.SigningInfo?.GetApkContentsSigners();
        if (currentSigners is null || updateSigners is null || currentSigners.Length == 0
            || currentSigners.Length != updateSigners.Length
            || currentSigners.Any(current => !updateSigners.Any(next => current.Equals(next))))
            throw new InvalidOperationException("The update signing key does not match this installation. Do not uninstall Jotdash; install an update signed with the same key.");
    }

    public static Task InstallUpdateAsync(string path) => MainThread.InvokeOnMainThreadAsync(async () =>
    {
        if (Platform.AppContext.PackageManager?.CanRequestPackageInstalls() != true)
        {
            var settings = new Intent(Settings.ActionManageUnknownAppSources,
                Android.Net.Uri.Parse("package:" + Platform.AppContext.PackageName));
            settings.AddFlags(ActivityFlags.NewTask);
            Platform.AppContext.StartActivity(settings);
            throw new InvalidOperationException("Allow Jotdash to install apps in Android Settings, then tap Download and install again.");
        }
        ValidateUpdatePackage(path);
        if (!await Launcher.Default.OpenAsync(new OpenFileRequest
        {
            File = new ReadOnlyFile(path, "application/vnd.android.package-archive"),
        }))
            throw new InvalidOperationException("Android could not open the installer.");
    });

    /// <summary>True when Android may pause Jotdash in the background (battery optimisation on).</summary>
    public static bool IsBatteryRestricted()
    {
        var pm = (Android.OS.PowerManager?)Platform.AppContext.GetSystemService(Context.PowerService);
        return pm is not null && !pm.IsIgnoringBatteryOptimizations(Platform.AppContext.PackageName);
    }

    /// <summary>Asks Android to let Jotdash sync in the background without limits.</summary>
    public static void AskBatteryUnrestricted()
    {
        var intent = new Intent(Settings.ActionRequestIgnoreBatteryOptimizations,
            Android.Net.Uri.Parse("package:" + Platform.AppContext.PackageName));
        intent.AddFlags(ActivityFlags.NewTask);
        Platform.AppContext.StartActivity(intent);
    }
}
