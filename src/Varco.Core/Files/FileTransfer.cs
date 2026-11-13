using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Varco.Core.Crypto;

namespace Varco.Core.Files;

/// <summary>Apre un elemento di un'offerta locale: (dati dalla posizione richiesta, byte rimanenti, errore).</summary>
public delegate (Stream? Data, long Length, string? Error) FileOpener(Guid peer, Guid offerId, int index, long offset);

/// <summary>
/// Trasferimento di un file su una connessione TCP dedicata, separata da quella di mouse e tastiera:
/// così un video da qualche GB non si mette in fila davanti ai movimenti del cursore.
///
///   client → server : "VRCF" 1                       (in chiaro: distingue dalle connessioni normali e dall'abbinamento)
///   handshake normale (chiave di gruppo, AES-GCM)
///   client → server : offerta(16) indice(int) posizione(long)
///   server → client : esito(byte: 0 ok, 1 errore) lunghezza(long) messaggio(string)
///   server → client : 0 + dati … poi 1 (fine) oppure 2 + messaggio (errore a metà)
///
/// Le versioni senza file (fino alla 0.5) non ricevono offerte, quindi non vengono mai contattate così.
/// </summary>
public static class FileTransfer
{
    private static readonly byte[] Magic = [.. "VRCF"u8, 1];
    private const int ChunkSize = 64 * 1024;
    private const byte Data = 0, End = 1, Failed = 2;

    /// <summary>Senza traffico per così tanto il trasferimento si considera morto.</summary>
    public static int IdleTimeoutMs { get; set; } = 30_000;

    public static bool IsFileFrame(byte[] frame) => frame.AsSpan().SequenceEqual(Magic);

    // ───────────────────────────── lato di chi offre ─────────────────────────────

    /// <param name="throttleMs">Pausa dopo ogni pezzo, in base a quanto il mouse verso quel PC ne risente.</param>
    internal static async Task ServeAsync(Stream s, LocalIdentity me, FileOpener? open, Func<Guid, int> throttleMs,
        int handshakeTimeoutMs, CancellationToken ct)
    {
        HandshakeResult hs;
        using (var h = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            h.CancelAfter(handshakeTimeoutMs);
            hs = await Handshake.RunServerAsync(s, me, h.Token).ConfigureAwait(false);
        }
        using var cipher = hs.Cipher;

        var req = cipher.Open(await ReadFrame(s, ct).ConfigureAwait(false));
        if (req.Length != 16 + 4 + 8) throw new InvalidDataException("Richiesta file non valida");
        var offerId = new Guid(req.AsSpan(0, 16));
        var index = BinaryPrimitives.ReadInt32LittleEndian(req.AsSpan(16));
        var offset = BinaryPrimitives.ReadInt64LittleEndian(req.AsSpan(20));

        var (data, length, error) = open is null ? (null, 0, "condivisione file non attiva") : open(hs.PeerId, offerId, index, offset);
        await using (data)
        {
            await WriteFrame(s, cipher, Header(data is null ? (byte)1 : (byte)0, length, error ?? ""), ct).ConfigureAwait(false);
            if (data is null)
            {
                Log.Info($"File richiesto da {hs.PeerName} non disponibile: {error}");
                return;
            }

            Log.Info($"Invio di un file a {hs.PeerName} ({length / 1024} KB{(offset > 0 ? $", da {offset / 1024} KB" : "")})");
            var buf = new byte[1 + ChunkSize];
            long sent = 0;
            while (true)
            {
                int n;
                try { n = await data.ReadAsync(buf.AsMemory(1, ChunkSize), ct).ConfigureAwait(false); }
                catch (IOException ex)
                {
                    await WriteFrame(s, cipher, [Failed, .. Encoding.UTF8.GetBytes(ex.Message)], ct).ConfigureAwait(false);
                    return;
                }
                if (n == 0) break;
                buf[0] = Data;
                await WriteFrame(s, cipher, buf.AsSpan(0, 1 + n).ToArray(), ct).ConfigureAwait(false);
                sent += n;
                var pause = throttleMs(hs.PeerId);
                if (pause > 0) await Task.Delay(pause, ct).ConfigureAwait(false);
            }
            await WriteFrame(s, cipher, [End], ct).ConfigureAwait(false);
            Log.Info($"File inviato a {hs.PeerName} ({sent / 1024} KB)");
        }
    }

