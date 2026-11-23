using Varco.Core.Config;
using Varco.Core.Input;
using Varco.Core.Net;
using Varco.Core.Protocol;

namespace Varco.Tests;

public class MediaKeyRouterTests
{
    private static readonly Guid Self = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();
    private static bool All(Guid _) => true;

    [Fact]
    public void Keys_stay_where_the_keyboard_goes_when_nothing_plays()
    {
        var r = new MediaKeyRouter(Self);
        Assert.Equal(Self, r.Route(Self, 1000, All));
        Assert.Equal(B, r.Route(B, 1000, All));
    }

    [Fact]
    public void Keys_go_to_the_pc_that_plays_and_to_the_latest_one_to_start()
    {
        var r = new MediaKeyRouter(Self);
        r.SetPlaying(B, true, 1000);
        Assert.Equal(B, r.Route(Self, 2000, All));
        r.SetPlaying(C, true, 3000);
        Assert.Equal(C, r.Route(Self, 4000, All));
        // Se suona anche il PC dove va la tastiera, restano lì.
        r.SetPlaying(Self, true, 5000);
        Assert.Equal(Self, r.Route(Self, 6000, All));
    }

    [Fact]
    public void After_pausing_the_keys_still_go_to_the_last_player_for_a_while()
    {
        var r = new MediaKeyRouter(Self);
        r.SetPlaying(B, true, 1000);
        r.SetPlaying(B, false, 2000); // messa in pausa: il prossimo play/pausa la deve riprendere
        Assert.Equal(B, r.Route(Self, 3000, All));
        Assert.Equal(Self, r.Route(Self, 2000 + MediaKeyRouter.RecentMs + 1, All));
    }

    [Fact]
    public void Disconnected_or_forgotten_pcs_get_no_keys()
    {
        var r = new MediaKeyRouter(Self);
        r.SetPlaying(B, true, 1000);
        Assert.Equal(Self, r.Route(Self, 2000, id => id != B));
        r.Forget(B);
        Assert.False(r.Knows(B));
        Assert.Equal(Self, r.Route(Self, 2000, All));
    }
}

public class GroupLockPolicyTests
{
    [Fact]
    public void Only_a_lock_by_the_person_at_the_pc_spreads()
    {
        var p = new GroupLockPolicy();
        Assert.True(p.ShouldPropagate(10_000, 9_000)); // Win+L appena premuto
        Assert.False(p.ShouldPropagate(100_000, 9_000)); // blocco per inattività
        Assert.False(p.ShouldPropagate(10_000, 0)); // mai toccato da qui
    }

    [Fact]
    public void A_lock_asked_by_another_pc_is_not_sent_back()
    {
        var p = new GroupLockPolicy();
        p.OnRemoteLock(10_000);
        Assert.False(p.ShouldPropagate(10_100, 10_050));
        Assert.True(p.ShouldPropagate(10_000 + GroupLockPolicy.EchoWindowMs + 1000, 10_000 + GroupLockPolicy.EchoWindowMs + 500));
    }
}

public class NewMessageCodecTests
{
    [Fact]
    public void New_messages_survive_the_codec()
    {
        Message[] messages =
        [
            new LockScreen(), new AudioActive(true), new MediaKeyMsg(0xB3, 0x22, KeyFlags.Extended | KeyFlags.Up),
            new PointerScale(144), new RelayOpen(Guid.NewGuid(), Guid.NewGuid(), true), new RelayClose(Guid.NewGuid()),
        ];
        foreach (var m in messages) Assert.Equal(m, MessageCodec.Decode(MessageCodec.Encode(m)));

        PeerRef[] peers = [new(Guid.NewGuid(), "UFFICIO"), new(Guid.NewGuid(), "PORTATILE")];
        Assert.Equal(peers, Assert.IsType<PeerList>(MessageCodec.Decode(MessageCodec.Encode(new PeerList(peers)))).Connected);
        var data = new RelayData(Guid.NewGuid(), [1, 2, 3]);
        var back = Assert.IsType<RelayData>(MessageCodec.Decode(MessageCodec.Encode(data)));
        Assert.Equal(data.Tunnel, back.Tunnel);
        Assert.Equal(data.Data, back.Data);
    }
}

