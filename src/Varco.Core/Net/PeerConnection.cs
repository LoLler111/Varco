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
    // Dati voluminosi (appunti): un pezzo per scrittura, dopo i messaggi normali, così un'immagine copiata non rallenta il mouse.
    private readonly System.Collections.Concurrent.ConcurrentQueue<byte[]> _bulk = new();
    /// <summary>Segnaposto nell'outbox: "c'è qualcosa in <see cref="_bulk"/>". Mai inviato.</summary>
    private static readonly byte[] BulkReady = new byte[0];
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<string> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int _intervalMs;
    private readonly int _timeoutMs;
    private long _lastReceived;
    private long _lastSent;
    private long _lastInput = long.MinValue / 2;
    private long _lastPing;
    private int _closing;
    private readonly SocketQos? _qos;

    // Mentre si usa il mouse su questo collegamento si manda un ping ogni ActivePingMs:
    //  • la scheda Wi-Fi in risparmio energetico non si "addormenta" e non consegna i movimenti a raffiche;
    //  • se un pacchetto si perde, il traffico continuo fa partire subito la ritrasmissione TCP invece di aspettare centinaia di ms.
    private const int ActivePingMs = 20;
    private const int ActiveWindowMs = 30_000;
    private const int StatsEveryMs = 10_000;
    private readonly object _statsGate = new();
    private int _rttCount, _rttSlow;
    private double _rttSum, _rttMax;
    private long _lastStats = Environment.TickCount64;

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
        _qos = SocketQos.TryApply(socket);
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
        if (IsInput(m)) Volatile.Write(ref _lastInput, Environment.TickCount64);
        if (m is ClipChunk chunk)
        {
            // Il primo pezzo di nuovi appunti rende inutile il resto di quelli vecchi ancora in coda.
            if (chunk.Offset == 0) _bulk.Clear();
            _bulk.Enqueue(MessageCodec.Encode(m));
            _outbox.Writer.TryWrite(BulkReady);
            return;
        }
        _outbox.Writer.TryWrite(MessageCodec.Encode(m));
    }

    private static bool IsInput(Message m) => m is MouseMove or MouseButtonMsg or MouseWheel or KeyMsg or Enter;

    private bool InputActive => Environment.TickCount64 - Volatile.Read(ref _lastInput) < ActiveWindowMs;

    private void RecordRtt(double ms)
    {
        RttMs = ms;
        if (!InputActive) return;
        lock (_statsGate)
        {
            _rttCount++;
            _rttSum += ms;
            if (ms > _rttMax) _rttMax = ms;
            if (ms > 50) _rttSlow++;
        }
    }

    private void MaybeLogStats(long now)
    {
        if (now - _lastStats < StatsEveryMs) return;
        _lastStats = now;
        lock (_statsGate)
        {
            if (_rttCount > 0)
                Log.Info($"Latenza con {PeerName}: media {_rttSum / _rttCount:0.0} ms, massima {_rttMax:0} ms, " +
                         $"oltre 50 ms {_rttSlow} su {_rttCount}");
            _rttCount = _rttSlow = 0;
            _rttSum = _rttMax = 0;
        }
    }

    public void Close(string reason)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0) return;
        _outbox.Writer.TryComplete();
        _cts.Cancel();
        _qos?.Dispose();
        try { _socket.Shutdown(SocketShutdown.Both); } catch { }
        try { _socket.Close(0); } catch { }
        _cipher.Dispose();
        _closed.TrySetResult(reason);
    }

    private async Task ReadLoop()
    {
        try
        {
            // Bufferizzato: intestazione e contenuto di un frame (e i frame arrivati insieme sul Wi-Fi) con una sola lettura dal socket.
            // Dopo l'handshake nessuno legge più direttamente da _stream, quindi non si perdono byte.
            var input = new BufferedStream(_stream, 16 * 1024);
            while (!_cts.IsCancellationRequested)
            {
                var frame = await Framing.ReadFrameAsync(input, _cts.Token).ConfigureAwait(false);
                Volatile.Write(ref _lastReceived, Environment.TickCount64);
                var msg = MessageCodec.Decode(_cipher.Open(frame));
                switch (msg)
                {
                    case null: break;
                    case Ping p: Send(new Pong(p.Timestamp)); break;
                    case Pong p: RecordRtt((Stopwatch.GetTimestamp() - p.Timestamp) * 1000.0 / Stopwatch.Frequency); break;
                    default:
                        if (IsInput(msg)) Volatile.Write(ref _lastInput, Environment.TickCount64);
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
        // Tutti i messaggi già in coda (es. un Pong e un movimento) partono con una sola scrittura, quindi in un solo pacchetto:
        // sul Wi-Fi ogni pacchetto in meno è tempo d'antenna in meno. Nessuna attesa: si manda solo ciò che è già pronto.
        var reader = _outbox.Reader;
        var batch = new System.Buffers.ArrayBufferWriter<byte>(512);
        try
        {
            while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
            {
                batch.ResetWrittenCount();
                var bulkPending = false;
                while (batch.WrittenCount < 64 * 1024 && reader.TryRead(out var plain))
                {
                    if (ReferenceEquals(plain, BulkReady)) bulkPending = true;
                    else Framing.AppendFrame(batch, _cipher.Seal(plain));
                }
                // Al massimo un pezzo voluminoso per scrittura; se ne restano, ci si risveglia per il prossimo.
                if ((bulkPending || !_bulk.IsEmpty) && _bulk.TryDequeue(out var big))
                {
                    Framing.AppendFrame(batch, _cipher.Seal(big));
                    if (!_bulk.IsEmpty) _outbox.Writer.TryWrite(BulkReady);
                }
                if (batch.WrittenCount == 0) continue;
                await _stream.WriteAsync(batch.WrittenMemory, _cts.Token).ConfigureAwait(false);
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
        var idleTick = Math.Max(50, _intervalMs / 2);
        try
        {
            while (!_cts.IsCancellationRequested)
            {
                var active = InputActive;
                await Task.Delay(active ? ActivePingMs / 2 : idleTick, _cts.Token).ConfigureAwait(false);
                var now = Environment.TickCount64;
                if (now - Volatile.Read(ref _lastReceived) > _timeoutMs)
                {
                    Close($"nessuna risposta da {_timeoutMs} ms");
                    return;
                }
                var due = active ? now - _lastPing >= ActivePingMs : now - Volatile.Read(ref _lastSent) >= _intervalMs;
                if (due)
                {
                    _lastPing = now;
                    Send(new Ping(Stopwatch.GetTimestamp()));
                }
                if (active) MaybeLogStats(now);
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
