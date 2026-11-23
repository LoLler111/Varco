namespace Varco.Core.Audio;

/// <summary>
/// Riserva di audio fra la rete e la scheda audio del PC centrale. I pacchetti arrivano a scatti (Wi-Fi, ritrasmissioni
/// TCP) ma la scheda audio li consuma a ritmo costante: si parte solo quando la riserva arriva all'obiettivo, e
/// l'obiettivo si adatta alla rete.
///  • Se la riserva si svuota mentre arrivano ancora pacchetti (rete lenta), si riparte da capo con un obiettivo più alto.
///  • Dopo un po' senza problemi l'obiettivo torna a scendere, così il ritardo resta il più basso che la rete permette.
///  • Se la riserva cresce troppo (raffica dopo un blocco, orologi delle schede audio un po' diversi) si buttano i
///    campioni più vecchi: meglio un salto che un ritardo che si accumula. Lo stesso se per un secondo intero non scende
///    mai fino all'obiettivo (audio accumulato all'avvio, mentre la scheda audio si apriva).
/// Se invece smettono di arrivare pacchetti (musica in pausa) non è colpa della rete: l'obiettivo non cambia.
/// Thread-safe: la rete scrive, il thread della scheda audio legge. I tempi sono in ms (TickCount64).
/// </summary>
public sealed class PlayoutBuffer
{
    public static readonly int MinTargetFrames = AudioFormat.MsToFrames(40);
    public static readonly int MaxTargetFrames = AudioFormat.MsToFrames(300);
    public static readonly int StepFrames = AudioFormat.MsToFrames(20);
    /// <summary>Oltre l'obiettivo più questo margine si buttano i campioni più vecchi.</summary>
    public static readonly int SlackFrames = AudioFormat.MsToFrames(120);
    /// <summary>Dopo quanto tempo senza svuotamenti l'obiettivo scende di un passo.</summary>
    public const long RelaxMs = 20_000;
    /// <summary>Se l'ultimo pacchetto è più vecchio di così, la riserva vuota vuol dire "nessun suono", non "rete lenta".</summary>
    public const long StreamGapMs = 150;
    /// <summary>Ogni quanto si guarda il punto più basso toccato dalla riserva, per togliere l'eccesso.</summary>
    public const long TrimWindowMs = 1000;

    private const int C = AudioFormat.Channels;
    private readonly object _gate = new();
    private readonly float[] _ring = new float[AudioFormat.SampleRate * C * 2]; // 2 s
    private int _start, _count; // in float
    private int _target = MinTargetFrames;
    private bool _buffering = true;
    private long _lastPush = long.MinValue / 2;
    private long _calmSince;
    private long _windowStart;
    private int _windowMin = int.MaxValue; // float

    /// <summary>Svuotamenti dovuti alla rete e campioni buttati (in frame), per il log.</summary>
    public int Underruns { get; private set; }
    public long DroppedFrames { get; private set; }

    public int TargetMs { get { lock (_gate) return _target * 1000 / AudioFormat.SampleRate; } }
    public int BufferedMs { get { lock (_gate) return _count / C * 1000 / AudioFormat.SampleRate; } }

    public void Push(ReadOnlySpan<float> samples, long now)
    {
        lock (_gate)
        {
            if (now - _calmSince >= RelaxMs && _target > MinTargetFrames)
            {
                _target = Math.Max(MinTargetFrames, _target - StepFrames);
                _calmSince = now;
            }
            _lastPush = now;
            if (samples.Length > _ring.Length) samples = samples[^_ring.Length..];
            var overflow = _count + samples.Length - _ring.Length;
            if (overflow > 0) Drop(overflow);
            var end = (_start + _count) % _ring.Length;
            var first = Math.Min(samples.Length, _ring.Length - end);
            samples[..first].CopyTo(_ring.AsSpan(end));
            samples[first..].CopyTo(_ring);
            _count += samples.Length;
            if (_count > (_target + SlackFrames) * C) Drop(_count - _target * C);
        }
    }

    /// <summary>Riempie sempre tutto <paramref name="dest"/> (con silenzio se manca l'audio). Restituisce i float di audio vero.</summary>
    public int Read(Span<float> dest, long now)
    {
        lock (_gate)
        {
            if (_buffering)
            {
                if (_count < _target * C)
                {
                    dest.Clear();
                    return 0;
                }
                _buffering = false;
            }
            var n = Math.Min(_count, dest.Length);
            var first = Math.Min(n, _ring.Length - _start);
            _ring.AsSpan(_start, first).CopyTo(dest);
            _ring.AsSpan(0, n - first).CopyTo(dest[first..]);
            _start = (_start + n) % _ring.Length;
            _count -= n;
            if (n == dest.Length)
            {
                // Per un secondo la riserva è sempre rimasta sopra l'obiettivo di almeno un passo: l'eccesso è solo ritardo.
                _windowMin = Math.Min(_windowMin, _count);
                if (now - _windowStart >= TrimWindowMs)
                {
                    if (_windowMin > (_target + StepFrames) * C) Drop(_windowMin - _target * C);
                    _windowStart = now;
                    _windowMin = int.MaxValue;
                }
            }
            else
            {
                dest[n..].Clear();
                _buffering = true;
                _windowStart = now;
                _windowMin = int.MaxValue;
                if (now - _lastPush < StreamGapMs)
                {
                    // Arrivano ancora pacchetti ma non abbastanza in fretta: serve più riserva.
                    _target = Math.Min(MaxTargetFrames, _target + StepFrames);
                    _calmSince = now;
                    Underruns++;
                }
            }
            return n;
        }
    }

    /// <summary>Svuota tutto (nuovo flusso, per esempio dopo che l'altro PC è ripartito).</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _start = _count = 0;
            _buffering = true;
            _windowMin = int.MaxValue;
        }
    }

    private void Drop(int floats)
    {
        floats -= floats % C; // sempre frame interi, o i canali si scambierebbero
        floats = Math.Min(floats, _count);
        _start = (_start + floats) % _ring.Length;
        _count -= floats;
        DroppedFrames += floats / C;
    }
}
