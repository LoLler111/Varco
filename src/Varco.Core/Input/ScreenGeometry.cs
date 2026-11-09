using Varco.Core.Protocol;

namespace Varco.Core.Input;

/// <summary>Rettangolo in pixel fisici; Right e Bottom sono esclusivi.</summary>
public readonly record struct Rect(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool Contains(int x, int y) => x >= Left && x < Right && y >= Top && y < Bottom;
    public (int X, int Y) Center => (Left + Width / 2, Top + Height / 2);
}

/// <summary>Geometria degli schermi (multi-monitor) indipendente dalla piattaforma.</summary>
public static class ScreenGeometry
{
    /// <summary>Di quanti pixel il cursore entra dal bordo (per non ritoccare subito il bordo appena attraversato).</summary>
    public const int EntryInset = 2;

    public static Edge Opposite(Edge e) => e switch
    {
        Edge.Left => Edge.Right,
        Edge.Right => Edge.Left,
        Edge.Top => Edge.Bottom,
        _ => Edge.Top,
    };

    public static Rect Bounds(IReadOnlyList<Rect> monitors)
    {
        if (monitors.Count == 0) return new Rect(0, 0, 1, 1);
        return new Rect(monitors.Min(m => m.Left), monitors.Min(m => m.Top),
                        monitors.Max(m => m.Right), monitors.Max(m => m.Bottom));
    }

    private static bool AnyContains(IReadOnlyList<Rect> monitors, int x, int y)
    {
        foreach (var m in monitors) if (m.Contains(x, y)) return true;
        return false;
    }

    private static Rect MonitorAt(IReadOnlyList<Rect> monitors, int x, int y)
    {
        Rect best = monitors[0];
        long bestDist = long.MaxValue;
        foreach (var m in monitors)
        {
            if (m.Contains(x, y)) return m;
            long dx = x < m.Left ? m.Left - x : x >= m.Right ? x - m.Right + 1 : 0;
            long dy = y < m.Top ? m.Top - y : y >= m.Bottom ? y - m.Bottom + 1 : 0;
            var d = dx * dx + dy * dy;
            if (d < bestDist) { bestDist = d; best = m; }
        }
        return best;
    }

    /// <summary>Il punto è sul bordo esterno del desktop (nessun monitor oltre)? Usato dal PC che controlla.</summary>
    public static Edge? DetectEdge(IReadOnlyList<Rect> monitors, int x, int y)
    {
        if (monitors.Count == 0) return null;
        var m = MonitorAt(monitors, x, y);
        var cy = Math.Clamp(y, m.Top, m.Bottom - 1);
        var cx = Math.Clamp(x, m.Left, m.Right - 1);
        if (x >= m.Right - 1 && !AnyContains(monitors, m.Right, cy)) return Edge.Right;
        if (x <= m.Left && !AnyContains(monitors, m.Left - 1, cy)) return Edge.Left;
        if (y >= m.Bottom - 1 && !AnyContains(monitors, cx, m.Bottom)) return Edge.Bottom;
        if (y <= m.Top && !AnyContains(monitors, cx, m.Top - 1)) return Edge.Top;
        return null;
    }

    /// <summary>
    /// Posizione lungo il bordo, 0..1 rispetto all'intero desktop (centro del pixel: y = Top → 0.5/Height).
    /// <see cref="EntryPoint"/> fa la conversione inversa, quindi un rapporto torna esattamente allo stesso pixel.
    /// </summary>
    public static float Ratio(IReadOnlyList<Rect> monitors, Edge edge, int x, int y)
    {
        var b = Bounds(monitors);
        float r = edge is Edge.Left or Edge.Right
            ? (y - b.Top + 0.5f) / Math.Max(1, b.Height)
            : (x - b.Left + 0.5f) / Math.Max(1, b.Width);
        return Math.Clamp(r, 0f, 1f);
    }

    /// <summary>Dove far apparire il cursore entrando dal bordo <paramref name="from"/>.</summary>
    public static (int X, int Y) EntryPoint(IReadOnlyList<Rect> monitors, Edge from, float ratio)
    {
        if (monitors.Count == 0) return (0, 0);
        var b = Bounds(monitors);
        ratio = Math.Clamp(ratio, 0f, 1f);
        if (from is Edge.Left or Edge.Right)
        {
            var y = b.Top + Math.Min(b.Height - 1, (int)(ratio * b.Height));
            var spanning = monitors.Where(m => y >= m.Top && y < m.Bottom).ToList();
            if (spanning.Count == 0) spanning = [MonitorAt(monitors, from == Edge.Left ? b.Left : b.Right - 1, y)];
            var m = from == Edge.Left ? spanning.MinBy(s => s.Left) : spanning.MaxBy(s => s.Right);
            y = Math.Clamp(y, m.Top, m.Bottom - 1);
            var x = from == Edge.Left ? m.Left + EntryInset : m.Right - 1 - EntryInset;
            return (x, y);
        }
        else
        {
            var x = b.Left + Math.Min(b.Width - 1, (int)(ratio * b.Width));
            var spanning = monitors.Where(m => x >= m.Left && x < m.Right).ToList();
            if (spanning.Count == 0) spanning = [MonitorAt(monitors, x, from == Edge.Top ? b.Top : b.Bottom - 1)];
            var m = from == Edge.Top ? spanning.MinBy(s => s.Top) : spanning.MaxBy(s => s.Bottom);
            x = Math.Clamp(x, m.Left, m.Right - 1);
            var y = from == Edge.Top ? m.Top + EntryInset : m.Bottom - 1 - EntryInset;
            return (x, y);
        }
    }

    /// <summary>
    /// Sposta il cursore remoto di (dx,dy) rispettando i monitor.
    /// Se lo spostamento esce dal desktop restituisce il bordo toccato (il cursore resta bloccato sul bordo).
    /// </summary>
    public static (int X, int Y, Edge? Hit) MoveBy(IReadOnlyList<Rect> monitors, int x, int y, int dx, int dy)
    {
        if (monitors.Count == 0) return (x + dx, y + dy, null);
        int nx = x + dx, ny = y + dy;
        if (AnyContains(monitors, nx, ny)) return (nx, ny, null);

        var m = MonitorAt(monitors, x, y);
        int cx = Math.Clamp(nx, m.Left, m.Right - 1);
        int cy = Math.Clamp(ny, m.Top, m.Bottom - 1);

        // Ogni asse separatamente: esce dal monitor e oltre non c'è un altro monitor → bordo.
        Edge? hEdge = null, vEdge = null;
        if ((nx >= m.Right || nx < m.Left) && !AnyContains(monitors, nx, cy))
            hEdge = nx >= m.Right ? Edge.Right : Edge.Left;
        if ((ny >= m.Bottom || ny < m.Top) && !AnyContains(monitors, cx, ny))
            vEdge = ny >= m.Bottom ? Edge.Bottom : Edge.Top;

        int fx = hEdge is null ? nx : cx;
        int fy = vEdge is null ? ny : cy;
        if (!AnyContains(monitors, fx, fy)) (fx, fy) = (cx, cy); // angolo scoperto fra monitor diversi
        return (fx, fy, hEdge ?? vEdge);
    }
}
