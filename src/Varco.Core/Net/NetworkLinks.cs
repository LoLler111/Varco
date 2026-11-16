using System.Net;
using System.Net.NetworkInformation;

namespace Varco.Core.Net;

/// <summary>Da quale scheda di rete passa una connessione: cavo o Wi-Fi (per la diagnostica).</summary>
public static class NetworkLinks
{
    private static readonly object Gate = new();
    private static NetworkInterface[] _cache = [];
    private static long _cachedAt = long.MinValue / 2;

    /// <summary>"Wi-Fi", "Cavo", "Stesso PC"… e nome della scheda; null se l'indirizzo non è di nessuna scheda.</summary>
    public static (string Kind, string Adapter)? Describe(IPAddress? local)
    {
        if (local is null) return null;
        if (local.IsIPv4MappedToIPv6) local = local.MapToIPv4();
        if (IPAddress.IsLoopback(local)) return ("Stesso PC", "loopback");
        foreach (var nic in Interfaces())
        {
            IPInterfaceProperties props;
            try { props = nic.GetIPProperties(); }
            catch (NetworkInformationException) { continue; }
            if (!props.UnicastAddresses.Any(a => a.Address.Equals(local))) continue;
            var kind = nic.NetworkInterfaceType switch
            {
                NetworkInterfaceType.Wireless80211 => "Wi-Fi",
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT
                    or NetworkInterfaceType.FastEthernetFx or NetworkInterfaceType.Ethernet3Megabit => "Cavo",
                NetworkInterfaceType.Ppp or NetworkInterfaceType.Tunnel => "VPN",
                _ => nic.NetworkInterfaceType.ToString(),
            };
            // Le schede virtuali (Hyper-V, VPN) si dichiarano "Ethernet": il nome aiuta a capire.
            return (kind, nic.Name);
        }
        return null;
    }

    private static NetworkInterface[] Interfaces()
    {
        lock (Gate)
        {
            if (Environment.TickCount64 - _cachedAt > 30_000)
            {
                try { _cache = NetworkInterface.GetAllNetworkInterfaces(); }
                catch (NetworkInformationException) { _cache = []; }
                _cachedAt = Environment.TickCount64;
            }
            return _cache;
        }
    }
}