public partial class CoordinatorTests
{
    private const ushort VolumeUp = 0xAF, PlayPause = 0xB3;

    [Fact]
    public void Media_key_goes_to_the_pc_that_plays()
    {
        var (c, net, p, other) = Setup();
        var router = new MediaKeyRouter(net.SelfId);
        c.MediaRouter = router;
        router.SetPlaying(other, true, Environment.TickCount64);

        Assert.True(c.OnLocalKey(VolumeUp, 0x30, KeyFlags.Extended)); // non arriva al PC locale
        router.SetPlaying(other, false, Environment.TickCount64); // anche se la musica si ferma, il rilascio segue la pressione
        Assert.True(c.OnLocalKey(VolumeUp, 0x30, KeyFlags.Extended | KeyFlags.Up));
        Assert.Equal(
            [new MediaKeyMsg(VolumeUp, 0x30, KeyFlags.Extended), new MediaKeyMsg(VolumeUp, 0x30, KeyFlags.Extended | KeyFlags.Up)],
            net.Sent.Where(s => s.To == other).Select(s => s.Msg).OfType<MediaKeyMsg>().ToList());
        Assert.Equal(InputMode.Local, c.Mode);
    }

    [Fact]
    public void Media_key_stays_here_when_nothing_plays_or_the_option_is_off()
    {
        var (c, net, _, other) = Setup();
        var router = new MediaKeyRouter(net.SelfId);
        c.MediaRouter = router;
        Assert.False(c.OnLocalKey(PlayPause, 0x22, KeyFlags.Extended));
        Assert.False(c.OnLocalKey(PlayPause, 0x22, KeyFlags.Extended | KeyFlags.Up));

        router.SetPlaying(other, true, Environment.TickCount64);
        c.MediaKeysFollowAudio = () => false;
        Assert.False(c.OnLocalKey(PlayPause, 0x22, KeyFlags.Extended));
        Assert.False(c.OnLocalKey(PlayPause, 0x22, KeyFlags.Extended | KeyFlags.Up));
        Assert.DoesNotContain(net.Sent, s => s.Msg is MediaKeyMsg);
    }

    [Fact]
    public void While_controlling_media_keys_stay_on_this_pc_if_the_music_plays_here()
    {
        var (c, net, _, other) = Setup();
        var router = new MediaKeyRouter(net.SelfId);
        c.MediaRouter = router;
        router.SetPlaying(net.SelfId, true, Environment.TickCount64);
        c.OnLocalMouseMove(1919, 540);
        Assert.Equal(InputMode.Controlling, c.Mode);
        Assert.False(c.OnLocalKey(VolumeUp, 0x30, KeyFlags.Extended)); // al PC locale, non al target
        Assert.False(c.OnLocalKey(VolumeUp, 0x30, KeyFlags.Extended | KeyFlags.Up));
        Assert.DoesNotContain(net.Sent, s => s.To == other && s.Msg is KeyMsg or MediaKeyMsg);

        // Le altre lettere vanno al target come sempre.
        Assert.True(c.OnLocalKey(0x41, 0x1E, KeyFlags.None));
        Assert.Contains(net.Sent, s => s.To == other && s.Msg is KeyMsg { Vk: 0x41 });
    }

    [Fact]
    public void While_controlling_media_keys_go_to_the_target_as_usual_when_it_plays()
    {
        var (c, net, _, other) = Setup();
        var router = new MediaKeyRouter(net.SelfId);
        c.MediaRouter = router;
        router.SetPlaying(other, true, Environment.TickCount64);
        c.OnLocalMouseMove(1919, 540);
        Assert.True(c.OnLocalKey(PlayPause, 0x22, KeyFlags.Extended));
        Assert.Contains(net.Sent, s => s.To == other && s.Msg is KeyMsg { Vk: PlayPause });
        Assert.DoesNotContain(net.Sent, s => s.Msg is MediaKeyMsg);
    }

