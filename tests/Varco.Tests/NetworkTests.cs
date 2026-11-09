using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Varco.Core.Config;
using Varco.Core.Crypto;
using Varco.Core.Net;
using Varco.Core.Protocol;

namespace Varco.Tests;

public class CryptoTests
{
    [Fact]
    public void GroupKey_roundtrip_and_tolerates_formatting()
    {
        var key = GroupKey.Generate();
        Assert.Matches("^([A-Z2-9]{4}-){7}[A-Z2-9]{4}$", key);
        Assert.True(GroupKey.TryParse(key.ToLowerInvariant().Replace("-", " "), out var bytes));
        Assert.Equal(key, GroupKey.Format(bytes));
        Assert.False(GroupKey.TryParse(key[..^1], out _));
    }

    [Fact]
    public async Task Handshake_same_key_ok_and_frames_roundtrip()
    {
        var key = GroupKey.TryParse(GroupKey.Generate(), out var k) ? k : throw new();
        var (a, b) = await Pair();
        var ia = new LocalIdentity(Guid.NewGuid(), "A", 1, key);
        var ib = new LocalIdentity(Guid.NewGuid(), "B", 2, key);
        var ta = Handshake.RunClientAsync(a, ia, CancellationToken.None);
        var tb = Handshake.RunServerAsync(b, ib, CancellationToken.None);
        var (ra, rb) = (await ta, await tb);
        Assert.Equal(ib.Id, ra.PeerId);
        Assert.Equal(ia.Id, rb.PeerId);
        Assert.Equal(ra.SessionId, rb.SessionId);

        var plain = MessageCodec.Encode(new KeyMsg(0x41, 0x1E, KeyFlags.Up));
        Assert.Equal(new KeyMsg(0x41, 0x1E, KeyFlags.Up), MessageCodec.Decode(rb.Cipher.Open(ra.Cipher.Seal(plain))));
        Assert.Equal(new MouseMove(-3, 7), MessageCodec.Decode(ra.Cipher.Open(rb.Cipher.Seal(MessageCodec.Encode(new MouseMove(-3, 7))))));
    }

    [Fact]
    public async Task Handshake_different_key_fails()
    {
        GroupKey.TryParse(GroupKey.Generate(), out var k1);
        GroupKey.TryParse(GroupKey.Generate(), out var k2);
        var (a, b) = await Pair();
        var ta = Handshake.RunClientAsync(a, new LocalIdentity(Guid.NewGuid(), "A", 1, k1), CancellationToken.None);
        var tb = Handshake.RunServerAsync(b, new LocalIdentity(Guid.NewGuid(), "B", 2, k2), CancellationToken.None);
        await Assert.ThrowsAsync<HandshakeException>(() => tb);
        a.Dispose();
        await Assert.ThrowsAnyAsync<Exception>(() => ta);
    }

    private static async Task<(NetworkStream, NetworkStream)> Pair()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var c = new TcpClient();
        var accept = l.AcceptTcpClientAsync();
        await c.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)l.LocalEndpoint).Port);
        var s = await accept;
        l.Stop();
        return (c.GetStream(), s.GetStream());
    }
}

/// <summary>Due nodi reali su loopback: il punto è verificare che si ritrovino SEMPRE da soli.</summary>
public class ReconnectTests : IAsyncLifetime
{
    private readonly byte[] _key;
    private readonly List<NetworkNode> _nodes = [];

    public ReconnectTests()
    {
        GroupKey.TryParse(GroupKey.Generate(), out _key);
    }

    private NetworkNode NewNode(string name, Guid? id = null, int port = 0, params string[] manual)
    {
        var n = new NetworkNode(id ?? Guid.NewGuid(), name, _key, new NodeOptions
        {
            TcpPort = port,
            DiscoveryPort = 0,
            HeartbeatIntervalMs = 100,
            HeartbeatTimeoutMs = 600,
            BackoffMinMs = 100,
            BackoffMaxMs = 500,
            ConnectTimeoutMs = 500,
            HandshakeTimeoutMs = 1000,
            ManualAddresses = manual,
        });
        _nodes.Add(n);
        return n;
    }

