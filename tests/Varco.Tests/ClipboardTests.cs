using Varco.Core.Clipboard;
using Varco.Core.Config;
using Varco.Core.Net;
using Varco.Core.Protocol;

namespace Varco.Tests;

public class ClipboardTests
{
    /// <summary>Appunti finti: <see cref="Copy"/> simula l'utente che copia.</summary>
    private sealed class FakeClipboard : IClipboardPlatform
    {
        public event Action<ClipContent>? LocalChanged;
        public readonly List<ClipContent> Written = [];
        public void SetContent(ClipContent content) { lock (Written) Written.Add(content); }
        public void Copy(ClipContent c) => LocalChanged?.Invoke(c);
    }

    /// <summary>Rete in memoria: i messaggi passano davvero da codifica e decodifica.</summary>
    private sealed class Mesh
    {
        public readonly Dictionary<Guid, ClipboardSync> Nodes = [];
        public readonly List<(Guid From, Guid To, Message Msg)> Traffic = [];

        public sealed class Net(Mesh mesh, Guid self) : IPeerNetwork
        {
            public Guid SelfId => self;
            public bool IsConnected(Guid id) => id != self && mesh.Nodes.ContainsKey(id);
            public bool Send(Guid id, Message m)
            {
                if (!IsConnected(id)) return false;
                var copy = MessageCodec.Decode(MessageCodec.Encode(m))!;
                lock (mesh.Traffic) mesh.Traffic.Add((self, id, copy));
                mesh.Nodes[id].OnMessage(self, copy);
                return true;
            }
            public void Broadcast(Message m) { foreach (var id in mesh.Nodes.Keys.ToList()) if (id != self) Send(id, m); }
        }
    }

    private sealed class Peer
    {
        public Guid Id = Guid.NewGuid();
        public FakeClipboard Clip = new();
        public bool Enabled = true;
        public bool CanSend = true;
        public ClipboardSync Sync = null!;
        public readonly List<ClipContent> Applied = [];
        public TaskCompletionSource<ClipContent> Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static (Mesh mesh, Peer[] peers) Setup(int count = 2, Action<Peer>? configure = null)
    {
        var mesh = new Mesh();
        var peers = Enumerable.Range(0, count).Select(_ => new Peer()).ToArray();
        foreach (var p in peers)
        {
            configure?.Invoke(p);
            p.Sync = new ClipboardSync(new Mesh.Net(mesh, p.Id), p.Clip, () => p.Enabled, () => p.CanSend);
            p.Sync.Applied += (_, c) => { lock (p.Applied) p.Applied.Add(c); p.Next.TrySetResult(c); };
            mesh.Nodes[p.Id] = p.Sync;
        }
        // Ognuno annuncia le proprie capacità agli altri, come appena connessi.
        foreach (var p in peers)
            foreach (var q in peers)
                if (p != q) p.Sync.OnPeerConnected(q.Id);
        return (mesh, peers);
    }

    private static ClipContent Text(string s) =>
        new([new ClipFormat("CF_UNICODETEXT", [.. System.Text.Encoding.Unicode.GetBytes(s), 0, 0])]);

    private static string TextOf(ClipContent c) =>
        System.Text.Encoding.Unicode.GetString(c.Formats.Single(f => f.Name == "CF_UNICODETEXT").Data).TrimEnd('\0');

    [Fact]
    public void Codec_roundtrip_new_messages()
    {
        Assert.Equal(new Capabilities(PeerCaps.Clipboard), MessageCodec.Decode(MessageCodec.Encode(new Capabilities(PeerCaps.Clipboard))));
        var chunk = new ClipChunk(7, 100, 32, [1, 2, 3]);
        var back = Assert.IsType<ClipChunk>(MessageCodec.Decode(MessageCodec.Encode(chunk)));
        Assert.Equal((7u, 100, 32), (back.ClipId, back.Total, back.Offset));
        Assert.Equal(chunk.Data, back.Data);
        // Un tipo sconosciuto (come i nostri per la 0.4.0) va ignorato, non deve rompere la connessione.
        Assert.Null(MessageCodec.Decode([99, 1, 2, 3]));
    }

