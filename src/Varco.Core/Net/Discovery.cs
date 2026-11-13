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
    /// <summary>Annuncio pubblico (non firmato): serve a far comparire i PC non ancora abbinati.</summary>
    private static readonly byte[] AnnounceMagic = "VRCA"u8.ToArray();
    /// <summary>"Richiamami": chiede a un PC di aprire lui la connessione TCP di abbinamento.</summary>
    private static readonly byte[] CallbackMagic = "VRCK"u8.ToArray();
    private const int CallbackSize = 4 + 1 + 16 + 2 + 16 + 16;
    private const int MacSize = 16;

    private readonly LocalIdentity _me;
    private readonly int _port;
    private readonly Func<IEnumerable<IPEndPoint>> _extraTargets;
    private readonly UdpClient _udp;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _sendNow = new(0, 1);

    public TimeSpan Interval { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Ogni quanto inviare un annuncio unicast a tutti gli indirizzi della sottorete (0 = mai).
    /// Serve quando il router non inoltra i broadcast tra cavo e Wi-Fi: l'unicast passa quasi sempre.
    /// </summary>
    public TimeSpan SweepInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Sottoreti più grandi di così (es. /16) non vengono scandite: troppi indirizzi.</summary>
    private const int MinSweepPrefix = 22;

    private long _lastSweep = long.MinValue / 2;
    private volatile bool _sweepSoon = true;
    private readonly Dictionary<IPAddress, long> _lastReply = new();
    private string? _lastNetLog;

    /// <summary>(id, nome, endpoint TCP del peer) — solo PC dello stesso gruppo (beacon firmato).</summary>
    public event Action<Guid, string, IPEndPoint>? PeerSeen;

    /// <summary>(id, nome, endpoint TCP, stesso gruppo?) — qualsiasi PC con Varco nella rete.</summary>
    public event Action<Guid, string, IPEndPoint, bool>? Announced;

    /// <summary>(id di chi chiede, suo endpoint TCP, token) — un PC chiede di essere richiamato per l'abbinamento.</summary>
    public event Action<Guid, IPEndPoint, byte[]>? CallbackRequested;

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
        _sweepSoon = true;
        try { if (_sendNow.CurrentCount == 0) _sendNow.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task SendLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            var nets = LocalNetworks();
            LogNetworks(nets);
            SendBeacons(BroadcastTargets(nets).Concat(_extraTargets()));

            // Scansione unicast: subito all'avvio e dopo un cambio di rete (al massimo ogni 10 s), poi a intervalli regolari.
            var now = Environment.TickCount64;
            var since = now - _lastSweep;
            if (SweepInterval > TimeSpan.Zero && (since >= SweepInterval.TotalMilliseconds || (_sweepSoon && since >= 10_000)))
            {
                _lastSweep = now;
                _sweepSoon = false;
                try { await SweepAsync(nets).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { Log.Debug($"Scansione della sottorete: {ex.Message}"); }
            }

            try { await _sendNow.WaitAsync(Interval, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Annuncio unicast a ogni indirizzo delle sottoreti con gateway (a piccoli gruppi, per non intasare la rete).</summary>
    private async Task SweepAsync(List<LocalNet> nets)
    {
        byte[][] packets = [BuildBeacon(), BuildAnnounce(reply: false)];
        var sent = 0;
        foreach (var net in nets)
        {
            if (!net.HasGateway || net.Prefix < MinSweepPrefix || net.Prefix > 30) continue;
            var self = ToUInt(net.Address);
            var mask = net.Prefix == 0 ? 0u : uint.MaxValue << (32 - net.Prefix);
            var first = (self & mask) + 1;
            var last = (self | ~mask) - 1;
            for (var host = first; host <= last; host++)
            {
                if (host == self) continue;
                var ep = new IPEndPoint(FromUInt(host), _port);
                foreach (var packet in packets)
                {
                    try { _udp.Send(packet, packet.Length, ep); }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
                }
                if (++sent % 32 == 0) await Task.Delay(5, _cts.Token).ConfigureAwait(false);
            }
        }
        if (sent > 0) Log.Debug($"Scansione unicast: {sent} indirizzi");
    }

    private void LogNetworks(List<LocalNet> nets)
    {
        var text = string.Join(", ", nets.Select(n => $"{n.Address}/{n.Prefix} ({n.Name}{(n.HasGateway ? "" : ", senza gateway")})"));
        if (text == _lastNetLog) return;
        _lastNetLog = text;
        Log.Info($"Reti per la ricerca dei PC: {(text.Length == 0 ? "nessuna" : text)}");
    }

    private void SendBeacons(IEnumerable<IPEndPoint> targets, bool reply = false)
    {
        byte[][] packets = [BuildBeacon(), BuildAnnounce(reply)];
        foreach (var t in targets.Distinct())
        {
            foreach (var packet in packets)
            {
                try { _udp.Send(packet, packet.Length, t); }
                catch (Exception ex) { Log.Debug($"Beacon verso {t} fallito: {ex.Message}"); }
            }
        }
    }

    /// <param name="reply">Risposta a un annuncio ricevuto: chi la riceve non risponde a sua volta (niente ping-pong).</param>
    private byte[] BuildAnnounce(bool reply)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(AnnounceMagic);
        w.Write((byte)1);
        w.Write(_me.Id.ToByteArray());
        w.Write((ushort)_me.ListenPort);
        w.Write(_me.GroupTag);
        w.Write(_me.Name.Length > 60 ? _me.Name[..60] : _me.Name);
        // Campo aggiunto in coda: le versioni precedenti lo ignorano.
        w.Write(reply ? (byte)1 : (byte)0);
        w.Flush();
        return ms.ToArray();
    }

    private bool TryParseAnnounce(byte[] buf, out Guid id, out string name, out int tcpPort, out bool sameGroup, out bool isReply)
    {
        id = Guid.Empty; name = ""; tcpPort = 0; sameGroup = false; isReply = false;
        if (buf.Length < 4 + 1 + 16 + 2 + 8 + 1 || !buf.AsSpan(0, 4).SequenceEqual(AnnounceMagic)) return false;
        try
        {
            using var r = new BinaryReader(new MemoryStream(buf), Encoding.UTF8);
            r.ReadBytes(4);
            if (r.ReadByte() != 1) return false;
            id = new Guid(r.ReadBytes(16));
            tcpPort = r.ReadUInt16();
            sameGroup = r.ReadBytes(8).AsSpan().SequenceEqual(_me.GroupTag);
            name = r.ReadString();
            isReply = r.BaseStream.Position < r.BaseStream.Length && r.ReadByte() == 1;
            return tcpPort != 0;
        }
        catch { return false; }
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

            if (TryParseCallback(res.Buffer, out var cid, out var cport, out var ctarget, out var token))
            {
                if (cid == _me.Id || (ctarget != Guid.Empty && ctarget != _me.Id) || !IsPrivate(res.RemoteEndPoint.Address)) continue;
                try { CallbackRequested?.Invoke(cid, new IPEndPoint(res.RemoteEndPoint.Address, cport), token); }
                catch (Exception ex) { Log.Error("CallbackRequested handler", ex); }
                continue;
            }

            if (TryParseAnnounce(res.Buffer, out var aid, out var aname, out var aport, out var same, out var isReply))
            {
                if (aid == _me.Id) continue;
                try { Announced?.Invoke(aid, aname, new IPEndPoint(res.RemoteEndPoint.Address, aport), same); }
                catch (Exception ex) { Log.Error("Announced handler", ex); }
                // Rispondo in unicast: se il broadcast passa in una sola direzione (es. dal cavo al Wi-Fi ma non al contrario),
                // o se mi ha trovato con la scansione, anche l'altro PC mi vede.
                if (!isReply) MaybeReply(res.RemoteEndPoint.Address);
                continue;
            }

            if (!TryParse(res.Buffer, out var id, out var name, out var tcpPort)) continue;
            if (id == _me.Id) continue;
            var ep = new IPEndPoint(res.RemoteEndPoint.Address, tcpPort);
            try { PeerSeen?.Invoke(id, name, ep); }
            catch (Exception ex) { Log.Error("PeerSeen handler", ex); }
        }
    }

    /// <summary>
    /// Chiede al PC <paramref name="target"/> (Guid.Empty = chiunque risponda a quell'indirizzo) di aprire lui
    /// la connessione TCP verso di noi. Serve quando il suo firewall blocca le connessioni in ingresso
    /// (tipico del Wi-Fi classificato come rete "Pubblica"): partendo dalla nostra porta di discovery,
    /// il pacchetto passa come risposta ai suoi beacon.
    /// </summary>
    public void SendCallback(IPAddress address, Guid target, byte[] token)
    {
        var packet = new byte[CallbackSize];
        CallbackMagic.CopyTo(packet, 0);
        packet[4] = 1;
        _me.Id.ToByteArray().CopyTo(packet, 5);
        packet[21] = (byte)(_me.ListenPort >> 8);
        packet[22] = (byte)_me.ListenPort;
        target.ToByteArray().CopyTo(packet, 23);
        token.AsSpan(0, 16).CopyTo(packet.AsSpan(39));
        try { _udp.Send(packet, packet.Length, new IPEndPoint(address, _port)); }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { Log.Debug($"Richiesta di richiamata verso {address} fallita: {ex.Message}"); }
    }

    private static bool TryParseCallback(byte[] buf, out Guid id, out int tcpPort, out Guid target, out byte[] token)
    {
        id = Guid.Empty; tcpPort = 0; target = Guid.Empty; token = [];
        if (buf.Length != CallbackSize || !buf.AsSpan(0, 4).SequenceEqual(CallbackMagic) || buf[4] != 1) return false;
        id = new Guid(buf.AsSpan(5, 16));
        tcpPort = buf[21] << 8 | buf[22];
        target = new Guid(buf.AsSpan(23, 16));
        token = buf.AsSpan(39, 16).ToArray();
        return tcpPort != 0;
    }

    /// <summary>Risponde direttamente a un peer (unicast) per accelerare la scoperta reciproca.</summary>
    public void ReplyTo(IPAddress address) => SendBeacons([new IPEndPoint(address, _port)], reply: true);

    /// <summary>Risposta a un annuncio: al massimo una al secondo per indirizzo e solo verso reti private.</summary>
    private void MaybeReply(IPAddress address)
    {
        if (!IsPrivate(address)) return;
        var now = Environment.TickCount64;
        if (_lastReply.TryGetValue(address, out var last) && now - last < 1000) return;
        if (_lastReply.Count > 256)
            foreach (var old in _lastReply.Where(kv => now - kv.Value > 10_000).Select(kv => kv.Key).ToList()) _lastReply.Remove(old);
        _lastReply[address] = now;
        ReplyTo(address);
    }

    /// <summary>Reti locali e VPN private (RFC 1918, link-local, CGNAT come Tailscale).</summary>
    internal static bool IsPrivate(IPAddress a)
    {
        if (a.IsIPv4MappedToIPv6) a = a.MapToIPv4();
        if (a.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = a.GetAddressBytes();
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
            || b[0] == 127;
    }

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

    internal sealed record LocalNet(string Name, IPAddress Address, int Prefix, bool HasGateway);

    /// <summary>Indirizzi IPv4 di questo PC sulle interfacce attive (cavo, Wi-Fi, adattatori virtuali).</summary>
    internal static List<LocalNet> LocalNetworks()
    {
        var list = new List<LocalNet>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = nic.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var prefix = ua.PrefixLength is > 0 and <= 32 ? ua.PrefixLength
                        : ua.IPv4Mask is { } m ? System.Numerics.BitOperations.PopCount(ToUInt(m)) : 24;
                    list.Add(new LocalNet(nic.Name, ua.Address, prefix, hasGateway));
                }
            }
        }
        catch (Exception ex) { Log.Debug($"Enumerazione interfacce: {ex.Message}"); }
        return list;
    }

    /// <summary>
    /// Broadcast diretto su ogni interfaccia attiva + broadcast globale.
    /// Il broadcast diretto (es. 192.168.1.255) esce dall'interfaccia giusta anche se il PC ha adattatori virtuali
    /// (Hyper-V, VPN), che invece possono "rubare" il broadcast globale 255.255.255.255.
    /// </summary>
    private IEnumerable<IPEndPoint> BroadcastTargets(List<LocalNet> nets)
    {
        var list = new List<IPEndPoint> { new(IPAddress.Broadcast, _port) };
        foreach (var n in nets)
        {
            if (n.Prefix >= 31) continue;
            var mask = n.Prefix == 0 ? 0u : uint.MaxValue << (32 - n.Prefix);
            list.Add(new IPEndPoint(FromUInt(ToUInt(n.Address) | ~mask), _port));
        }
        return list;
    }

    private static uint ToUInt(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static IPAddress FromUInt(uint v) => new([(byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v]);

    public void Dispose()
    {
        _cts.Cancel();
        _udp.Dispose();
    }
}
