namespace Varco.Core.Input;

/// <summary>
/// Chi è il master del gruppo, per l'icona nella tray: il PC su cui qualcuno ha toccato mouse o tastiera più di recente
/// (input fisico, non quello iniettato da Varco). Gli altri sono slave, anche se il mouse non ha mai lasciato il master:
/// ogni PC dice agli altri da quanto non lo si tocca (<see cref="Protocol.InputActivity"/>), così lo sanno tutti appena
/// si connettono. Mentre il master comanda uno slave, l'input fisico continua ad arrivare dal master, che resta tale.
/// I PC con versioni vecchie non mandano niente e non contano. Thread-safe.
/// </summary>
public sealed class ControlRole(Guid self)
{
    private readonly object _gate = new();
    /// <summary>Ultimo input fisico di ogni PC che l'ha detto, in ms di TickCount64 di questo PC; long.MinValue = mai.</summary>
    private readonly Dictionary<Guid, long> _peerLastInput = new();

    /// <summary>Quanto mandare agli altri: ms dall'ultimo input fisico, <see cref="uint.MaxValue"/> se mai.</summary>
    public static uint IdleMs(long lastInputMs, long nowMs) =>
        lastInputMs == 0 ? uint.MaxValue : (uint)Math.Clamp(nowMs - lastInputMs, 0, uint.MaxValue - 1);

    public void OnPeerActivity(Guid peer, uint idleMs, long nowMs)
    {
        lock (_gate) _peerLastInput[peer] = idleMs == uint.MaxValue ? long.MinValue : nowMs - idleMs;
    }

    public void Forget(Guid peer)
    {
        lock (_gate) _peerLastInput.Remove(peer);
    }

    /// <summary>Il master del gruppo fra questo PC e quelli connessi; null = questo PC.</summary>
    /// <param name="ownLastInputMs">Ultimo input fisico su questo PC; 0 = mai.</param>
    /// <param name="connected">PC connessi adesso: quelli scollegati non contano.</param>
    public Guid? Master(long ownLastInputMs, Func<Guid, bool> connected)
    {
        lock (_gate)
        {
            var best = self;
            var bestAt = ownLastInputMs == 0 ? long.MinValue : ownLastInputMs;
            foreach (var (peer, at) in _peerLastInput)
            {
                if (!connected(peer)) continue;
                // A pari tempo (succede solo con "mai" da entrambe le parti) vince l'id più basso: tutti i PC decidono uguale.
                if (at > bestAt || (at == bestAt && peer.CompareTo(best) < 0))
                {
                    best = peer;
                    bestAt = at;
                }
            }
            return best == self ? null : best;
        }
    }
}