    [Fact]
    public async Task Text_copied_on_one_pc_arrives_on_the_others()
    {
        var (_, p) = Setup(3);
        p[0].Clip.Copy(Text("ciao Varco"));
        Assert.Equal("ciao Varco", TextOf(await p[1].Next.Task.WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.Equal("ciao Varco", TextOf(await p[2].Next.Task.WaitAsync(TimeSpan.FromSeconds(5))));
        Assert.Equal("ciao Varco", TextOf(p[1].Clip.Written.Single()));
    }

    [Fact]
    public async Task Large_image_is_chunked_and_arrives_intact()
    {
        var (mesh, p) = Setup();
        var pixels = new byte[3 * 1024 * 1024];
        new Random(42).NextBytes(pixels); // incomprimibile: tanti pezzi
        var content = new ClipContent([new ClipFormat("CF_DIB", pixels), new ClipFormat("PNG", [1, 2, 3])]);
        p[0].Clip.Copy(content);

        var got = await p[1].Next.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(pixels, got.Formats.Single(f => f.Name == "CF_DIB").Data);
        Assert.Equal(new byte[] { 1, 2, 3 }, got.Formats.Single(f => f.Name == "PNG").Data);
        lock (mesh.Traffic)
        {
            var chunks = mesh.Traffic.Select(t => t.Msg).OfType<ClipChunk>().ToList();
            Assert.True(chunks.Count > 50, $"pezzi: {chunks.Count}");
            Assert.All(chunks, c => Assert.True(c.Data.Length <= ClipboardSync.ChunkSize));
        }
    }

    [Fact]
    public async Task Received_content_is_not_sent_back()
    {
        var (mesh, p) = Setup();
        var c = Text("andata");
        p[0].Clip.Copy(c);
        await p[1].Next.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Su Windows la nostra scrittura viene già filtrata; se un programma la ricopiasse identica non deve tornare indietro.
        p[1].Clip.Copy(c);
        await Task.Delay(300);
        lock (mesh.Traffic) Assert.DoesNotContain(mesh.Traffic, t => t.From == p[1].Id && t.Msg is ClipChunk);
        Assert.Empty(p[0].Clip.Written);
    }

    [Fact]
    public async Task Same_text_copied_again_after_remote_change_is_sent()
    {
        var (_, p) = Setup();
        p[0].Clip.Copy(Text("X"));
        await p[1].Next.Task.WaitAsync(TimeSpan.FromSeconds(5));

        p[0].Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        p[1].Clip.Copy(Text("Y"));
        Assert.Equal("Y", TextOf(await p[0].Next.Task.WaitAsync(TimeSpan.FromSeconds(5))));

        p[1].Next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        p[0].Clip.Copy(Text("X"));
        Assert.Equal("X", TextOf(await p[1].Next.Task.WaitAsync(TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task Peer_without_capability_receives_nothing()
    {
        var (mesh, p) = Setup();
        var old = Guid.NewGuid(); // un PC con la 0.4.0: connesso ma senza Capabilities
        mesh.Nodes[old] = new ClipboardSync(new Mesh.Net(mesh, old), new FakeClipboard(), () => true, () => true);
        p[0].Clip.Copy(Text("solo per i nuovi"));
        await p[1].Next.Task.WaitAsync(TimeSpan.FromSeconds(5));
        lock (mesh.Traffic) Assert.DoesNotContain(mesh.Traffic, t => t.To == old && t.Msg is ClipChunk);
    }

    [Fact]
    public async Task Disabled_pc_neither_sends_nor_receives()
    {
        var (mesh, p) = Setup(configure: x => { });
        p[1].Enabled = false;
        p[1].Sync.AnnounceCaps();

        p[0].Clip.Copy(Text("a"));
        p[1].Clip.Copy(Text("b"));
        await Task.Delay(300);
        Assert.Empty(p[0].Clip.Written);
        Assert.Empty(p[1].Clip.Written);
        lock (mesh.Traffic) Assert.DoesNotContain(mesh.Traffic, t => t.Msg is ClipChunk);

        // Riattivato: riprende subito.
        p[1].Enabled = true;
        p[1].Sync.AnnounceCaps();
        p[0].Clip.Copy(Text("di nuovo"));
        Assert.Equal("di nuovo", TextOf(await p[1].Next.Task.WaitAsync(TimeSpan.FromSeconds(5))));
    }

    [Fact]
    public async Task Pc_controlling_another_does_not_send()
    {
        var (mesh, p) = Setup(configure: x => { });
        p[0].CanSend = false;
        p[0].Clip.Copy(Text("da un programma in background"));
        await Task.Delay(300);
        lock (mesh.Traffic) Assert.DoesNotContain(mesh.Traffic, t => t.Msg is ClipChunk);
    }

    [Fact]
    public void Corrupt_or_out_of_order_chunks_are_dropped()
    {
        var (_, p) = Setup();
        var wire = ClipboardSync.Compress(ClipboardSync.Serialize(Text(new string('x', 200_000))));
        var chunks = ClipboardSync.Chunks(1, wire).ToList();
        Assert.True(chunks.Count >= 1);
        // Manca il primo pezzo: niente viene applicato, nessuna eccezione.
        foreach (var c in chunks.Skip(1)) p[1].Sync.OnMessage(p[0].Id, c);
        // Dimensione assurda: rifiutata subito, senza allocare.
        p[1].Sync.OnMessage(p[0].Id, new ClipChunk(2, int.MaxValue, 0, [1]));
        Assert.Empty(p[1].Clip.Written);
    }

    [Fact]
    public void Decompression_is_capped()
    {
        var bomb = ClipboardSync.Compress(new byte[ClipboardSync.MaxRawBytes + 1024 * 1024]);
        Assert.True(bomb.Length < 1024 * 1024);
        Assert.Throws<InvalidDataException>(() => ClipboardSync.Decompress(bomb));
    }
}

/// <summary>Sulla rete vera: gli appunti voluminosi non devono far aspettare il mouse.</summary>
public class ClipboardPriorityTests : IAsyncLifetime
{
    private readonly List<NetworkNode> _nodes = [];

    [Fact]
    public async Task Mouse_overtakes_a_large_clipboard_transfer()
    {
        GroupKey.TryParse(GroupKey.Generate(), out var key);
        NetworkNode New(string name, params string[] manual)
        {
            var n = new NetworkNode(Guid.NewGuid(), name, key, new NodeOptions { TcpPort = 0, DiscoveryPort = 0, ManualAddresses = manual });
            _nodes.Add(n);
            return n;
        }
        var b = New("B");
        var a = New("A", $"127.0.0.1:{b.ListenPort}");
        var received = new List<Message>();
        var done = new TaskCompletionSource();
        var wire = new byte[32 * 1024 * 1024];
        new Random(1).NextBytes(wire);
        var chunks = ClipboardSync.Chunks(5, wire).ToList();
        // B smette di leggere al primo pezzo: i buffer TCP si riempiono e la coda di A si allunga davvero.
        var gate = new ManualResetEventSlim();
        b.MessageReceived += (_, m) =>
        {
            if (m is ClipChunk) gate.Wait(TimeSpan.FromSeconds(2));
            lock (received) received.Add(m);
            if (m is ClipChunk c && c.Offset + c.Data.Length == wire.Length) done.TrySetResult();
        };
        a.Start(); b.Start();
        var until = Environment.TickCount64 + 5000;
        while (!a.IsConnected(b.Me.Id) && Environment.TickCount64 < until) await Task.Delay(25);
        Assert.True(a.IsConnected(b.Me.Id));

        foreach (var c in chunks) a.Send(b.Me.Id, c);
        await Task.Delay(300); // il writer di A ora è fermo con centinaia di pezzi in coda
        a.Send(b.Me.Id, new MouseWheel(120, false));
        gate.Set();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(20));

        lock (received)
        {
            var wheelAt = received.FindIndex(m => m is MouseWheel);
            // Davanti alla rotella solo i pezzi già nei buffer TCP; dietro, il grosso della coda.
            Assert.True(wheelAt >= 0 && received.Count - wheelAt > chunks.Count / 2,
                $"rotella in posizione {wheelAt} su {received.Count}");
            var got = received.OfType<ClipChunk>().ToList();
            Assert.Equal(chunks.Count, got.Count);
            Assert.Equal(wire, got.SelectMany(c => c.Data).ToArray());
        }
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var n in _nodes) await n.DisposeAsync();
    }
}
