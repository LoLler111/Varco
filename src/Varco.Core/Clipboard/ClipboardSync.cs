using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Varco.Core.Net;
using Varco.Core.Protocol;

namespace Varco.Core.Clipboard;

/// <summary>Un formato degli appunti: nome ("CF_UNICODETEXT", "HTML Format", "PNG"…) e byte grezzi.</summary>
public sealed record ClipFormat(string Name, byte[] Data);

/// <summary>Contenuto degli appunti da condividere (solo i formati che hanno senso fra PC diversi).</summary>
public sealed record ClipContent(IReadOnlyList<ClipFormat> Formats)
{
    public long TotalBytes => Formats.Sum(f => (long)f.Data.Length);
}

/// <summary>Accesso agli appunti del sistema. L'implementazione Windows sta nell'agent.</summary>
public interface IClipboardPlatform
{
    /// <summary>L'utente (o un programma) ha copiato qualcosa. Non scatta per i contenuti scritti con <see cref="SetContent"/>.</summary>
    event Action<ClipContent>? LocalChanged;
    void SetContent(ClipContent content);
}

/// <summary>
/// Appunti condivisi: quando su un PC si copia qualcosa, il contenuto viene compresso e mandato a pezzi
/// a tutti i PC connessi che hanno annunciato di saperlo gestire (<see cref="PeerCaps.Clipboard"/>).
/// I PC con la versione 0.4.0 o precedenti non annunciano nulla e quindi non ricevono niente.
/// </summary>
public sealed class ClipboardSync
{
    /// <summary>Pezzi piccoli: in coda a bassa priorità, fra un pezzo e l'altro passano mouse e tastiera.</summary>
    public const int ChunkSize = 32 * 1024;
    /// <summary>Oltre questa dimensione (non compressa) gli appunti restano solo sul PC dove si è copiato.</summary>
    public const int MaxRawBytes = 96 * 1024 * 1024;

    private const byte PayloadVersion = 1;

    private readonly IPeerNetwork _net;
    private readonly IClipboardPlatform _platform;
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _canSend;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, PeerCaps> _caps = [];
    private readonly Dictionary<Guid, Incoming> _incoming = [];
    private uint _nextId = (uint)Random.Shared.Next();
    /// <summary>Impronta degli ultimi appunti noti (copiati qui o arrivati da un altro PC): evita di rimandarli indietro.</summary>
    private byte[]? _lastHash;

    /// <summary>Per i test: scatta dopo aver applicato appunti arrivati da un altro PC.</summary>
    internal event Action<Guid, ClipContent>? Applied;

    /// <param name="enabled">Impostazione "condividi gli appunti" di questo PC.</param>
    /// <param name="canSend">Falso mentre questo PC comanda un altro: le copie locali arrivano da programmi, non dall'utente.</param>
    public ClipboardSync(IPeerNetwork net, IClipboardPlatform platform, Func<bool> enabled, Func<bool> canSend)
    {
        _net = net;
        _platform = platform;
        _enabled = enabled;
        _canSend = canSend;
        _platform.LocalChanged += c => _ = Task.Run(() => OnLocalChanged(c));
    }

    private PeerCaps MyCaps => _enabled() ? PeerCaps.Clipboard : PeerCaps.None;

    public void OnPeerConnected(Guid id) => _net.Send(id, new Capabilities(MyCaps));

    public void OnPeerDisconnected(Guid id)
    {
        if (_net.IsConnected(id)) return;
        lock (_gate)
        {
            _caps.Remove(id);
            _incoming.Remove(id);
        }
    }

    /// <summary>Da chiamare dopo aver cambiato l'impostazione: gli altri PC smettono (o riprendono) a mandarci gli appunti.</summary>
    public void AnnounceCaps()
    {
        if (!_enabled()) lock (_gate) _incoming.Clear();
        _net.Broadcast(new Capabilities(MyCaps));
    }

    /// <summary>Gestisce i messaggi degli appunti; restituisce false per gli altri.</summary>
    public bool OnMessage(Guid from, Message msg)
    {
        switch (msg)
        {
            case Capabilities c:
                lock (_gate) _caps[from] = c.Caps;
                return true;
            case ClipChunk chunk:
                OnChunk(from, chunk);
                return true;
            default:
                return false;
        }
    }

    // ───────────────────────────── invio ─────────────────────────────

    private void OnLocalChanged(ClipContent content)
    {
        try
        {
            if (!_enabled() || content.Formats.Count == 0) return;
            if (!_canSend())
            {
                Log.Debug("Appunti cambiati mentre questo PC ne comanda un altro: non li condivido");
                return;
            }
            if (content.TotalBytes > MaxRawBytes)
            {
                Log.Info($"Appunti troppo grandi da condividere ({content.TotalBytes / (1024 * 1024)} MB)");
                return;
            }

            var raw = Serialize(content);
            var hash = SHA256.HashData(raw);
            List<Guid> targets;
            uint id;
            lock (_gate)
            {
                if (_lastHash is not null && hash.AsSpan().SequenceEqual(_lastHash)) return;
                _lastHash = hash;
                targets = _caps.Where(kv => kv.Value.HasFlag(PeerCaps.Clipboard) && _net.IsConnected(kv.Key)).Select(kv => kv.Key).ToList();
                id = ++_nextId;
            }
            if (targets.Count == 0) return;

            var wire = Compress(raw);
            Log.Info($"Appunti condivisi ({Describe(content)}, {Kb(wire.Length)} sulla rete) con {targets.Count} PC");
            foreach (var chunk in Chunks(id, wire))
                foreach (var t in targets)
                    _net.Send(t, chunk);
        }
        catch (Exception ex)
        {
            Log.Error("Invio degli appunti", ex);
        }
    }

