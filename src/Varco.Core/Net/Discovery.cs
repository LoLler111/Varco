using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Varco.Core.Crypto;

namespace Varco.Core.Net;

/// <summary>
/// Scoperta dei PC del gruppo via broadcast UDP.
/// I PC vengono identificati per ID (GUID) e non per nome macchina: niente dipendenza da DNS/NetBIOS.
/// Ogni beacon è firmato con HMAC della chiave di gruppo, quindi gli estranei vengono ignorati.
/// Funziona tra cavo e Wi-Fi purché siano sulla stessa rete locale (stessa sottorete).
/// </summary>
public sealed class Discovery : IDisposable
{
    private static readonly byte[] Magic = "VRCB"u8.ToArray();
    private const int MacSize = 16;

    private readonly LocalIdentity _me;
    private readonly int _port;
    private readonly Func<IEnumerable<IPEndPoint>> _extraTargets;
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendNow = new(0, 1);

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>(id, nome, endpoint TCP del peer)</summary>
    public event Action<Guid, string, IPEndPoint>? PeerSeen;

    public Discovery(LocalIdentity me, int port, Func<IEnumerable<IPEndPoint>> extraTargets)
    {
        _me = me;
        _port = port;
        _extraTargets = extraTargets;
        _udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        try
        {
            // Evita che un ICMP "port unreachable" faccia fallire le Receive successive (SIO_UDP_CONNRESET).
            const int SIO_UDP_CONNRESET = -1744830452;
            _udp.Client.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null);
        }
        catch { }
    }

    public void Start()
    {
        _ = Task.Run(ReceiveLoop);
        _ = Task.Run(SendLoop);
    }

    /// <summary>Invia subito un beacon (es. dopo un cambio di rete o il risveglio).</summary>
    public void Announce()
    {
        try { if (_sendNow.CurrentCount == 0) _sendNow.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task SendLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            SendBeacons(BroadcastTargets().Concat(_extraTargets()));
            try { await _sendNow.WaitAsync(Interval, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void SendBeacons(IEnumerable<IPEndPoint> targets)
    {
        var packet = BuildBeacon();
        foreach (var t in targets.Distinct())
        {
            try { _udp.Send(packet, packet.Length, t); }
            catch (Exception ex) { Log.Debug($"Beacon verso {t} fallito: {ex.Message}"); }
        }
    }

    private async Task ReceiveLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            UdpReceiveResult res;
            try { res = await _udp.ReceiveAsync(_cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                Log.Debug($"Discovery receive: {ex.SocketErrorCode}");
                continue;
            }

            if (!TryParse(res.Buffer, out var id, out var name, out var tcpPort)) continue;
            if (id == _me.Id) continue;
            var ep = new IPEndPoint(res.RemoteEndPoint.Address, tcpPort);
            try { PeerSeen?.Invoke(id, name, ep); }
            catch (Exception ex) { Log.Error("PeerSeen handler", ex); }
        }
    }

    /// <summary>Risponde direttamente a un peer (unicast) per accelerare la scoperta reciproca.</summary>
    public void ReplyTo(IPAddress address) => SendBeacons([new IPEndPoint(address, _port)]);

    private byte[] BuildBeacon()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Magic);
        w.Write((byte)1);
        w.Write(_me.GroupTag);
        w.Write(_me.Id.ToByteArray());
        w.Write((ushort)_me.ListenPort);
        w.Write(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        w.Write(_me.Name.Length > 60 ? _me.Name[..60] : _me.Name);
        w.Flush();
        var body = ms.ToArray();
        var mac = HMACSHA256.HashData(_me.GroupKey, body)[..MacSize];
        return [.. body, .. mac];
    }

    private bool TryParse(byte[] buf, out Guid id, out string name, out int tcpPort)
    {
        id = Guid.Empty; name = ""; tcpPort = 0;
        if (buf.Length < Magic.Length + MacSize + 8 || !buf.AsSpan(0, 4).SequenceEqual(Magic)) return false;
        var body = buf.AsSpan(0, buf.Length - MacSize);
        var mac = buf.AsSpan(buf.Length - MacSize);
        if (!CryptographicOperations.FixedTimeEquals(HMACSHA256.HashData(_me.GroupKey, body)[..MacSize], mac))
            return false; // altro gruppo o pacchetto contraffatto
        try
        {
            using var r = new BinaryReader(new MemoryStream(buf, 0, body.Length), Encoding.UTF8);
            r.ReadBytes(4);
            if (r.ReadByte() != 1) return false;
            r.ReadBytes(8); // tag di gruppo (già coperto dall'HMAC)
            id = new Guid(r.ReadBytes(16));
            tcpPort = r.ReadUInt16();
            r.ReadInt64(); // timestamp, informativo
            name = r.ReadString();
            return true;
        }
        catch { return false; }
    }

    /// <summary>Broadcast diretto su ogni interfaccia attiva + broadcast globale.</summary>
    private IEnumerable<IPEndPoint> BroadcastTargets()
    {
        var list = new List<IPEndPoint> { new(IPAddress.Broadcast, _port) };
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                foreach (var ua in nic.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                    var ip = ua.Address.GetAddressBytes();
                    var mask = ua.IPv4Mask.GetAddressBytes();
                    var bc = new byte[4];
                    for (int i = 0; i < 4; i++) bc[i] = (byte)(ip[i] | ~mask[i]);
                    list.Add(new IPEndPoint(new IPAddress(bc), _port));
                }
            }
        }
        catch (Exception ex) { Log.Debug($"Enumerazione interfacce: {ex.Message}"); }
        return list;
    }

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
    }
}