    private static byte[] Header(byte status, long length, string message)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(status);
        w.Write(length);
        w.Write(message);
        w.Flush();
        return ms.ToArray();
    }

    // ───────────────────────────── lato di chi incolla ─────────────────────────────

    /// <summary>Si collega a <paramref name="ep"/> e chiede l'elemento <paramref name="index"/> dell'offerta. Lancia <see cref="IOException"/> se non disponibile.</summary>
    internal static async Task<RemoteFile> OpenAsync(IPEndPoint ep, LocalIdentity me, Guid expectedPeer, Guid offerId, int index, long offset,
        int connectTimeoutMs, int handshakeTimeoutMs, CancellationToken ct)
    {
        var socket = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
            t.CancelAfter(connectTimeoutMs);
            await socket.ConnectAsync(ep, t.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            socket.Dispose();
            throw new IOException($"impossibile raggiungere l'altro PC ({(ex is SocketException se ? se.SocketErrorCode.ToString() : "timeout")})", ex);
        }
        var stream = new NetworkStream(socket, ownsSocket: true);
        try
        {
            HandshakeResult hs;
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                t.CancelAfter(handshakeTimeoutMs);
                await Framing.WriteFrameAsync(stream, Magic, t.Token).ConfigureAwait(false);
                hs = await Handshake.RunClientAsync(stream, me, t.Token).ConfigureAwait(false);
            }
            if (hs.PeerId != expectedPeer)
            {
                hs.Cipher.Dispose();
                throw new IOException("all'indirizzo risponde un altro PC");
            }

            var req = new byte[16 + 4 + 8];
            offerId.TryWriteBytes(req);
            BinaryPrimitives.WriteInt32LittleEndian(req.AsSpan(16), index);
            BinaryPrimitives.WriteInt64LittleEndian(req.AsSpan(20), offset);
            await WriteFrame(stream, hs.Cipher, req, ct).ConfigureAwait(false);

            var header = hs.Cipher.Open(await ReadFrame(stream, ct).ConfigureAwait(false));
            using var r = new BinaryReader(new MemoryStream(header), Encoding.UTF8);
            var status = r.ReadByte();
            var length = r.ReadInt64();
            var message = r.ReadString();
            if (status != 0)
            {
                hs.Cipher.Dispose();
                throw new IOException(message.Length > 0 ? message : "file non disponibile");
            }
            return new RemoteFile(stream, hs.Cipher, length);
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Il file in arrivo, da leggere in sequenza. <see cref="Length"/> = byte attesi dalla posizione richiesta.</summary>
    public sealed class RemoteFile : Stream
    {
        private readonly NetworkStream _net;
        private readonly BufferedStream _in;
        private readonly FrameCipher _cipher;
        private byte[] _chunk = [];
        private int _pos = 1;
        private long _received;
        private bool _ended;

        internal RemoteFile(NetworkStream net, FrameCipher cipher, long length)
        {
            _net = net;
            _in = new BufferedStream(net, 128 * 1024);
            _cipher = cipher;
            Length = length;
        }

        public override long Length { get; }
        public override long Position { get => _received; set => throw new NotSupportedException(); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (_pos >= _chunk.Length)
            {
                if (_ended || buffer.Length == 0) return 0;
                var frame = _cipher.Open(await ReadFrame(_in, ct).ConfigureAwait(false));
                if (frame.Length == 0) throw new InvalidDataException("Pezzo vuoto");
                switch (frame[0])
                {
                    case Data:
                        _chunk = frame;
                        _pos = 1;
                        break;
                    case End:
                        _ended = true;
                        if (_received != Length) throw new IOException($"il file è cambiato durante la copia ({_received} byte invece di {Length})");
                        return 0;
                    default:
                        _ended = true;
                        throw new IOException("errore leggendo il file sull'altro PC: " + Encoding.UTF8.GetString(frame, 1, frame.Length - 1));
                }
            }
            var n = Math.Min(buffer.Length, _chunk.Length - _pos);
            _chunk.AsMemory(_pos, n).CopyTo(buffer);
            _pos += n;
            _received += n;
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _net.Dispose();
                _cipher.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    // ───────────────────────────── frame con timeout ─────────────────────────────

    private static async Task<byte[]> ReadFrame(Stream s, CancellationToken ct)
    {
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(IdleTimeoutMs);
        try { return await Framing.ReadFrameAsync(s, t.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("l'altro PC non risponde"); }
    }

    private static async Task WriteFrame(Stream s, FrameCipher cipher, byte[] plain, CancellationToken ct)
    {
        using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
        t.CancelAfter(IdleTimeoutMs);
        try { await Framing.WriteFrameAsync(s, cipher.Seal(plain), t.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("l'altro PC non riceve"); }
    }
}
