// Builds the Blinkjot Android app into dist/blinkjot.apk.
// Usage: dotnet run build-apk.cs        (needs the .NET MAUI Android workload, see README.md)
using System.Diagnostics;

string here = (Environment.GetEnvironmentVariable("DOTNET_RUNFILE") ?? AppContext.GetData("EntryPointFilePath") as string) is string f
    ? Path.GetDirectoryName(f)! : Directory.GetCurrentDirectory();
string project = Path.Combine(here, "Blinkjot", "Blinkjot.csproj");
string outDir = Path.Combine(here, "Blinkjot", "bin", "apk");
string dist = Path.Combine(here, "dist");

// The MAUI workload can live in a user-local SDK (no admin rights needed) – use it when present.
string localSdk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "dotnet-maui");
string dotnet = File.Exists(Path.Combine(localSdk, "dotnet.exe")) ? Path.Combine(localSdk, "dotnet.exe") : "dotnet";
string androidSdk = Environment.GetEnvironmentVariable("ANDROID_HOME")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk");

if (Environment.GetEnvironmentVariable("BLINKJOT_KEYSTORE") is null)
    Console.WriteLine("Note: BLINKJOT_KEYSTORE is not set, so this APK is signed with the debug key (fine for testing, but it can't update a release install).");

try
{
    var psi = new ProcessStartInfo(dotnet) { WorkingDirectory = Path.GetDirectoryName(project)! };
    foreach (string a in new[] { "publish", project, "-f", "net10.0-android", "-c", "Release", "-o", outDir, $"-p:AndroidSdkDirectory={androidSdk}" })
        psi.ArgumentList.Add(a);
    if (dotnet != "dotnet")
    {
        psi.Environment["DOTNET_ROOT"] = localSdk;
        psi.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
    }
    Console.WriteLine($"Building with {dotnet} …");
    using (var proc = Process.Start(psi)!)
    {
        proc.WaitForExit();
        if (proc.ExitCode != 0) { Console.Error.WriteLine("Build failed."); return proc.ExitCode; }
    }

    string? apk = Directory.GetFiles(outDir, "*-Signed.apk").FirstOrDefault();
    if (apk is null) { Console.Error.WriteLine($"No signed .apk found in {outDir}"); return 1; }
    Directory.CreateDirectory(dist);
    string target = Path.Combine(dist, "blinkjot.apk");
    File.Copy(apk, target, overwrite: true);
    Console.WriteLine($"✔ {target} ({new FileInfo(target).Length / 1024 / 1024} MB)");
    return 0;
}
catch (Exception e)
{
    Console.Error.WriteLine($"Failed: {e.Message}");
    return 1;
}