    internal static IEnumerable<ClipChunk> Chunks(uint id, byte[] wire)
    {
        if (wire.Length == 0) yield break;
        for (var off = 0; off < wire.Length; off += ChunkSize)
            yield return new ClipChunk(id, wire.Length, off, wire.AsSpan(off, Math.Min(ChunkSize, wire.Length - off)).ToArray());
    }

    // ───────────────────────────── ricezione ─────────────────────────────

    private sealed class Incoming(uint id, int total)
    {
        public uint Id { get; } = id;
        public byte[] Buffer { get; } = new byte[total];
        public int Received { get; set; }
    }

    private void OnChunk(Guid from, ClipChunk c)
    {
        byte[]? complete = null;
        lock (_gate)
        {
            if (!_enabled()) return;
            _incoming.TryGetValue(from, out var inc);
            if (c.Offset == 0)
            {
                // Appunti nuovi: quelli eventualmente a metà sono superati.
                if (c.Total <= 0 || c.Total > MaxRawBytes) { _incoming.Remove(from); return; }
                _incoming[from] = inc = new Incoming(c.ClipId, c.Total);
            }
            if (inc is null || inc.Id != c.ClipId || c.Total != inc.Buffer.Length || c.Offset != inc.Received
                || c.Data.Length == 0 || c.Data.Length > inc.Buffer.Length - inc.Received)
            {
                // Pezzo fuori sequenza (es. connessione caduta a metà): si aspetta il prossimo inizio.
                _incoming.Remove(from);
                return;
            }
            c.Data.CopyTo(inc.Buffer, inc.Received);
            inc.Received += c.Data.Length;
            if (inc.Received == inc.Buffer.Length)
            {
                _incoming.Remove(from);
                complete = inc.Buffer;
            }
        }
        // Decompressione e scrittura fuori dal thread di lettura della connessione: il mouse non deve aspettare.
        if (complete is not null) _ = Task.Run(() => Apply(from, complete));
    }

    private void Apply(Guid from, byte[] wire)
    {
        try
        {
            var raw = Decompress(wire);
            var content = Deserialize(raw);
            if (content.Formats.Count == 0) return;
            var hash = SHA256.HashData(raw);
            lock (_gate)
            {
                if (!_enabled()) return;
                if (_lastHash is not null && hash.AsSpan().SequenceEqual(_lastHash)) return;
                _lastHash = hash;
            }
            _platform.SetContent(content);
            Log.Info($"Appunti ricevuti ({Describe(content)})");
            Applied?.Invoke(from, content);
        }
        catch (Exception ex)
        {
            Log.Error("Appunti ricevuti non validi", ex);
        }
    }

    // ───────────────────────────── formato ─────────────────────────────

    internal static byte[] Serialize(ClipContent content)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(PayloadVersion);
        w.Write(content.Formats.Count);
        foreach (var f in content.Formats)
        {
            w.Write(f.Name);
            w.Write(f.Data.Length);
            w.Write(f.Data);
        }
        w.Flush();
        return ms.ToArray();
    }

    internal static ClipContent Deserialize(byte[] raw)
    {
        using var r = new BinaryReader(new MemoryStream(raw), Encoding.UTF8);
        if (r.ReadByte() != PayloadVersion) return new ClipContent([]); // formato futuro: meglio niente che spazzatura
        var count = r.ReadInt32();
        if (count is < 0 or > 64) throw new InvalidDataException($"Troppi formati ({count})");
        var list = new List<ClipFormat>(count);
        for (var i = 0; i < count; i++)
        {
            var name = r.ReadString();
            var len = r.ReadInt32();
            if (len < 0 || len > raw.Length - r.BaseStream.Position) throw new InvalidDataException("Formato troncato");
            list.Add(new ClipFormat(name, r.ReadBytes(len)));
        }
        return new ClipContent(list);
    }

    internal static byte[] Compress(byte[] raw)
    {
        using var ms = new MemoryStream(raw.Length / 2 + 64);
        using (var b = new BrotliStream(ms, CompressionLevel.Fastest, leaveOpen: true)) b.Write(raw);
        return ms.ToArray();
    }

    /// <summary>Decompressione con tetto: un PC del gruppo non può far esplodere la memoria con una "bomba" compressa.</summary>
    internal static byte[] Decompress(byte[] wire)
    {
        using var b = new BrotliStream(new MemoryStream(wire), CompressionMode.Decompress);
        using var ms = new MemoryStream(wire.Length * 2);
        var buf = new byte[81920];
        int n;
        while ((n = b.Read(buf)) > 0)
        {
            if (ms.Length + n > MaxRawBytes + 64 * 1024) throw new InvalidDataException("Appunti decompressi troppo grandi");
            ms.Write(buf, 0, n);
        }
        return ms.ToArray();
    }

    private static string Describe(ClipContent c)
    {
        var kinds = new List<string>();
        if (c.Formats.Any(f => f.Name == "CF_UNICODETEXT")) kinds.Add("testo");
        if (c.Formats.Any(f => f.Name is "CF_DIB" or "CF_DIBV5" or "PNG")) kinds.Add("immagine");
        if (kinds.Count == 0) kinds.Add(string.Join("+", c.Formats.Select(f => f.Name)));
        return $"{string.Join(" e ", kinds)}, {Kb(c.TotalBytes)}";
    }

    private static string Kb(long bytes) => bytes < 1024 * 1024 ? $"{Math.Max(1, bytes / 1024)} KB" : $"{bytes / (1024.0 * 1024):0.0} MB";
}
