using Varco.Core.Net;
using Varco.Core.Protocol;

namespace Varco.Core.Audio;

/// <summary>
/// Audio fra i PC (vedi <see cref="AudioRouting"/>), indipendente da Windows:
///  • sui PC che mandano: finché il PC centrale è collegato, cattura ciò che esce dalle casse, lo comprime e glielo manda;
///  • sul PC centrale: per ogni PC che manda, decomprime e riproduce, con il nome "Audio NOME-PC" nel Mixer volume;
///  • tiene allineata l'impostazione su tutto il gruppo.
/// I messaggi arrivano dai thread della rete, i comandi da quello dell'icona: tutto thread-safe.
/// </summary>
public sealed class AudioShare : IDisposable
{
    /// <summary>Dopo quanto tempo senza pacchetti si chiude la riproduzione di un PC (e sparisce dal Mixer volume).</summary>
    public const long PlayerIdleMs = 30_000;
    /// <summary>Un PC conta come "sta mandando audio" se l'ultimo pacchetto è più recente di così.</summary>
    public const long ActiveMs = 3_000;
    /// <summary>Un salto di numerazione più grande di così vuol dire che l'altro PC ha ricominciato da capo.</summary>
    private const int MaxGap = 1000;

    private readonly IPeerNetwork _net;
    private readonly IAudioPlatform? _platform;
    private readonly Func<AudioRouting> _get;
    private readonly Action<AudioRouting> _save;
    private readonly Func<Guid, string> _nameOf;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Receiver> _receivers = new();
    private readonly Timer _sweep;
    private IDisposable? _capture;
    private Guid _captureHub;
    private bool _disposed;

    /// <param name="get">Impostazione attuale (dalla configurazione).</param>
    /// <param name="save">Salva un'impostazione nuova nella configurazione.</param>
    /// <param name="nameOf">Nome di un PC del gruppo, per il Mixer volume e per il log.</param>
    public AudioShare(IPeerNetwork net, IAudioPlatform? platform, Func<AudioRouting> get, Action<AudioRouting> save, Func<Guid, string> nameOf)
    {
        _net = net;
        _platform = platform;
        _get = get;
        _save = save;
        _nameOf = nameOf;
        _sweep = new Timer(_ => Sweep(Environment.TickCount64), null, 5000, 5000);
    }

    /// <summary>Questo PC sta mandando il suo audio al centrale?</summary>
    public bool Sending { get { lock (_gate) return _capture is not null; } }

    /// <summary>Questo PC (centrale) sta riproducendo l'audio di un altro PC? Allora il suono che esce da qui non è suo.</summary>
    public bool Playing { get { lock (_gate) return _receivers.Count > 0; } }

    /// <summary>I PC da cui arriva audio in questo momento.</summary>
    public List<Guid> Receiving(long now)
    {
        lock (_gate) return _receivers.Values.Where(r => now - Volatile.Read(ref r.LastFrame) < ActiveMs).Select(r => r.Peer).ToList();
    }

    public void Start() => Update();

    public void OnPeerConnected(Guid peer)
    {
        _net.Send(peer, _get().ToMessage());
        Update();
    }

    public void OnPeerDisconnected(Guid peer)
    {
        Receiver? gone;
        lock (_gate) _receivers.Remove(peer, out gone);
        gone?.Close("si è scollegato");
        Update();
    }

    /// <summary>Gestisce i messaggi dell'audio; false se il messaggio non è suo.</summary>
    public bool OnMessage(Guid from, Message m)
    {
        switch (m)
        {
            case AudioRoutingMsg r:
                OnRouting(from, r);
                return true;
            case AudioFrame f:
                OnFrame(from, f, Environment.TickCount64);
                return true;
            default:
                return false;
        }
    }

    /// <summary>L'utente ha cambiato l'impostazione su questo PC: vale per tutto il gruppo.</summary>
    public void SetRouting(Guid hub, IEnumerable<Guid> sources, DateTime nowUtc)
    {
        var next = AudioRouting.Changed(_get(), hub, sources, nowUtc);
        _save(next);
        Log.Info(Describe("Audio fra i PC impostato qui", next));
        _net.Broadcast(next.ToMessage());
        Update();
    }

    /// <summary>
    /// Dove vanno i tasti del volume diretti a <paramref name="pc"/>: se quel PC manda il suo audio al centrale, il suo
    /// volume non conta più (si sente dal centrale), quindi vanno al centrale. Null = nessuna deviazione.
    /// </summary>
    public Guid? VolumeKeysTarget(Guid pc)
    {
        var r = _get();
        if (!r.Sends(pc)) return null;
        var self = _net.SelfId;
        var hubReachable = r.Hub == self || _net.IsConnected(r.Hub);
        var pcSending = pc == self ? Sending : _net.IsConnected(pc);
        return hubReachable && pcSending ? r.Hub : null;
    }

    private void OnRouting(Guid from, AudioRoutingMsg m)
    {
        var current = _get();
        if (!current.IsOlderThan(m)) return;
        var next = AudioRouting.FromMessage(m);
        _save(next);
        Log.Info(Describe($"Audio fra i PC cambiato da {_nameOf(from)}", next));
        Update();
    }