    private static async Task Eventually(Func<bool> cond, int timeoutMs = 5000, string what = "condizione")
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (cond()) return;
            await Task.Delay(25);
        }
        Assert.Fail($"Timeout: {what}");
    }

    [Fact]
    public async Task Connects_and_exchanges_messages()
    {
        var b = NewNode("B");
        var a = NewNode("A", manual: $"127.0.0.1:{b.ListenPort}");
        var got = new TaskCompletionSource<Message>();
        b.MessageReceived += (_, m) => got.TrySetResult(m);
        a.Start(); b.Start();

        await Eventually(() => a.IsConnected(b.Me.Id) && b.IsConnected(a.Me.Id), what: "connessione iniziale");
        a.Send(b.Me.Id, new MouseWheel(120, false));
        Assert.Equal(new MouseWheel(120, false), await got.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task Reconnects_after_peer_restart_on_same_port()
    {
        var bId = Guid.NewGuid();
        var b = NewNode("B", bId);
        var port = b.ListenPort;
        var a = NewNode("A", manual: $"127.0.0.1:{port}");
        a.Start(); b.Start();
        await Eventually(() => a.IsConnected(bId), what: "connessione iniziale");

        // B "si spegne"…
        await b.DisposeAsync();
        await Eventually(() => !a.IsConnected(bId), what: "rilevamento disconnessione");

        // …e torna: A deve ritrovarlo senza alcun intervento.
        await Task.Delay(300);
        var b2 = NewNode("B", bId, port);
        b2.Start();
        await Eventually(() => a.IsConnected(bId) && b2.IsConnected(a.Me.Id), what: "riconnessione automatica");
    }

    [Fact]
    public async Task Peer_that_only_knows_us_from_inbound_reconnects_back()
    {
        // B non ha indirizzi manuali: impara l'indirizzo di A dalla connessione in ingresso
        // e deve riuscire a ricollegarsi lui se A si riavvia.
        var aId = Guid.NewGuid();
        var a = NewNode("A", aId);
        var aPort = a.ListenPort;
        var b = NewNode("B");
        var c = NewNode("C", manual: $"127.0.0.1:{b.ListenPort}"); // C esiste solo per non lasciare B isolato
        a.Start(); b.Start(); c.Start();

        // A si presenta a B una volta tramite un nodo temporaneo che conosce B
        await a.DisposeAsync();
        var aTmp = NewNode("A", aId, aPort, $"127.0.0.1:{b.ListenPort}");
        aTmp.Start();
        await Eventually(() => b.IsConnected(aId), what: "prima connessione A→B");

        // A si riavvia SENZA conoscere B: deve essere B a ricomporre il numero.
        await aTmp.DisposeAsync();
        await Eventually(() => !b.IsConnected(aId), what: "disconnessione");
        var a2 = NewNode("A", aId, aPort);
        a2.Start();
        await Eventually(() => b.IsConnected(aId) && a2.IsConnected(b.Me.Id), what: "B ricollega A");
    }

    [Fact]
    public async Task Half_open_connection_is_detected_by_heartbeat()
    {
        var b = NewNode("B");
        var a = NewNode("A", manual: $"127.0.0.1:{b.ListenPort}");
        var disconnected = new TaskCompletionSource();
        a.PeerDisconnected += (_, _) => disconnected.TrySetResult();
        a.Start(); b.Start();
        await Eventually(() => a.IsConnected(b.Me.Id), what: "connessione iniziale");

        // Simula un PC "congelato" (standby, cavo staccato): il socket resta aperto ma non arriva più nulla.
        FreezeConnections(b);
        await disconnected.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Simultaneous_dial_ends_with_exactly_one_connection_each_side()
    {
        var aId = Guid.NewGuid();
        var bId = Guid.NewGuid();
        // porte note prima: ognuno conosce l'altro e compongono insieme
        var pa = FreePort();
        var pb = FreePort();
        var a = NewNode("A", aId, pa, $"127.0.0.1:{pb}");
        var b = NewNode("B", bId, pb, $"127.0.0.1:{pa}");
        var aConnects = 0;
        var aDisconnects = 0;
        a.PeerConnected += _ => Interlocked.Increment(ref aConnects);
        a.PeerDisconnected += (_, _) => Interlocked.Increment(ref aDisconnects);
        a.Start(); b.Start();

        await Eventually(() => a.IsConnected(bId) && b.IsConnected(aId), what: "connessione");
        await Task.Delay(1500); // lascia esaurire eventuali gare
        Assert.True(a.IsConnected(bId) && b.IsConnected(aId));

        // Stabilità: messaggi passano e nessuna disconnessione "spuria" dopo l'assestamento.
        var before = aDisconnects;
        var got = new TaskCompletionSource();
        b.MessageReceived += (_, m) => { if (m is Leave) got.TrySetResult(); };
        a.Send(bId, new Leave());
        await got.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(800);
        Assert.Equal(before, aDisconnects);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    /// <summary>Blocca invio e lettura delle connessioni di un nodo senza chiudere i socket.</summary>
    private static void FreezeConnections(NetworkNode node)
    {
        var conns = (Dictionary<Guid, PeerConnection>)typeof(NetworkNode)
            .GetField("_conns", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(node)!;
        foreach (var c in conns.Values)
        {
            var cts = (CancellationTokenSource)typeof(PeerConnection)
                .GetField("_cts", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(c)!;
            // Fermo i loop ma lascio il socket aperto: dall'altra parte sembra vivo ma muto.
            typeof(PeerConnection).GetField("_closing", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(c, 1);
            cts.Cancel();
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var n in _nodes) await n.DisposeAsync();
    }
}
