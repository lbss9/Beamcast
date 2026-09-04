using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Beamcast.Net;

/// <summary>Finds the addresses a host can hand to viewers.</summary>
public static class NetworkInfo
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(6) };

    /// <summary>Local IPv4 addresses on interfaces that are up, most likely LAN address first.</summary>
    public static IReadOnlyList<string> LocalAddresses()
    {
        var result = new List<(string Address, int Rank)>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up)
                    continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    continue;

                var rank = nic.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Ethernet => 0,
                    NetworkInterfaceType.Wireless80211 => 1,
                    _ => 2,
                };
                if (nic.Description.Contains("virtual", StringComparison.OrdinalIgnoreCase)
                    || nic.Description.Contains("vmware", StringComparison.OrdinalIgnoreCase)
                    || nic.Description.Contains("hyper-v", StringComparison.OrdinalIgnoreCase))
                    rank += 5;

                foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
                {
                    if (unicast.Address.AddressFamily != AddressFamily.InterNetwork)
                        continue;
                    if (IPAddress.IsLoopback(unicast.Address))
                        continue;
                    result.Add((unicast.Address.ToString(), rank));
                }
            }
        }
        catch (NetworkInformationException) { }

        return result.OrderBy(r => r.Rank).ThenBy(r => r.Address).Select(r => r.Address).Distinct().ToList();
    }

    /// <summary>Asks a public echo service for the address the internet sees. Only call on user request.</summary>
    public static async Task<string?> PublicAddressAsync(CancellationToken ct)
    {
        try
        {
            var text = (await Http.GetStringAsync("https://api.ipify.org", ct).ConfigureAwait(false)).Trim();
            return IPAddress.TryParse(text, out _) ? text : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
