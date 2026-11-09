using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Varco.Core.Crypto;
using Varco.Core.Protocol;

namespace Varco.Core.Net;

/// <summary>
/// Una connessione cifrata e autenticata verso un altro PC.
/// Ha un heartbeat applicativo: se per <c>timeoutMs</c> non arriva nulla la connessione viene chiusa
/// (non ci si fida del keepalive TCP, che su una connessione "mezza aperta" impiega minuti).
/// </summary>
public sealed class PeerConnection : IAsyncDisposable
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;
    private readonly FrameCipher _cipher;
    private readonly Channel<byte[]> _outbox = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<string> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _intervalMs;
    private readonly int _timeoutMs;
    private long _lastReceived;
    private long _lastSent;
    private int _closing;

    public Guid PeerId { get; }
    public string PeerName { get; }
    public int PeerListenPort { get; }
    public IPEndPoint RemoteEndPoint { get; }
    /// <summary>Id del PC che ha aperto la connessione (serve a risolvere connessioni doppie in modo deterministico).</summary>
    public Guid InitiatorId { get; }
    public long EstablishedAt { get; } = Environment.TickCount64;
    public byte[] SessionId { get; }
    public double RttMs { get; private set; } = -1;

    /// <summary>Messaggi applicativi ricevuti (Ping/Pong sono gestiti internamente).</summary>
    public event Action<PeerConnection, Message>? MessageReceived;

    /// <summary>Completata con il motivo della chiusura.</summary>
    public Task<string> Closed => _closed.Task;

    internal PeerConnection(Socket socket, NetworkStream stream, HandshakeResult hs, Guid initiatorId, int intervalMs, int timeoutMs)
    {
        _socket = socket;
        _stream = stream;
        _cipher = hs.Cipher;
        PeerId = hs.PeerId;
        PeerName = hs.PeerName;
        PeerListenPort = hs.PeerListenPort;
        InitiatorId = initiatorId;
        SessionId = hs.SessionId;
        RemoteEndPoint = (IPEndPoint)socket.RemoteEndPoint!;
        _intervalMs = intervalMs;
        _timeoutMs = timeoutMs;
        _lastReceived = _lastSent = Environment.TickCount64;
    }

    /// <summary>Avvia lettura, scrittura e heartbeat. Va chiamato dopo aver agganciato gli eventi.</summary>
    internal void Start()
    {
        _ = Task.Run(ReadLoop);
        _ = Task.Run(WriteLoop);
        _ = Task.Run(HeartbeatLoop);
    }

    public void Send(Message m)
    {
        if (_closing != 0) return;
        _outbox.Writer.TryWrite(MessageCodec.Encode(m));
    }

    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0) return;
        _outbox.Writer.TryComplete();
        _cts.Cancel();
        try { _socket.Shutdown(SocketShutdown.Both); } catch { }
        try { _socket.Close(0); } catch { }
        _cipher.Dispose();
        _closed.TrySetResult(reason);
    }

    private async Task ReadLoop()
    {
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var frame = await Framing.ReadFrameAsync(_stream, _cts.Token).ConfigureAwait(false);
                Volatile.Write(ref _lastReceived, Environment.TickCount64);
                var msg = MessageCodec.Decode(_cipher.Open(frame));
                switch (msg)
                {
                    case null: break;
                    case Ping p: Send(new Pong(p.Timestamp)); break;
                    case Pong p: RttMs = (Stopwatch.GetTimestamp() - p.Timestamp) * 1000.0 / Stopwatch.Frequency; break;
                    default:
                        try { MessageReceived?.Invoke(this, msg); }
                        catch (Exception ex) { Log.Error($"Errore gestendo {msg.GetType().Name} da {PeerName}", ex); }
                        break;
                }
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Close(_closing != 0 ? "chiusa" : $"lettura: {ex.GetType().Name}");
        }
    }

    private async Task WriteLoop()
    {
        try
        {
            await foreach (var plain in _outbox.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await Framing.WriteFrameAsync(_stream, _cipher.Seal(plain), _cts.Token).ConfigureAwait(false);
                Volatile.Write(ref _lastSent, Environment.TickCount64);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Close($"scrittura: {ex.GetType().Name}");
        }
    }

    private async Task HeartbeatLoop()
    {
        var tick = Math.Max(50, _intervalMs / 2);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await Task.Delay(tick, _cts.Token).ConfigureAwait(false);
                var now = Environment.TickCount64;
                if (now - Volatile.Read(ref _lastReceived) > _timeoutMs)
                {
                    Close($"nessuna risposta da {_timeoutMs} ms");
                    return;
                }
                if (now - Volatile.Read(ref _lastSent) >= _intervalMs)
                    Send(new Ping(Stopwatch.GetTimestamp()));
            }
        }
        catch (OperationCanceledException) { }
    }

    public ValueTask DisposeAsync()
    {
        Close("dispose");
        return ValueTask.CompletedTask;
    }

    public override string ToString() => $"{PeerName} ({RemoteEndPoint})";
}
