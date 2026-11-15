using Varco.Core;
using Varco.Core.Config;
using Varco.Core.Control;
using Varco.Core.Input;
using Varco.Core.Protocol;

namespace Varco.Tests;

/// <summary>«Rimuovi» cambia la chiave di gruppo: il PC rimosso resta fuori, quelli spenti la ricevono quando si riaccendono.</summary>
public class GroupKeyTests : IAsyncLifetime
{
    private sealed class NoInput : IInputPlatform
    {
        public void Post(Action action) => action();
        public IReadOnlyList<Rect> GetMonitors() => [new Rect(0, 0, 1920, 1080)];
        public (int X, int Y) GetCursorPos() => (0, 0);
        public void SetCursorPos(int x, int y) { }
        public void InjectMoveTo(int x, int y) { }
        public void InjectButton(MouseButton button, bool down) { }
        public void InjectWheel(int delta, bool horizontal) { }
        public void InjectKey(ushort vk, ushort scan, KeyFlags flags) { }
    }

    private readonly List<VarcoHost> _hosts = [];
    private readonly List<string> _dirs = [];
    /// <summary>Porta fissa per ogni PC, come nella realtà (47800): con la porta 0 cambierebbe a ogni riavvio della rete.</summary>
    private readonly Dictionary<string, int> _ports = [];

    private async Task<(VarcoHost Host, ConfigStore Store, string Dir)> NewHost(string name, string? dir = null)
    {
        if (dir is null)
        {
            dir = Path.Combine(Path.GetTempPath(), "varco-test-" + Guid.NewGuid().ToString("N"));
            _dirs.Add(dir);
        }
        var store = new ConfigStore(dir);
        store.Update(c => c.MachineName = name, immediate: true);
        if (!_ports.TryGetValue(dir, out var port)) _ports[dir] = port = FreePort();
        var host = new VarcoHost(store, new NoInput(), tcpPort: port, discoveryPort: 0);
        _hosts.Add(host);
        await host.StartAsync();
        return (host, store, dir);
    }

    private async Task Stop(VarcoHost host)
    {
        _hosts.Remove(host);
        await host.DisposeAsync();
    }

