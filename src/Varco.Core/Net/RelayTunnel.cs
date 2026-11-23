using System.Threading.Channels;
using Varco.Core.Protocol;

namespace Varco.Core.Net;

/// <summary>
/// Inoltro tramite un altro PC: quando A e C non si vedono (router che non passa fra cavo e Wi-Fi, firewall) ma tutti e
/// due sono collegati a B, A e C si collegano attraverso B. Il tunnel è uno stream sopra la connessione con B: i byte
/// scritti partono come <see cref="RelayData"/>, B li gira all'altro capo, e sopra il tunnel corrono lo stesso
/// handshake e la stessa cifratura di una connessione diretta. B vede solo byte cifrati.
///
/// Si chiude quando lo chiude chi lo usa, quando l'altro capo o B mandano <see cref="RelayClose"/>, o quando cade la
/// connessione con B. Chi legge vede la fine dello stream e la connessione sopra il tunnel si chiude come se fosse
/// caduto il cavo.
/// </summary>
internal sealed class RelayTunnel : Stream
{
    /// <summary>Byte massimi in un solo <see cref="RelayData"/>: il frame che li porta a B deve stare sotto il limite dei frame.</summary>
    private const int MaxChunk = 256 * 1024;

    private readonly Channel<byte[]> _in = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Action<RelayTunnel>? _onClosed;
    private byte[]? _current;
    private int _pos;
    private int _closed;
    private int _remoteClosed;

    public Guid Id { get; }
    /// <summary>La connessione con il PC che fa da tramite.</summary>
    public PeerConnection Carrier { get; }

    public RelayTunnel(Guid id, PeerConnection carrier, Action<RelayTunnel>? onClosed = null)
    {
        Id = id;
        Carrier = carrier;
        _onClosed = onClosed;
    }

    /// <summary>Byte arrivati dall'altro capo (dal thread di lettura della connessione con il tramite).</summary>
    public void Deliver(byte[] data)
    {
        if (data.Length > 0) _in.Writer.TryWrite(data);
    }

    /// <summary>L'altro capo o il tramite hanno chiuso il tunnel: niente più dati, e non serve avvisarli.</summary>
    public void RemoteClosed()
    {
        Interlocked.Exchange(ref _remoteClosed, 1);
        Dispose();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        if (buffer.Length == 0) return 0;
        while (_current is null || _pos >= _current.Length)
        {
            try
            {
                if (!await _in.Reader.WaitToReadAsync(ct).ConfigureAwait(false)) return 0; // tunnel chiuso: fine dello stream
            }
            catch (ChannelClosedException) { return 0; }
            if (_in.Reader.TryRead(out var next)) { _current = next; _pos = 0; }
        }
        var n = Math.Min(buffer.Length, _current.Length - _pos);
        _current.AsMemory(_pos, n).CopyTo(buffer);
        _pos += n;
        return n;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
    {
        Write(buffer.AsSpan(offset, count));
        return Task.CompletedTask;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (Volatile.Read(ref _closed) != 0 || Carrier.Closed.IsCompleted) throw new IOException("tunnel di inoltro chiuso");
        for (var off = 0; off < buffer.Length; off += MaxChunk)
            Carrier.Send(new RelayData(Id, buffer.Slice(off, Math.Min(MaxChunk, buffer.Length - off)).ToArray()));
    }

    protected override void Dispose(bool disposing)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _in.Writer.TryComplete();
        if (Volatile.Read(ref _remoteClosed) == 0) Carrier.Send(new RelayClose(Id));
        try { _onClosed?.Invoke(this); } catch (Exception ex) { Log.Error("Chiusura del tunnel di inoltro", ex); }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
