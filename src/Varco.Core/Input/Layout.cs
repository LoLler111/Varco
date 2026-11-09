using System.Text.Json.Serialization;
using Varco.Core.Protocol;

namespace Varco.Core.Input;

/// <summary>
/// Un PC nella disposizione: rettangolo nello spazio virtuale condiviso (X verso destra, Y verso il basso).
/// Width/Height sono le dimensioni del desktop del PC in pixel fisici (le comunica il PC stesso),
/// così i rettangoli sono proporzionali alla risoluzione come in Impostazioni → Schermo di Windows.
/// </summary>
public sealed class LayoutEntry
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    [JsonIgnore] public int Right => X + Width;
    [JsonIgnore] public int Bottom => Y + Height;

    public LayoutEntry Clone() => new() { Id = Id, Name = Name, X = X, Y = Y, Width = Width, Height = Height };
}

/// <summary>Il PC raggiunto attraversando un bordo e la posizione (0..1) lungo il suo bordo d'ingresso.</summary>
public readonly record struct LayoutHop(Guid Id, float Ratio);

/// <summary>
/// Disposizione 2D dei PC, come i monitor in Impostazioni → Schermo.
/// È condivisa da tutto il gruppo: vince la versione più alta (a parità, l'autore con ID maggiore).
/// </summary>
public sealed class Layout
{
    /// <summary>Dimensione usata finché un PC non ha comunicato la sua.</summary>
    public const int DefaultWidth = 1920, DefaultHeight = 1080;
    /// <summary>Distanza massima (pixel virtuali) perché due rettangoli si considerino a contatto.</summary>
    public const int EdgeTolerance = 4;

    public long Version { get; set; }
    public Guid Author { get; set; }
    public List<LayoutEntry> Entries { get; set; } = [];

    public bool IsNewerThan(Layout other) =>
        Version > other.Version || (Version == other.Version && Author.CompareTo(other.Author) > 0);

    public LayoutEntry? Find(Guid id) => Entries.FirstOrDefault(e => e.Id == id);

    /// <summary>
    /// Il cursore esce da <paramref name="from"/> attraverso <paramref name="edge"/> alla posizione <paramref name="ratio"/> (0..1 lungo quel bordo):
    /// restituisce il PC il cui rettangolo tocca quel punto del bordo e la posizione corrispondente lungo il suo bordo d'ingresso.
    /// Null se in quel punto non c'è nessun PC accanto (il cursore resta dov'è, come fa Windows).
    /// </summary>
    public LayoutHop? Cross(Guid from, Edge edge, float ratio)
    {
        var f = Find(from);
        if (f is null) return null;
        ratio = Math.Clamp(ratio, 0f, 1f);
        var sideways = edge is Edge.Left or Edge.Right;
        double along = sideways ? f.Y + ratio * f.Height : f.X + ratio * f.Width;

        LayoutEntry? best = null;
        int bestGap = int.MaxValue;
        foreach (var n in Entries)
        {
            if (n.Id == from || n.Width <= 0 || n.Height <= 0) continue;
            if (sideways ? along < n.Y || along >= n.Bottom : along < n.X || along >= n.Right) continue;
            // gap > 0: spazio vuoto fra i due; gap < 0: si sovrappongono (es. dopo un cambio di risoluzione).
            var (gap, beyond) = edge switch
            {
                Edge.Right => (n.X - f.Right, n.Right > f.Right),
                Edge.Left => (f.X - n.Right, n.X < f.X),
                Edge.Bottom => (n.Y - f.Bottom, n.Bottom > f.Bottom),
                _ => (f.Y - n.Bottom, n.Y < f.Y),
            };
            if (gap > EdgeTolerance || !beyond) continue;
            if (Math.Abs(gap) < bestGap) { bestGap = Math.Abs(gap); best = n; }
        }
        if (best is null) return null;
        var r = sideways ? (along - best.Y) / best.Height : (along - best.X) / best.Width;
        return new LayoutHop(best.Id, Math.Clamp((float)r, 0f, 1f));
    }

    /// <summary>
    /// Porta il layout al formato attuale: le versioni precedenti salvavano una griglia (X,Y = colonna, riga) senza dimensioni.
    /// </summary>
    public void Normalize()
    {
        if (Entries.Count > 0 && Entries.All(e => e.Width <= 0 && e.Height <= 0))
        {
            foreach (var e in Entries)
            {
                e.X *= DefaultWidth;
                e.Y *= DefaultHeight;
            }
        }
        foreach (var e in Entries)
        {
            if (e.Width <= 0) e.Width = DefaultWidth;
            if (e.Height <= 0) e.Height = DefaultHeight;
        }
    }

