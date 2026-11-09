using Varco.Core.Protocol;

namespace Varco.Core.Input;

public sealed class LayoutEntry
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
}

/// <summary>
/// Disposizione dei PC su una griglia (X verso destra, Y verso il basso).
/// È condivisa da tutto il gruppo: vince la versione più alta (a parità, l'autore con ID maggiore).
/// </summary>
public sealed class Layout
{
    public long Version { get; set; }
    public Guid Author { get; set; }
    public List<LayoutEntry> Entries { get; set; } = [];

    public bool IsNewerThan(Layout other) =>
        Version > other.Version || (Version == other.Version && Author.CompareTo(other.Author) > 0);

    public LayoutEntry? Find(Guid id) => Entries.FirstOrDefault(e => e.Id == id);

    public Guid? Neighbor(Guid from, Edge direction)
    {
        var e = Find(from);
        if (e is null) return null;
        var (x, y) = direction switch
        {
            Edge.Left => (e.X - 1, e.Y),
            Edge.Right => (e.X + 1, e.Y),
            Edge.Top => (e.X, e.Y - 1),
            _ => (e.X, e.Y + 1),
        };
        return Entries.FirstOrDefault(n => n.X == x && n.Y == y)?.Id;
    }

    public Layout Clone() => new()
    {
        Version = Version,
        Author = Author,
        Entries = Entries.Select(e => new LayoutEntry { Id = e.Id, Name = e.Name, X = e.X, Y = e.Y }).ToList(),
    };
}

/// <summary>Mantiene il layout coerente fra i PC: unione dei membri, versioni crescenti.</summary>
public sealed class LayoutManager
{
    private readonly object _gate = new();
    private readonly Guid _self;
    private readonly string _selfName;
    private Layout _current;

    /// <summary>Il layout è cambiato: va salvato e inviato agli altri.</summary>
    public event Action<Layout>? Changed;

    public LayoutManager(Guid self, string selfName, Layout initial)
    {
        _self = self;
        _selfName = selfName;
        _current = initial.Clone();
        EnsureMembers([]);
    }

    public Layout Current { get { lock (_gate) return _current.Clone(); } }

    public Guid? Neighbor(Guid from, Edge direction)
    {
        lock (_gate) return _current.Neighbor(from, direction);
    }

    /// <summary>Applica un layout ricevuto se più recente. Restituisce true se è stato adottato.</summary>
    public bool Merge(Layout incoming, IEnumerable<(Guid Id, string Name)> connected)
    {
        bool adopted;
        lock (_gate)
        {
            adopted = incoming.IsNewerThan(_current);
            if (adopted) _current = incoming.Clone();
        }
        // Se il layout adottato non contiene me o i PC connessi li aggiungo (e riparte la propagazione).
        var bumped = EnsureMembers(connected);
        if (adopted && !bumped) Raise();
        return adopted;
    }

    /// <summary>Aggiunge in coda (a destra) i PC mancanti. Restituisce true se ha modificato il layout.</summary>
    public bool EnsureMembers(IEnumerable<(Guid Id, string Name)> members)
    {
        bool changed = false;
        lock (_gate)
        {
            foreach (var (id, name) in members.Prepend((_self, _selfName)))
            {
                if (_current.Find(id) is not null) continue;
                var y = 0;
                var row = _current.Entries.Where(e => e.Y == y).ToList();
                var x = row.Count == 0 ? 0 : row.Max(e => e.X) + 1;
                _current.Entries.Add(new LayoutEntry { Id = id, Name = name, X = x, Y = y });
                changed = true;
            }
            if (changed) Bump();
        }
        if (changed) Raise();
        return changed;
    }

    /// <summary>Riparte da zero (es. ingresso in un altro gruppo): la versione 0 perde contro qualsiasi layout del gruppo.</summary>
    public void Reset()
    {
        lock (_gate) _current = new Layout();
        EnsureMembers([]);
        lock (_gate) { _current.Version = 0; _current.Author = Guid.Empty; }
        Raise();
    }

    /// <summary>Scelta dell'utente: PC in fila da sinistra a destra.</summary>
    public void SetRow(IReadOnlyList<(Guid Id, string Name)> order)
    {
        lock (_gate)
        {
            _current.Entries = order.Select((p, i) => new LayoutEntry { Id = p.Id, Name = p.Name, X = i, Y = 0 }).ToList();
            if (_current.Find(_self) is null)
                _current.Entries.Add(new LayoutEntry { Id = _self, Name = _selfName, X = order.Count, Y = 0 });
            Bump();
        }
        Raise();
    }

    private void Bump()
    {
        _current.Version++;
        _current.Author = _self;
    }

    private void Raise()
    {
        var snapshot = Current;
        try { Changed?.Invoke(snapshot); }
        catch (Exception ex) { Log.Error("Layout.Changed", ex); }
    }
}
