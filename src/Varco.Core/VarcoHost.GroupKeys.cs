using Varco.Core.Config;
using Varco.Core.Net;
using Varco.Core.Protocol;

namespace Varco.Core;

/// <summary>
/// Cambio della chiave di gruppo quando si rimuove un PC: il PC rimosso conosce la chiave vecchia, quindi se si
/// riaccendesse si ricollegherebbe. Con la chiave nuova resta fuori.
///
///  • Chi rimuove crea una chiave nuova e la manda a tutti i PC collegati, che la adottano.
///  • La chiave vecchia resta valida solo per i PC del gruppo che in quel momento erano spenti (elenco "in attesa"):
///    quando si riaccendono si presentano con quella, ricevono la nuova e da lì in poi usano solo la nuova.
///  • Il PC rimosso non è in nessun elenco, quindi con la chiave vecchia non passa più.
///
/// Le versioni senza questa funzione ignorano il messaggio: restano collegate con la chiave vecchia (sono "in attesa")
/// finché non si aggiornano, e poi ricevono la nuova come gli altri.
/// </summary>
public sealed partial class VarcoHost
{
    private const int MaxRetiredKeys = 4;
    /// <summary>Un PC spento più a lungo di così va ricollegato a mano.</summary>
    private static readonly TimeSpan RetiredKeyLifetime = TimeSpan.FromDays(365);

    /// <summary>Chiavi sostituite che il PC indicato può ancora usare (lo chiede il nodo a ogni connessione).</summary>
    private IReadOnlyList<byte[]> RetiredKeysFor(Guid peer)
    {
        var list = new List<byte[]>();
        foreach (var r in _store.Current.RetiredKeys) // la lista viene sempre sostituita intera: lettura senza lock
            if (r.Pending.Contains(peer) && DateTime.UtcNow - r.RetiredUtc < RetiredKeyLifetime && GroupKey.TryParse(r.Key, out var k))
                list.Add(k);
        return list;
    }

    /// <summary>
    /// Nuova connessione: se è nata con una chiave sostituita, l'altro PC era spento quando la chiave è cambiata e gli
    /// mando quella attuale. Altrimenti ha già la chiave attuale e non è più "in attesa" di niente.
    /// </summary>
    private void CheckGroupKey(PeerConnection c)
    {
        if (!GroupKey.TryParse(_store.Current.GroupKey, out var current)) return;
        if (c.GroupKey.AsSpan().SequenceEqual(current))
        {
            MarkUpToDate(c.PeerId);
            return;
        }
        Log.Info($"{c.PeerName} si è collegato con la chiave di gruppo precedente: gli mando quella nuova");
        var cfg = _store.Current;
        _net.Send(c.PeerId, new GroupKeyChange(current, cfg.GroupKeyChangedUtc.Ticks, [.. cfg.RemovedPeers]));
    }

    private void MarkUpToDate(Guid peer)
    {
        if (!_store.Current.RetiredKeys.Any(r => r.Pending.Contains(peer))) return;
        _store.Update(c => c.RetiredKeys = c.RetiredKeys
            .Select(r => new RetiredGroupKey { Key = r.Key, RetiredUtc = r.RetiredUtc, Pending = r.Pending.Where(p => p != peer).ToList() })
            .Where(r => r.Pending.Count > 0)
            .ToList());
    }

    /// <summary>«Rimuovi»: il PC esce dai PC noti e dalla disposizione, e la chiave di gruppo cambia.</summary>
    private async Task ForgetAsync(Guid id)
    {
        var name = _store.Current.KnownPeers.FirstOrDefault(p => p.Id == id)?.Name ?? id.ToString()[..8];
        var key = System.Security.Cryptography.RandomNumberGenerator.GetBytes(GroupKey.KeyBytes);
        var now = DateTime.UtcNow;
        var others = ConnectedPeers().Where(p => p.Item1 != id).Select(p => p.Item2).ToList();
        Log.Info($"{name} rimosso dal gruppo: cambio la chiave" + (others.Count > 0 ? $" e la mando a {string.Join(", ", others)}" : ""));
        AdoptGroupKey(key, now, [id]);
        _net.Broadcast(new GroupKeyChange(key, now.Ticks, [.. _store.Current.RemovedPeers]));
        await Task.Delay(others.Count > 0 ? 500 : 0).ConfigureAwait(false); // il messaggio deve partire prima di chiudere le connessioni
        await RestartNetworkAsync().ConfigureAwait(false);
    }

