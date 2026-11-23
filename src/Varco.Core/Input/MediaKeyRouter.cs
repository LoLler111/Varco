namespace Varco.Core.Input;

/// <summary>
/// A quale PC vanno i tasti multimediali (volume, muto, play/pausa, brano precedente e successivo, stop).
/// Di solito vanno dove va la tastiera (questo PC, o quello che si sta comandando). Ma se la musica suona su un altro
/// PC del gruppo, volume e play/pausa servono lì:
///  1. se suona il PC dove va la tastiera, restano lì;
///  2. altrimenti vanno al PC che suona (se ne suona più d'uno, a quello che ha cominciato per ultimo);
///  3. se non suona nessuno, vanno al PC che ha smesso per ultimo, se è successo da poco: così il secondo play/pausa
///     riprende la musica messa in pausa dal primo;
///  4. altrimenti dove va la tastiera.
/// Thread-safe. I tempi sono in ms (TickCount64).
/// </summary>
public sealed class MediaKeyRouter(Guid self)
{
    /// <summary>Per quanto tempo dopo la fine della musica i tasti vanno ancora a quel PC (ms).</summary>
    public const long RecentMs = 10 * 60_000;

    private sealed class State
    {
        public bool Playing;
        public long StartedAt;
        public long StoppedAt = long.MinValue / 2;
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, State> _states = new();

    public Guid Self { get; } = self;

    /// <summary>Stato audio di questo PC o di un altro (da <see cref="Protocol.AudioActive"/>).</summary>
    public void SetPlaying(Guid id, bool playing, long now)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(id, out var s)) _states[id] = s = new State();
            if (playing == s.Playing) return;
            s.Playing = playing;
            if (playing) s.StartedAt = now; else s.StoppedAt = now;
        }
    }

    /// <summary>Il PC si è scollegato: non riceve più tasti finché non manda di nuovo il suo stato.</summary>
    public void Forget(Guid id)
    {
        lock (_gate) _states.Remove(id);
    }

    /// <summary>Il PC sa ricevere i tasti multimediali (ha mandato il suo stato audio)?</summary>
    public bool Knows(Guid id)
    {
        lock (_gate) return id == Self || _states.ContainsKey(id);
    }

    /// <param name="current">Dove va la tastiera: questo PC o quello comandato.</param>
    /// <param name="reachable">Il PC è collegato (questo PC lo è sempre).</param>
    public Guid Route(Guid current, long now, Func<Guid, bool> reachable)
    {
        lock (_gate)
        {
            if (_states.TryGetValue(current, out var cur) && cur.Playing) return current;
            var candidates = _states.Where(kv => kv.Key == Self || reachable(kv.Key)).ToList();
            var playing = candidates.Where(kv => kv.Value.Playing).OrderByDescending(kv => kv.Value.StartedAt).ToList();
            if (playing.Count > 0) return playing[0].Key;
            var recent = candidates.Where(kv => now - kv.Value.StoppedAt <= RecentMs).OrderByDescending(kv => kv.Value.StoppedAt).ToList();
            return recent.Count > 0 ? recent[0].Key : current;
        }
    }
}

/// <summary>
/// Blocco di gruppo: quando l'utente blocca il PC che ha davanti (Win+L, Start → Blocca, Ctrl+Alt+Canc → Blocca), si
/// bloccano anche gli altri PC collegati. Il blocco automatico per inattività invece resta lì: un PC che si usa da un
/// altro PC si blocca da solo quando nessuno lo comanda, e non deve bloccare il PC su cui si sta lavorando.
/// Si riconosce dall'input fisico: chi blocca a mano ha appena usato mouse o tastiera di quel PC.
/// Thread-safe. I tempi sono in ms (TickCount64).
/// </summary>
public sealed class GroupLockPolicy
{
    /// <summary>Il blocco conta come "a mano" se l'ultimo input fisico è più recente di così (ms).</summary>
    public const long PhysicalWindowMs = 5000;
    /// <summary>Dopo un blocco chiesto da un altro PC, il blocco di questo PC non si rimanda agli altri (ms).</summary>
    public const long EchoWindowMs = 10_000;

    private long _remoteLockAt = long.MinValue / 2;

    /// <summary>Un altro PC ha chiesto di bloccare questo.</summary>
    public void OnRemoteLock(long now) => Interlocked.Exchange(ref _remoteLockAt, now);

    /// <summary>Questo PC si è appena bloccato: va bloccato anche il resto del gruppo?</summary>
    /// <param name="lastPhysicalInputMs">Ultimo input fisico (non iniettato) visto qui; 0 = mai.</param>
    public bool ShouldPropagate(long now, long lastPhysicalInputMs) =>
        lastPhysicalInputMs > 0 && now - lastPhysicalInputMs <= PhysicalWindowMs
        && now - Interlocked.Read(ref _remoteLockAt) > EchoWindowMs;
}