    [Fact]
    public void Received_media_key_is_injected_even_when_not_controlled()
    {
        var (c, _, p, other) = Setup();
        c.OnMessage(other, new MediaKeyMsg(PlayPause, 0x22, KeyFlags.Extended), () => []);
        Assert.Equal(InputMode.Local, c.Mode);
        Assert.Contains($"key {PlayPause:X2} {KeyFlags.Extended}", p.Log);
    }

    [Fact]
    public void Target_scales_moves_by_the_ratio_of_the_two_scales()
    {
        var (c, _, p, other) = Setup();
        c.OnMessage(other, new Enter(Edge.Left, 0.5f), () => []);
        c.OnMessage(other, new PointerScale(96), () => []);
        p.Dpi = 144; // 150% qui, 100% sul PC che comanda
        var (x, y) = p.Cursor;
        c.OnMessage(other, new MouseMove(10, 4), () => []);
        Assert.Equal((x + 15, y + 6), p.Cursor);
        // Le frazioni non si perdono: due movimenti da 1 pixel diventano 3.
        c.OnMessage(other, new MouseMove(1, 0), () => []);
        c.OnMessage(other, new MouseMove(1, 0), () => []);
        Assert.Equal((x + 18, y + 6), p.Cursor);
    }

    [Fact]
    public void Without_the_scale_message_moves_stay_pixel_exact()
    {
        var (c, _, p, other) = Setup();
        c.OnMessage(other, new Enter(Edge.Left, 0.5f), () => []);
        p.Dpi = 144;
        var (x, y) = p.Cursor;
        c.OnMessage(other, new MouseMove(10, 4), () => []);
        Assert.Equal((x + 10, y + 4), p.Cursor);
    }

    [Fact]
    public void Controller_sends_its_scale_only_with_the_option_on()
    {
        var (c, net, p, other) = Setup();
        p.Dpi = 120;
        c.OnLocalMouseMove(1919, 540);
        Assert.DoesNotContain(net.Sent, s => s.Msg is PointerScale);

        var (c2, net2, p2, other2) = Setup();
        c2.PointerScaleCompensation = () => true;
        p2.Dpi = 120;
        c2.OnLocalMouseMove(1919, 540);
        Assert.Equal(new PointerScale(120), net2.Sent.Where(s => s.To == other2).Select(s => s.Msg).Last());
    }
}

/// <summary>Tre nodi reali su loopback: A e C non si conoscono gli indirizzi, ma tutti e due vedono B.</summary>
public class RelayTests : IAsyncLifetime
{
    private readonly byte[] _key;
    private readonly List<NetworkNode> _nodes = [];

    public RelayTests()
    {
        GroupKey.TryParse(GroupKey.Generate(), out _key);
    }

    private NetworkNode NewNode(string name, Guid id, IReadOnlyList<(Guid, string, IReadOnlyList<string>)> known, params string[] manual)
    {
        var n = new NetworkNode(id, name, _key, new NodeOptions
        {
            TcpPort = 0,
            DiscoveryPort = 0,
            HeartbeatIntervalMs = 100,
            HeartbeatTimeoutMs = 1500,
            BackoffMinMs = 100,
            BackoffMaxMs = 500,
            ConnectTimeoutMs = 500,
            HandshakeTimeoutMs = 1000,
            ManualAddresses = manual,
            KnownPeers = known,
            RelayAfterMs = 500,
            RelayRetryMs = 500,
        });
        _nodes.Add(n);
        return n;
    }