    private void OnGroupKeyChange(Guid from, GroupKeyChange m)
    {
        var cfg = _store.Current;
        if (m.Key.Length != GroupKey.KeyBytes || !GroupKey.TryParse(cfg.GroupKey, out var current)) return;
        var name = ConnectedPeers().FirstOrDefault(p => p.Item1 == from).Item2 ?? from.ToString()[..8];
        var removed = m.Removed.Where(r => r != cfg.MachineId).ToList();
        if (m.Removed.Contains(cfg.MachineId))
        {
            Log.Warn($"{name} manda una chiave di gruppo che esclude questo PC: la ignoro");
            return;
        }
        if (m.Key.AsSpan().SequenceEqual(current))
        {
            ApplyRemovals(removed);
            return;
        }
        // Connessione nata con la mia chiave attuale: per l'altro PC è già una chiave sostituita, quindi la sua è più nuova.
        // Altrimenti abbiamo cambiato chiave tutti e due mentre eravamo scollegati: vince il cambio più recente.
        var viaMine = _node?.GroupKeyOf(from) is { } used && used.AsSpan().SequenceEqual(current);
        if (!viaMine)
        {
            var mine = cfg.GroupKeyChangedUtc.Ticks;
            if (m.ChangedUtcTicks < mine || (m.ChangedUtcTicks == mine && m.Key.AsSpan().SequenceCompareTo(current) <= 0))
            {
                Log.Info($"{name} ha cambiato la chiave di gruppo mentre eravamo scollegati, ma il cambio di questo PC è più recente: tengo la mia");
                return;
            }
        }
        Log.Info($"Nuova chiave di gruppo ricevuta da {name}" + (removed.Count > 0 ? $" ({removed.Count} PC rimossi)" : ""));
        AdoptGroupKey(m.Key, new DateTime(Math.Clamp(m.ChangedUtcTicks, 0, DateTime.MaxValue.Ticks), DateTimeKind.Utc), removed);
        // Il riavvio chiude anche la connessione da cui è arrivato il messaggio: lo faccio subito dopo.
        _ = Task.Run(async () =>
        {
            await Task.Delay(300).ConfigureAwait(false);
            await RestartNetworkAsync().ConfigureAwait(false);
        });
    }

    /// <summary>
    /// Passa alla chiave nuova. La vecchia resta valida per i PC noti (tranne i rimossi), finché non si collegano con la nuova.
    /// </summary>
    private void AdoptGroupKey(byte[] key, DateTime changedUtc, IReadOnlyCollection<Guid> removed)
    {
        var now = DateTime.UtcNow;
        _store.Update(c =>
        {
            var pending = c.KnownPeers.Select(p => p.Id).Where(p => !removed.Contains(p)).ToList();
            var retired = new List<RetiredGroupKey>();
            if (c.GroupKey is { } old && pending.Count > 0)
                retired.Add(new RetiredGroupKey { Key = old, Pending = pending, RetiredUtc = now });
            foreach (var r in c.RetiredKeys)
            {
                var keep = r.Pending.Where(p => !removed.Contains(p)).ToList();
                if (keep.Count > 0 && now - r.RetiredUtc < RetiredKeyLifetime)
                    retired.Add(new RetiredGroupKey { Key = r.Key, Pending = keep, RetiredUtc = r.RetiredUtc });
            }
            c.RetiredKeys = retired.Take(MaxRetiredKeys).ToList();
            c.GroupKey = GroupKey.Format(key);
            c.GroupKeyChangedUtc = changedUtc;
            c.KnownPeers = c.KnownPeers.Where(p => !removed.Contains(p.Id)).ToList();
            c.RemovedPeers = c.RemovedPeers.Concat(removed).Distinct().TakeLast(64).ToList();
        }, immediate: true);
        if (removed.Count > 0) Layout.Remove(removed);
    }

    /// <summary>PC rimossi da un altro del gruppo che ha già la nostra stessa chiave.</summary>
    private void ApplyRemovals(IReadOnlyCollection<Guid> removed)
    {
        var cfg = _store.Current;
        if (removed.Count == 0 || !cfg.KnownPeers.Any(p => removed.Contains(p.Id)) && !cfg.RetiredKeys.Any(r => r.Pending.Any(removed.Contains)))
            return;
        _store.Update(c =>
        {
            c.KnownPeers = c.KnownPeers.Where(p => !removed.Contains(p.Id)).ToList();
            c.RetiredKeys = c.RetiredKeys
                .Select(r => new RetiredGroupKey { Key = r.Key, RetiredUtc = r.RetiredUtc, Pending = r.Pending.Where(p => !removed.Contains(p)).ToList() })
                .Where(r => r.Pending.Count > 0)
                .ToList();
            c.RemovedPeers = c.RemovedPeers.Concat(removed).Distinct().TakeLast(64).ToList();
        }, immediate: true);
        Layout.Remove(removed);
    }

    /// <summary>Nuovo gruppo (abbinamento in un altro gruppo, chiave scritta a mano, «esci dal gruppo»): niente del vecchio resta valido.</summary>
    private static void ResetGroupHistory(VarcoConfig c)
    {
        c.RetiredKeys = [];
        c.RemovedPeers = [];
        c.GroupKeyChangedUtc = default;
    }
}
