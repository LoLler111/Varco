using Varco.Core.Protocol;

namespace Varco.Core.Input;

/// <summary>
/// Quando il bordo dello schermo fa passare all'altro PC. Toccare il bordo basta, salvo:
/// <list type="bullet">
/// <item><see cref="CornerGuard"/>: negli angoli di ogni monitor non si passa (la X di una finestra massimizzata, il pulsante Start);</item>
/// <item><see cref="DelayMs"/> &gt; 0: si passa solo dopo essere rimasti contro il bordo per quel tempo;</item>
/// <item><see cref="BlockFullscreen"/>: non si passa mentre un'app a schermo intero (gioco, presentazione) è in primo piano su quel monitor;</item>
/// <item><see cref="ScrollLockHolds"/>: con Bloc Scorr acceso il cursore resta sul PC dove si trova.</item>
/// </list>
/// Ogni PC applica le proprie impostazioni ai propri bordi.
/// </summary>
public sealed record EdgeSettings(bool CornerGuard = true, int DelayMs = 0, bool BlockFullscreen = true, bool ScrollLockHolds = true)
{
    public static readonly EdgeSettings Default = new();
    /// <summary>Sosta sul bordo quando l'opzione è attiva.</summary>
    public const int StandardDelayMs = 300;
}

public static class EdgeGuard
{
    /// <summary>Lato degli angoli "morti": 4% dell'altezza del monitor (43 px a 1080p, 86 px a 4K, dove la scala è più alta).</summary>
    public static int CornerSize(Rect monitor) => Math.Max(24, monitor.Height * 4 / 100);

    /// <summary>Il punto del bordo è vicino a un angolo del suo monitor?</summary>
    public static bool InCorner(IReadOnlyList<Rect> monitors, Edge edge, int x, int y)
    {
        if (monitors.Count == 0) return false;
        var m = ScreenGeometry.MonitorAt(monitors, x, y);
        var size = CornerSize(m);
        return edge is Edge.Left or Edge.Right
            ? y - m.Top < size || m.Bottom - 1 - y < size
            : x - m.Left < size || m.Right - 1 - x < size;
    }
}
