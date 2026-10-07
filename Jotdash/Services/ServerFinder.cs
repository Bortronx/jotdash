using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Jotdash.Services;

/// <summary>
/// Finds the sync server on the phone's current Wi-Fi (or VPN /24) by asking every neighbour
/// for http://IP:8126/quicknotes/health. Used by Settings → "Find server", so nobody has to type an IP.
/// </summary>
public static class ServerFinder
{
    public static async Task<List<string>> ScanAsync(CancellationToken ct = default)
    {
        var hosts = Neighbours().Distinct().ToList();
        var found = new ConcurrentBag<string>();
        using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(1.5) })
        {
            Timeout = TimeSpan.FromSeconds(4),
        };
        await Parallel.ForEachAsync(hosts, new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = ct }, async (ip, token) =>
        {
            string url = $"http://{ip}:{ConnectionSettings.DefaultPort}/quicknotes/";
            try
            {
                if ((await http.GetStringAsync(url + "health", token)).Contains("planeConfigured")) found.Add(url);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { } // nothing there
        });
        return found.ToList();
    }

    /// <summary>Every other address in each private /24 the phone is on (home Wi-Fi, NetBird/Tailscale).</summary>
    private static IEnumerable<IPAddress> Neighbours()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback
                || new[] { "rmnet", "ccmni", "pdp", "v4-", "seth" }.Any(nic.Name.StartsWith)) continue; // skip mobile data
            foreach (var a in nic.GetIPProperties().UnicastAddresses)
            {
                byte[] b = a.Address.GetAddressBytes();
                bool isPrivate = a.Address.AddressFamily == AddressFamily.InterNetwork && (b[0] == 10 || (b[0] == 192 && b[1] == 168)
                    || (b[0] == 172 && b[1] is >= 16 and < 32) || (b[0] == 100 && b[1] is >= 64 and < 128));
                if (!isPrivate) continue;
                for (int last = 1; last < 255; last++)
                    if (last != b[3]) yield return new IPAddress(new[] { b[0], b[1], b[2], (byte)last });
            }
        }
    }
}