    private static async Task Eventually(Func<bool> cond, int timeoutMs = 8000, string what = "condizione")
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (cond()) return;
            await Task.Delay(25);
        }
        Assert.Fail($"Timeout: {what}");
    }

    private (NetworkNode A, NetworkNode B, NetworkNode C) Triangle()
    {
        var b = NewNode("B", Guid.NewGuid(), []);
        Guid idA = Guid.NewGuid(), idC = Guid.NewGuid();
        var a = NewNode("A", idA, [(idC, "C", [])], $"127.0.0.1:{b.ListenPort}");
        var c = NewNode("C", idC, [(idA, "A", [])], $"127.0.0.1:{b.ListenPort}");
        return (a, b, c);
    }

    [Fact]
    public async Task Pcs_that_cannot_see_each_other_connect_through_a_third()
    {
        var (a, b, c) = Triangle();
        var got = new List<Message>();
        c.MessageReceived += (from, m) => { if (from == a.Me.Id) lock (got) got.Add(m); };
        a.Start(); b.Start(); c.Start();

        await Eventually(() => a.IsConnected(c.Me.Id) && c.IsConnected(a.Me.Id), what: "collegamento tramite B");
        Assert.Equal(b.Me.Id, a.RelayedVia(c.Me.Id));
        Assert.Equal(b.Me.Id, c.RelayedVia(a.Me.Id));
        Assert.False(a.IsDirectlyConnected(c.Me.Id));
        Assert.Contains(a.GetStatus(), s => s.Id == c.Me.Id && s.Via == "B");

        // Un messaggio piccolo e uno più grande del pezzo massimo del tunnel arrivano interi e in ordine.
        var big = new byte[700_000];
        Random.Shared.NextBytes(big);
        a.Send(c.Me.Id, new MouseWheel(120, false));
        a.Send(c.Me.Id, new DragFiles(big));
        await Eventually(() => { lock (got) return got.Count >= 2; }, what: "messaggi tramite B");
        lock (got)
        {
            Assert.Equal(new MouseWheel(120, false), got[0]);
            Assert.Equal(big, Assert.IsType<DragFiles>(got[1]).Offer);
        }

        // B si spegne: A e C se ne accorgono subito, senza aspettare il timeout dell'heartbeat.
        var t0 = Environment.TickCount64;
        await b.DisposeAsync();
        await Eventually(() => !a.IsConnected(c.Me.Id) && !c.IsConnected(a.Me.Id), 3000, "tunnel chiuso con B");
        Assert.True(Environment.TickCount64 - t0 < 1500, "la chiusura deve arrivare prima del timeout dell'heartbeat");
    }

    [Fact]
    public async Task Pcs_that_never_met_learn_each_other_from_the_third()
    {
        var b = NewNode("B", Guid.NewGuid(), []);
        var a = NewNode("A", Guid.NewGuid(), [], $"127.0.0.1:{b.ListenPort}");
        var c = NewNode("C", Guid.NewGuid(), [], $"127.0.0.1:{b.ListenPort}");
        a.Start(); b.Start(); c.Start();
        await Eventually(() => a.IsConnected(c.Me.Id) && c.IsConnected(a.Me.Id), what: "collegamento tramite B");
        Assert.Contains(a.GetStatus(), s => s.Id == c.Me.Id && s.Name == "C" && s.Via == "B");
    }

    [Fact]
    public async Task A_direct_connection_replaces_the_relay()
    {
        var (a, b, c) = Triangle();
        a.Start(); b.Start(); c.Start();
        await Eventually(() => a.IsConnected(c.Me.Id) && a.RelayedVia(c.Me.Id) is not null, what: "collegamento tramite B");

        // Ora A trova l'indirizzo di C (es. il router torna a passare i broadcast).
        a.AddPeerHint(c.Me.Id, "C", new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, c.ListenPort));
        await Eventually(() => a.IsDirectlyConnected(c.Me.Id) && c.IsDirectlyConnected(a.Me.Id), what: "collegamento diretto");
        await Task.Delay(500);
        Assert.True(a.IsDirectlyConnected(c.Me.Id)); // e resta diretto
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var n in _nodes) await n.DisposeAsync();
    }
}
