using Varco.Core;
using Varco.Core.Config;
using Varco.Core.Control;
using Varco.Core.Input;
using Varco.Core.Protocol;

namespace Varco.Tests;

/// <summary>Abbinamento reale fra due host su loopback (senza discovery UDP: si usa l'indirizzo).</summary>
public class PairingTests : IAsyncLifetime
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

    private readonly List<(VarcoHost Host, string Dir)> _hosts = [];

    private async Task<(VarcoHost Host, ConfigStore Store)> NewHost(string name)
    {
        var dir = Path.Combine(Path.GetTempPath(), "varco-test-" + Guid.NewGuid().ToString("N"));
        var store = new ConfigStore(dir);
        store.Update(c => c.MachineName = name, immediate: true);
        var host = new VarcoHost(store, new NoInput(), tcpPort: 0, discoveryPort: 0);
        _hosts.Add((host, dir));
        await host.StartAsync();
        return (host, store);
    }

    private static async Task<T> Eventually<T>(Func<T?> probe, int timeoutMs = 8000, string what = "condizione") where T : class
    {
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            if (probe() is { } v) return v;
            await Task.Delay(50);
        }
        throw new Xunit.Sdk.XunitException($"Timeout: {what}");
    }

    [Fact]
    public async Task Accepted_request_joins_groups_and_connects()
    {
        var (a, sa) = await NewHost("PC-A");
        var (b, sb) = await NewHost("PC-B");
        Assert.NotEqual(sa.Current.GroupKey, sb.Current.GroupKey); // ognuno nasce con la sua chiave

        var r = await a.HandleAsync(new ControlRequest { Cmd = "pair", Address = $"127.0.0.1:{b.Node!.ListenPort}" });
        Assert.True(r.Ok, r.Error);

        var req = await Eventually(() => b.GetStatus().IncomingPair, what: "richiesta su B");
        Assert.Equal("PC-A", req.PeerName);
        var outgoing = await Eventually(() => a.GetStatus().OutgoingPair is { State: "WaitingConfirm" } o ? o : null, what: "A in attesa");
        Assert.Equal(req.Code, outgoing.Code); // stesso codice sui due schermi

        Assert.True((await b.HandleAsync(new ControlRequest { Cmd = "pairAnswer", RequestId = req.RequestId, Accept = true })).Ok);

        await Eventually(() => a.GetStatus().OutgoingPair is { State: "Done" } o ? o : null, what: "abbinamento completato");
        await Eventually(() => sa.Current.GroupKey == sb.Current.GroupKey ? "" : null, what: "stessa chiave");
        await Eventually(() =>
            a.GetStatus().Peers.Any(p => p.Name == "PC-B" && p.State == "Connected") &&
            b.GetStatus().Peers.Any(p => p.Name == "PC-A" && p.State == "Connected") ? "" : null,
            timeoutMs: 10000, what: "connessione dopo l'abbinamento");

        // Entrambi nella stessa disposizione
        await Eventually(() => a.Layout.Current.Entries.Count == 2 && b.Layout.Current.Entries.Count == 2 ? "" : null, what: "layout condiviso");
    }

    [Fact]
    public async Task Rejected_request_changes_nothing()
    {
        var (a, sa) = await NewHost("PC-A");
        var (b, sb) = await NewHost("PC-B");
        var keyA = sa.Current.GroupKey;
        var keyB = sb.Current.GroupKey;

        await a.HandleAsync(new ControlRequest { Cmd = "pair", Address = $"127.0.0.1:{b.Node!.ListenPort}" });
        var req = await Eventually(() => b.GetStatus().IncomingPair, what: "richiesta su B");
        await b.HandleAsync(new ControlRequest { Cmd = "pairAnswer", RequestId = req.RequestId, Accept = false });

        var result = await Eventually(() => a.GetStatus().OutgoingPair is { State: "Rejected" } o ? o : null, what: "rifiuto");
        Assert.Contains("rifiutato", result.Message);
        Assert.Equal(keyA, sa.Current.GroupKey);
        Assert.Equal(keyB, sb.Current.GroupKey);
        Assert.Empty(sa.Current.KnownPeers);
    }

    [Fact]
    public async Task Unreachable_peer_is_asked_to_call_back()
    {
        // Simula il portatile col firewall che blocca le connessioni in ingresso:
        // A prova un indirizzo dove nessuno ascolta e la richiesta di richiamata arriva a B "via UDP".
        var (a, sa) = await NewHost("PC-A");
        var (b, sb) = await NewHost("PC-B");
        var closedPort = ClosedPort();
        a.CallbackSenderForTests = (_, _, token) =>
            b.OnCallbackRequested(sa.Current.MachineId, new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, a.Node!.ListenPort), token);

        var r = await a.HandleAsync(new ControlRequest { Cmd = "pair", Address = $"127.0.0.1:{closedPort}" });
        Assert.True(r.Ok, r.Error);

        var req = await Eventually(() => b.GetStatus().IncomingPair, what: "richiesta su B tramite richiamata");
        Assert.Equal("PC-A", req.PeerName);
        var outgoing = await Eventually(() => a.GetStatus().OutgoingPair is { State: "WaitingConfirm" } o ? o : null, what: "A in attesa");
        Assert.Equal(req.Code, outgoing.Code);

        Assert.True((await b.HandleAsync(new ControlRequest { Cmd = "pairAnswer", RequestId = req.RequestId, Accept = true })).Ok);
        await Eventually(() => a.GetStatus().OutgoingPair is { State: "Done" } o ? o : null, what: "abbinamento completato");
        await Eventually(() => sa.Current.GroupKey == sb.Current.GroupKey ? "" : null, what: "stessa chiave");
    }

    [Fact]
    public async Task Pairing_with_own_address_is_refused()
    {
        var (a, sa) = await NewHost("PC-A");
        var r = await a.HandleAsync(new ControlRequest { Cmd = "pair", Address = $"127.0.0.1:{a.Node!.ListenPort}" });
        Assert.False(r.Ok);
        Assert.Contains("questo PC", r.Error);
        Assert.Empty(sa.Current.ManualAddresses);
    }

    private static int ClosedPort()
    {
        using var s = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork, System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        s.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        return ((System.Net.IPEndPoint)s.LocalEndPoint!).Port;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var (h, dir) in _hosts)
        {
            await h.DisposeAsync();
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
