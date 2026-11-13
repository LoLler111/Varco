using System.Text;
using Varco.Core.Clipboard;
using Varco.Core.Config;
using Varco.Core.Files;
using Varco.Core.Net;
using Varco.Core.Protocol;

namespace Varco.Tests;

public sealed class FileTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "varco-test-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly List<NetworkNode> _nodes = [];

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var n in _nodes) await n.DisposeAsync();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string MakeFile(string rel, int size, int seed = 1)
    {
        var path = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var data = new byte[size];
        new Random(seed).NextBytes(data);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void Offer_lists_files_and_folders_with_relative_paths_and_roundtrips()
    {
        var video = MakeFile("video.mp4", 1000);
        MakeFile(@"Cartella\a.txt", 10);
        MakeFile(@"Cartella\Sotto\b.bin", 20);
        var offers = new FileOffers(DirectFileAccess.Instance);

        var offer = offers.Create([video, Path.Combine(_dir, "Cartella")])!;
        var paths = offer.Entries.Select(e => e.Path).ToList();
        Assert.Equal(["video.mp4", "Cartella", @"Cartella\a.txt", @"Cartella\Sotto", @"Cartella\Sotto\b.bin"],
            paths.Take(2).Concat(paths.Skip(2).Order()).ToList());
        Assert.Equal(1030, offer.TotalBytes);
        Assert.Equal(3, offer.FileCount);
        Assert.True(offer.Entries.Single(e => e.Path == "Cartella").IsDirectory);

        var back = FileOffer.Deserialize(offer.Serialize())!;
        Assert.Equal(offer.Id, back.Id);
        Assert.Equal(offer.Entries, back.Entries);

        // Stessa copia ripetuta → stessa offerta (gli appunti non la rispediscono).
        Assert.Equal(offer.Id, offers.Create([video, Path.Combine(_dir, "Cartella")])!.Id);
    }

    [Theory]
    [InlineData(@"..\fuori.txt")]
    [InlineData(@"C:\Windows\x.dll")]
    [InlineData(@"\\server\share\x")]
    [InlineData(@"a\..\..\x")]
    [InlineData(@"a\\b")]
    [InlineData("a/b")]
    public void Unsafe_paths_from_another_pc_are_rejected(string path)
    {
        Assert.False(FileOffer.IsSafeRelativePath(path));
        var evil = new FileOffer(Guid.NewGuid(), [new FileEntry(path, 1, FileAttributes.Normal, DateTime.UtcNow, false)]);
        Assert.Null(FileOffer.Deserialize(evil.Serialize()));
    }

    [Fact]
    public void Only_entries_of_a_known_offer_can_be_opened()
    {
        var offers = new FileOffers(DirectFileAccess.Instance);
        var offer = offers.Create([MakeFile("x.bin", 100)])!;
        var (data, length, error) = offers.Open(offer.Id, 0, 40);
        using (data) Assert.Equal((60L, (string?)null), (length, error));
        Assert.NotNull(offers.Open(Guid.NewGuid(), 0, 0).Error);
        Assert.NotNull(offers.Open(offer.Id, 1, 0).Error);
        Assert.NotNull(offers.Open(offer.Id, -1, 0).Error);
    }

    private (NetworkNode A, NetworkNode B) Pair()
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
        a.Start(); b.Start();
        return (a, b);
    }

    private static async Task Connected(NetworkNode a, NetworkNode b)
    {
        var until = Environment.TickCount64 + 5000;
        while (!(a.IsConnected(b.Me.Id) && b.IsConnected(a.Me.Id)) && Environment.TickCount64 < until) await Task.Delay(25);
        Assert.True(a.IsConnected(b.Me.Id));
    }

    [Fact]
    public async Task File_is_transferred_on_a_separate_connection_with_offset()
    {
        var (a, b) = Pair();
        var offers = new FileOffers(DirectFileAccess.Instance);
        var path = MakeFile("grande.bin", 3 * 1024 * 1024 + 123, seed: 7);
        var offer = offers.Create([path])!;
        a.FileProvider = (peer, id, index, offset) => offers.Open(id, index, offset);
        await Connected(a, b);

        await using (var remote = await b.OpenFileAsync(a.Me.Id, offer.Id, 0, 0, CancellationToken.None))
        {
            Assert.Equal(new FileInfo(path).Length, remote.Length);
            var ms = new MemoryStream();
            await remote.CopyToAsync(ms);
            Assert.Equal(File.ReadAllBytes(path), ms.ToArray());
        }

        // Ripresa da metà file.
        await using (var remote = await b.OpenFileAsync(a.Me.Id, offer.Id, 0, 1_000_000, CancellationToken.None))
        {
            var ms = new MemoryStream();
            await remote.CopyToAsync(ms);
            Assert.Equal(File.ReadAllBytes(path)[1_000_000..], ms.ToArray());
        }

        // La connessione di mouse e tastiera non è stata toccata.
        Assert.True(a.IsConnected(b.Me.Id) && b.IsConnected(a.Me.Id));
    }

    [Fact]
    public async Task Unknown_offer_or_disabled_sharing_gives_a_clear_error()
    {
        var (a, b) = Pair();
        await Connected(a, b);
        var ex = await Assert.ThrowsAsync<IOException>(() => b.OpenFileAsync(a.Me.Id, Guid.NewGuid(), 0, 0, CancellationToken.None));
        Assert.Contains("non attiva", ex.Message);

        var offers = new FileOffers(DirectFileAccess.Instance);
        a.FileProvider = (peer, id, index, offset) => offers.Open(id, index, offset);
        ex = await Assert.ThrowsAsync<IOException>(() => b.OpenFileAsync(a.Me.Id, Guid.NewGuid(), 0, 0, CancellationToken.None));
        Assert.Contains("non sono più disponibili", ex.Message);
    }

    [Fact]
    public async Task File_changed_during_copy_is_reported()
    {
        var (a, b) = Pair();
        var path = MakeFile("cambia.bin", 1024 * 1024);
        var offers = new FileOffers(DirectFileAccess.Instance);
        var offer = offers.Create([path])!;
        // Dichiara più byte di quelli che manda: chi riceve deve accorgersene invece di salvare un file troncato.
        a.FileProvider = (peer, id, index, offset) =>
        {
            var (data, length, error) = offers.Open(id, index, offset);
            return (data, length + 10, error);
        };
        await Connected(a, b);
        await using var remote = await b.OpenFileAsync(a.Me.Id, offer.Id, 0, 0, CancellationToken.None);
        await Assert.ThrowsAsync<IOException>(() => remote.CopyToAsync(Stream.Null));
    }

    // ───────────── offerte negli appunti ─────────────

    private sealed class FakeClipboard : IClipboardPlatform
    {
        public event Action<ClipContent>? LocalChanged;
        public readonly List<ClipContent> Written = [];
        public void SetContent(ClipContent content) { lock (Written) Written.Add(content); }
        public void Copy(ClipContent c) => LocalChanged?.Invoke(c);
    }

    private sealed class Net(Dictionary<Guid, ClipboardSync> nodes, Guid self) : IPeerNetwork
    {
        public Guid SelfId => self;
        public bool IsConnected(Guid id) => id != self && nodes.ContainsKey(id);
        public bool Send(Guid id, Message m)
        {
            if (!IsConnected(id)) return false;
            nodes[id].OnMessage(self, MessageCodec.Decode(MessageCodec.Encode(m))!);
            return true;
        }
        public void Broadcast(Message m) { foreach (var id in nodes.Keys.ToList()) if (id != self) Send(id, m); }
    }

    [Fact]
    public async Task Copied_files_become_an_offer_only_for_pcs_that_support_files()
    {
        var nodes = new Dictionary<Guid, ClipboardSync>();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), old = Guid.NewGuid();
        var clipA = new FakeClipboard();
        var clipB = new FakeClipboard();
        var clipOld = new FakeClipboard();
        var syncA = new ClipboardSync(new Net(nodes, a), clipA, () => true, () => true, new FileOffers(DirectFileAccess.Instance));
        var syncB = new ClipboardSync(new Net(nodes, b), clipB, () => true, () => true, new FileOffers(DirectFileAccess.Instance));
        var syncOld = new ClipboardSync(new Net(nodes, old), clipOld, () => true, () => true); // come la 0.5: appunti sì, file no
        nodes[a] = syncA; nodes[b] = syncB; nodes[old] = syncOld;
        foreach (var (x, s) in nodes)
            foreach (var y in nodes.Keys)
                if (x != y) s.OnPeerConnected(y);

        var got = new TaskCompletionSource<(Guid, FileOffer)>();
        syncB.FilesOffered += (from, o) => got.TrySetResult((from, o));
        var video = MakeFile("cattura.mp4", 5000);
        clipA.Copy(new ClipContent([new ClipFormat(ClipboardSync.LocalFilesFormat, Encoding.UTF8.GetBytes(video))]));

        var (sender, offer) = await got.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(a, sender);
        Assert.Equal("cattura.mp4", offer.Entries.Single().Path);
        Assert.Equal(5000, offer.TotalBytes);
        await Task.Delay(200);
        Assert.Empty(clipB.Written);   // gli appunti di B li riempie l'icona, non l'agent
        Assert.Empty(clipOld.Written); // la 0.5 non riceve nulla (avrebbe svuotato i suoi appunti)
    }
}