    /// <summary>
    /// Cambia le dimensioni di un PC mantenendo la disposizione: chi stava interamente alla sua destra (o sotto)
    /// si sposta della stessa differenza, così i PC che lo toccavano continuano a toccarlo.
    /// </summary>
    public void Resize(LayoutEntry e, int width, int height)
    {
        int dw = width - e.Width, dh = height - e.Height;
        int oldRight = e.Right, oldBottom = e.Bottom;
        foreach (var n in Entries)
        {
            if (n == e) continue;
            if (dw != 0 && n.X >= oldRight - EdgeTolerance) n.X += dw;
            if (dh != 0 && n.Y >= oldBottom - EdgeTolerance) n.Y += dh;
        }
        e.Width = width;
        e.Height = height;
    }

    /// <summary>Sposta tutto perché l'angolo in alto a sinistra sia (0,0): non cambia chi tocca chi.</summary>
    public void ToOrigin()
    {
        if (Entries.Count == 0) return;
        int minX = Entries.Min(e => e.X), minY = Entries.Min(e => e.Y);
        foreach (var e in Entries) { e.X -= minX; e.Y -= minY; }
    }

    public Layout Clone() => new()
    {
        Version = Version,
        Author = Author,
        Entries = Entries.Select(e => e.Clone()).ToList(),
    };
}

/// <summary>Aggancio dei rettangoli mentre l'utente li trascina (come i monitor in Impostazioni → Schermo).</summary>
public static class LayoutSnap
{
    /// <summary>Lunghezza minima (pixel virtuali) del tratto di bordo in comune, perché il mouse possa passare.</summary>
    public const int MinShared = 120;

    /// <summary>
    /// La posizione più vicina a (<paramref name="x"/>,<paramref name="y"/>) in cui un rettangolo <paramref name="w"/>×<paramref name="h"/>
    /// tocca almeno uno degli altri lungo un bordo senza sovrapporsi a nessuno. I bordi vicini entro
    /// <paramref name="align"/> si allineano (stessa altezza in alto o in basso, stessa posizione a sinistra o a destra).
    /// </summary>
    public static (int X, int Y) Snap(IReadOnlyList<Rect> others, int w, int h, int x, int y, int align)
    {
        if (others.Count == 0) return (x, y);
        (int X, int Y)? best = null;
        long bestDist = long.MaxValue;

        foreach (var o in others)
        {
            // accanto (a sinistra o a destra)
            var mY = Math.Max(1, Math.Min(MinShared, Math.Min(h, o.Height) / 2));
            var cy = Math.Clamp(y, o.Top - h + mY, o.Bottom - mY);
            cy = Align(cy, h, o.Top, o.Bottom, align);
            Consider(o.Left - w, cy);
            Consider(o.Right, cy);

            // sopra o sotto
            var mX = Math.Max(1, Math.Min(MinShared, Math.Min(w, o.Width) / 2));
            var cx = Math.Clamp(x, o.Left - w + mX, o.Right - mX);
            cx = Align(cx, w, o.Left, o.Right, align);
            Consider(cx, o.Top - h);
            Consider(cx, o.Bottom);
        }
        return best ?? (x, y);

        void Consider(int cx, int cy)
        {
            var r = new Rect(cx, cy, cx + w, cy + h);
            foreach (var o in others)
                if (r.Left < o.Right && o.Left < r.Right && r.Top < o.Bottom && o.Top < r.Bottom) return;
            long dx = cx - x, dy = cy - y;
            var d = dx * dx + dy * dy;
            if (d < bestDist) { bestDist = d; best = (cx, cy); }
        }
    }

    private static int Align(int pos, int size, int start, int end, int align)
    {
        if (Math.Abs(pos - start) <= align) return start;
        if (Math.Abs(pos + size - end) <= align) return end - size;
        return pos;
    }
}

/// <summary>Mantiene il layout coerente fra i PC: unione dei membri, dimensioni aggiornate, versioni crescenti.</summary>
public sealed class LayoutManager
{
    private readonly object _gate = new();
    private readonly Guid _self;
    private readonly string _selfName;
    private (int W, int H)? _selfSize;
    private Layout _current;

    /// <summary>Il layout è cambiato: va salvato e inviato agli altri.</summary>
    public event Action<Layout>? Changed;