    private static async Task Eventually(Func<bool> cond, int timeoutMs = 10000, string what = "condizione")
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (cond()) return;
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException($"Timeout: {what}");
    }

    private static async Task Pair(VarcoHost from, VarcoHost to)
    {
        var r = await from.HandleAsync(new ControlRequest { Cmd = "pair", Address = $"127.0.0.1:{to.Node!.ListenPort}" });
        Assert.True(r.Ok, r.Error);
        PairRequestDto? req = null;
        await Eventually(() => (req = to.GetStatus().IncomingPair) is not null, what: "richiesta di abbinamento");
        await to.HandleAsync(new ControlRequest { Cmd = "pairAnswer", RequestId = req!.RequestId, Accept = true });
        await Eventually(() => from.GetStatus().OutgoingPair is { State: "Done" }, what: "abbinamento completato");
    }

    private static bool Connected(VarcoHost h, string peer) => h.GetStatus().Peers.Any(p => p.Name == peer && p.State == "Connected");

    [Fact]
    public async Task Removed_pc_stays_out_and_offline_member_gets_the_new_key()
    {
        var (a, sa, _) = await NewHost("PC-A");
        var (b, sb, _) = await NewHost("PC-B");
        var (c, sc, dirC) = await NewHost("PC-C");
        var (x, sx, dirX) = await NewHost("PC-X");
        await Pair(a, b);
        await Pair(a, c);
        await Pair(a, x);
        await Eventually(() => new[] { sb, sc, sx }.All(s => s.Current.GroupKey == sa.Current.GroupKey), what: "stessa chiave per tutti");
        await Eventually(() => Connected(a, "PC-B") && Connected(a, "PC-C") && Connected(a, "PC-X"), what: "tutti collegati ad A");
        var oldKey = sa.Current.GroupKey;
        var idX = sx.Current.MachineId;
        var idC = sc.Current.MachineId;

        // C e X si spengono; X viene rimosso mentre è spento (il pulsante «Rimuovi» c'è solo per i PC non collegati).
        await Stop(c);
        await Stop(x);
        var r = await a.HandleAsync(new ControlRequest { Cmd = "forget", Id = idX });
        Assert.True(r.Ok, r.Error);
        Assert.NotEqual(oldKey, sa.Current.GroupKey);
        Assert.DoesNotContain(sa.Current.KnownPeers, p => p.Id == idX);
        Assert.DoesNotContain(a.Layout.Current.Entries, e => e.Id == idX);

        // B era collegato: riceve subito la chiave nuova e anche lui dimentica X.
        await Eventually(() => sb.Current.GroupKey == sa.Current.GroupKey, what: "B riceve la chiave nuova");
        Assert.DoesNotContain(sb.Current.KnownPeers, p => p.Id == idX);
        await Eventually(() => Connected(a, "PC-B") && Connected(b, "PC-A"), what: "A e B ricollegati con la chiave nuova");
        // La chiave vecchia vale ancora solo per C.
        Assert.All(sa.Current.RetiredKeys, k => Assert.Equal([idC], k.Pending));

        // C si riaccende con la chiave vecchia: si collega lo stesso, riceve la nuova e da lì è un PC come gli altri.
        var (c2, sc2, _) = await NewHost("PC-C", dirC);
        Assert.Equal(oldKey, sc2.Current.GroupKey);
        await Eventually(() => sc2.Current.GroupKey == sa.Current.GroupKey, what: "C riceve la chiave nuova");
        await Eventually(() => Connected(c2, "PC-A") && Connected(a, "PC-C"), what: "C ricollegato ad A");
        Assert.DoesNotContain(sc2.Current.KnownPeers, p => p.Id == idX);
        await Eventually(() => sa.Current.RetiredKeys.Count == 0, what: "chiave vecchia non più accettata da A");

        // X si riaccende con la chiave vecchia: non entra più.
        var (x2, sx2, _) = await NewHost("PC-X", dirX);
        Assert.Equal(oldKey, sx2.Current.GroupKey);
        await Task.Delay(3000);
        Assert.False(x2.GetStatus().Peers.Any(p => p.State == "Connected"), "X non deve ricollegarsi");
        Assert.False(Connected(a, "PC-X") || Connected(c2, "PC-X"));
        Assert.Equal(oldKey, sx2.Current.GroupKey);
    }

    [Fact]
    public async Task Repairing_a_removed_pc_works_again()
    {
        var (a, sa, _) = await NewHost("PC-A");
        var (x, sx, dirX) = await NewHost("PC-X");
        await Pair(a, x);
        await Eventually(() => Connected(a, "PC-X"), what: "collegati");
        var idX = sx.Current.MachineId;
        await Stop(x);
        await a.HandleAsync(new ControlRequest { Cmd = "forget", Id = idX });
        Assert.Contains(idX, sa.Current.RemovedPeers);

        var (x2, sx2, _) = await NewHost("PC-X", dirX);
        await Pair(a, x2);
        await Eventually(() => sx2.Current.GroupKey == sa.Current.GroupKey && Connected(a, "PC-X") && Connected(x2, "PC-A"), what: "X ricollegato");
        Assert.DoesNotContain(idX, sa.Current.RemovedPeers);
    }

    [Fact]
    public void GroupKeyChange_roundtrips()
    {
        var key = new byte[GroupKey.KeyBytes];
        Random.Shared.NextBytes(key);
        var removed = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var m = (GroupKeyChange)MessageCodec.Decode(MessageCodec.Encode(new GroupKeyChange(key, 12345, removed)))!;
        Assert.Equal(key, m.Key);
        Assert.Equal(12345, m.ChangedUtcTicks);
        Assert.Equal(removed, m.Removed);
    }

    private static int FreePort()
    {
        using var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        return ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var h in _hosts.ToList()) await h.DisposeAsync();
        foreach (var d in _dirs) try { Directory.Delete(d, true); } catch { }
    }
}