    private void OnFrame(Guid from, AudioFrame f, long now)
    {
        if (_platform is null) return;
        var r = _get();
        if (r.Hub != _net.SelfId || !r.Sources.Contains(from)) return; // non per noi, o l'impostazione è appena cambiata
        Receiver? rx;
        lock (_gate)
        {
            if (_disposed) return;
            if (!_receivers.TryGetValue(from, out rx))
            {
                var name = _nameOf(from);
                rx = new Receiver(from, name) { LastFrame = now };
                try { rx.Player = _platform.StartPlayer(AudioRouting.SessionFor(from), $"Audio {name}", rx.Buffer); }
                catch (Exception ex)
                {
                    Log.Error($"Riproduzione dell'audio di {name}", ex);
                    return;
                }
                _receivers[from] = rx;
                Log.Info($"Arriva l'audio di {name}: lo riproduco qui");
            }
        }
        rx.Add(f, now);
    }

    private void Update()
    {
        var r = _get();
        var self = _net.SelfId;
        var send = _platform is not null && r.Sends(self) && _net.IsConnected(r.Hub);
        var closed = new List<(Receiver Rx, string Why)>();
        lock (_gate)
        {
            if (_disposed) return;
            if (_capture is not null && (!send || _captureHub != r.Hub))
            {
                _capture.Dispose();
                _capture = null;
                Log.Info(send || !r.Sends(self) ? "Smetto di mandare l'audio di questo PC" : "PC centrale scollegato: l'audio torna sulle casse di questo PC");
            }
            if (send && _capture is null)
            {
                var sender = new Sender(_net, r.Hub);
                try
                {
                    _capture = _platform!.StartCapture(sender.OnSamples);
                    _captureHub = r.Hub;
                    Log.Info($"Mando l'audio di questo PC a {_nameOf(r.Hub)} (casse di questo PC mute)");
                }
                catch (Exception ex) { Log.Error("Cattura dell'audio", ex); }
            }
            foreach (var rx in _receivers.Values.ToList())
            {
                if (r.Hub == self && r.Sources.Contains(rx.Peer)) continue;
                _receivers.Remove(rx.Peer);
                closed.Add((rx, "non è più tra i PC che mandano l'audio qui"));
            }
        }
        foreach (var (rx, why) in closed) rx.Close(why);
    }

    private void Sweep(long now)
    {
        var idle = new List<Receiver>();
        lock (_gate)
        {
            foreach (var rx in _receivers.Values.ToList())
            {
                if (now - Volatile.Read(ref rx.LastFrame) < PlayerIdleMs) continue;
                _receivers.Remove(rx.Peer);
                idle.Add(rx);
            }
        }
        foreach (var rx in idle) rx.Close("nessun suono da un po'");
    }

    private string Describe(string what, AudioRouting r) =>
        !r.Enabled ? $"{what}: spento"
        : $"{what}: PC centrale {_nameOf(r.Hub)}, mandano l'audio {(r.Sources.Count == 0 ? "nessuno" : string.Join(", ", r.Sources.Select(_nameOf)))}";

    public void Dispose()
    {
        List<Receiver> all;
        lock (_gate)
        {
            _disposed = true;
            _capture?.Dispose();
            _capture = null;
            all = _receivers.Values.ToList();
            _receivers.Clear();
        }
        _sweep.Dispose();
        foreach (var rx in all) rx.Close("chiusura");
    }

    /// <summary>Sul PC che manda: comprime e spedisce al centrale. Usato solo dal thread di cattura.</summary>
    private sealed class Sender(IPeerNetwork net, Guid hub)
    {
        private readonly AudioEncoder _encoder = new();
        private ushort _seq;

        public void OnSamples(ReadOnlySpan<float> samples)
        {
            try { _encoder.Push(samples, packet => net.Send(hub, new AudioFrame(_seq++, packet))); }
            catch (Exception ex) { Log.Error("Compressione dell'audio", ex); }
        }
    }

    /// <summary>Sul centrale: l'audio di un PC, dalla rete alla scheda audio.</summary>
    private sealed class Receiver(Guid peer, string name)
    {
        private readonly object _gate = new();
        private readonly AudioDecoder _decoder = new();
        private ushort _nextSeq;
        private bool _started;
        private long _lost;

        public Guid Peer { get; } = peer;
        public PlayoutBuffer Buffer { get; } = new();
        public IDisposable? Player;
        public long LastFrame;

        public void Add(AudioFrame f, long now)
        {
            lock (_gate)
            {
                if (_started && f.Seq != _nextSeq)
                {
                    var gap = (ushort)(f.Seq - _nextSeq);
                    if (gap < MaxGap) _lost += gap; // scartati da chi manda: la rete non stava al passo
                    else
                    {
                        // L'altro PC ha ricominciato a mandare da capo: niente code del flusso vecchio.
                        _decoder.Reset();
                        Buffer.Clear();
                    }
                }
                _started = true;
                _nextSeq = (ushort)(f.Seq + 1);
                try { Buffer.Push(_decoder.Decode(f.Data), now); }
                catch (Exception ex) { Log.Warn($"Pacchetto audio di {name} illeggibile: {ex.Message}"); }
            }
            Volatile.Write(ref LastFrame, now);
        }

        public void Close(string why)
        {
            try { Player?.Dispose(); }
            catch (Exception ex) { Log.Error($"Chiusura dell'audio di {name}", ex); }
            Log.Info($"Audio di {name} chiuso ({why}): svuotamenti {Buffer.Underruns}, " +
                     $"ritardo finale {Buffer.TargetMs} ms, pacchetti persi {_lost}, ms buttati {Buffer.DroppedFrames * 1000 / AudioFormat.SampleRate}");
        }
    }
}