    public LayoutManager(Guid self, string selfName, Layout initial)
    {
        _self = self;
        _selfName = selfName;
        _current = initial.Clone();
        _current.Normalize();
        EnsureMembers([]);
    }

    public Layout Current { get { lock (_gate) return _current.Clone(); } }

    /// <summary>Vedi <see cref="Layout.Cross"/>.</summary>
    public LayoutHop? Cross(Guid from, Edge edge, float ratio)
    {
        lock (_gate) return _current.Cross(from, edge, ratio);
    }

    /// <summary>Applica un layout ricevuto se più recente. Restituisce true se è stato adottato.</summary>
    public bool Merge(Layout incoming, IEnumerable<(Guid Id, string Name)> connected)
    {
        bool adopted;
        lock (_gate)
        {
            adopted = incoming.IsNewerThan(_current);
            if (adopted)
            {
                _current = incoming.Clone();
                _current.Normalize();
            }
        }
        // Se il layout adottato non contiene me o i PC connessi (o ha la mia dimensione sbagliata) lo correggo e riparte la propagazione.
        var bumped = EnsureMembers(connected);
        if (adopted && !bumped) Raise();
        return adopted;
    }

    /// <summary>
    /// Aggiunge a destra i PC mancanti e aggiorna la dimensione di questo PC. Restituisce true se ha modificato il layout.
    /// </summary>
    public bool EnsureMembers(IEnumerable<(Guid Id, string Name)> members)
    {
        bool changed = false;
        lock (_gate)
        {
            foreach (var (id, name) in members.Prepend((_self, _selfName)))
            {
                if (_current.Find(id) is not null) continue;
                var (w, h) = id == _self && _selfSize is { } s ? s : (Layout.DefaultWidth, Layout.DefaultHeight);
                var last = _current.Entries.MaxBy(e => e.Right);
                _current.Entries.Add(new LayoutEntry { Id = id, Name = name, X = last?.Right ?? 0, Y = last?.Y ?? 0, Width = w, Height = h });
                changed = true;
            }
            if (_selfSize is { } size && _current.Find(_self) is { } me && (me.Width != size.W || me.Height != size.H))
            {
                _current.Resize(me, size.W, size.H);
                changed = true;
            }
            if (changed) Bump();
        }
        if (changed) Raise();
        return changed;
    }

    /// <summary>Dimensione attuale del desktop di questo PC (tutti i monitor). Se è cambiata il layout viene aggiornato e propagato.</summary>
    public void SetSelfSize(int width, int height)
    {
        if (width <= 0 || height <= 0) return;
        lock (_gate)
        {
            if (_selfSize == (width, height)) return;
            _selfSize = (width, height);
        }
        EnsureMembers([]);
    }

    /// <summary>Riparte da zero (es. ingresso in un altro gruppo): la versione 0 perde contro qualsiasi layout del gruppo.</summary>
    public void Reset()
    {
        lock (_gate) _current = new Layout();
        EnsureMembers([]);
        lock (_gate) { _current.Version = 0; _current.Author = Guid.Empty; }
        Raise();
    }

    /// <summary>Scelta dell'utente: nuove posizioni (pixel virtuali, angolo in alto a sinistra) dei PC indicati.</summary>
    public void SetPositions(IEnumerable<(Guid Id, int X, int Y)> positions)
    {
        lock (_gate)
        {
            foreach (var (id, x, y) in positions)
            {
                if (_current.Find(id) is not { } e) continue;
                e.X = x;
                e.Y = y;
            }
            _current.ToOrigin();
            Bump();
        }
        Raise();
    }

    /// <summary>PC in fila da sinistra a destra, allineati in alto.</summary>
    public void SetRow(IReadOnlyList<(Guid Id, string Name)> order)
    {
        lock (_gate)
        {
            var old = _current.Entries.ToDictionary(e => e.Id);
            var row = order.Append((_self, _selfName)).DistinctBy(p => p.Item1).ToList();
            var x = 0;
            _current.Entries = row.Select(p =>
            {
                var size = old.TryGetValue(p.Item1, out var prev) ? (prev.Width, prev.Height) : (Layout.DefaultWidth, Layout.DefaultHeight);
                var e = new LayoutEntry { Id = p.Item1, Name = p.Item2, X = x, Y = 0, Width = size.Item1, Height = size.Item2 };
                x += e.Width;
                return e;
            }).ToList();
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
