using Varco.Core.Net;
using Varco.Core.Protocol;

namespace Varco.Core.Input;

/// <summary>
/// Accesso a mouse/tastiera del sistema. I metodi Inject*/Get*/Set* vanno chiamati solo sul thread di input
/// (quello agganciato al desktop attivo): dal resto del codice si passa da <see cref="Post"/>.
/// </summary>
public interface IInputPlatform
{
    /// <summary>Esegue l'azione sul thread di input, in modo asincrono.</summary>
    void Post(Action action);
    /// <summary>Monitor in pixel fisici; il primo è il principale.</summary>
    IReadOnlyList<Rect> GetMonitors();
    (int X, int Y) GetCursorPos();
    void SetCursorPos(int x, int y);
    void InjectMoveTo(int x, int y);
    void InjectButton(MouseButton button, bool down);
    void InjectWheel(int delta, bool horizontal);
    void InjectKey(ushort vk, ushort scan, KeyFlags flags);
}

public enum InputMode { Local, Controlling, Controlled }

/// <summary>
/// Macchina a stati dell'input:
///  • Local: mouse e tastiera lavorano qui; se il cursore tocca un bordo con un PC vicino connesso → Controlling.
///  • Controlling: l'input fisico di questo PC viene bloccato e inviato al target.
///  • Controlled: un altro PC ci comanda; iniettiamo il suo input. Se qualcuno tocca il nostro mouse/tastiera → Local.
/// Su ogni disconnessione si torna a Local e si rilasciano tutti i tasti premuti (niente tasti "incastrati").
/// </summary>
public sealed class InputCoordinator
{
    private const ushort VK_F12 = 0x7B;
    private static readonly ushort[] CtrlKeys = [0x11, 0xA2, 0xA3];
    private static readonly ushort[] AltKeys = [0x12, 0xA4, 0xA5];

    private readonly object _gate = new();
    private readonly IPeerNetwork _net;
    private readonly IInputPlatform _platform;
    private readonly LayoutManager _layout;

    private InputMode _mode = InputMode.Local;
    private Guid _peer; // target se Controlling, controller se Controlled

    // lato controller
    private int _parkX, _parkY;
    private int _returnX, _returnY;
    private int _localButtons;
    private readonly HashSet<ushort> _localDownKeys = new();
    private readonly HashSet<ushort> _remoteDownKeys = new();

    // lato target
    private readonly Dictionary<ushort, KeyMsg> _injectedKeys = new();
    private int _injectedButtons;
    private Edge? _latchedEdge;
    // Posizione "virtuale" del cursore iniettato: GetCursorPos subito dopo SendInput potrebbe non essere ancora aggiornato.
    // Usata solo sul thread di input.
    private int _vx, _vy;
    private bool _vValid;

    public InputCoordinator(IPeerNetwork net, IInputPlatform platform, LayoutManager layout)
    {
        _net = net;
        _platform = platform;
        _layout = layout;
        SyncDesktopSize();
    }

    /// <summary>Comunica al layout la dimensione attuale del desktop (cambia se si cambia risoluzione o si collega un monitor).</summary>
    public void SyncDesktopSize()
    {
        var monitors = _platform.GetMonitors();
        if (monitors.Count == 0) return;
        var b = ScreenGeometry.Bounds(monitors);
        _layout.SetSelfSize(b.Width, b.Height);
    }

    public InputMode Mode { get { lock (_gate) return _mode; } }
    public Guid? ActivePeer { get { lock (_gate) return _mode == InputMode.Local ? null : _peer; } }

    public event Action<InputMode, Guid?>? ModeChanged;

    private void SetMode(InputMode mode, Guid peer)
    {
        _mode = mode;
        _peer = peer;
        var p = mode == InputMode.Local ? (Guid?)null : peer;
        Log.Info($"Modalità input: {mode}{(p is null ? "" : $" ({p})")}");
        try { ModeChanged?.Invoke(mode, p); } catch { }
    }

    // ═══════════════════════ eventi dagli hook (thread di input) ═══════════════════════
    // Restituiscono true se l'evento va bloccato localmente.

    public bool OnLocalMouseMove(int x, int y)
    {
        lock (_gate)
        {
            switch (_mode)
            {
                case InputMode.Local:
                    if (_localButtons != 0) return false; // niente passaggio durante un trascinamento
                    var monitors = _platform.GetMonitors();
                    if (ScreenGeometry.DetectEdge(monitors, x, y) is not Edge edge) return false;
                    // Chi c'è oltre il bordo proprio in questo punto? (disposizione 2D: dipende anche dall'altezza)
                    if (_layout.Cross(_net.SelfId, edge, ScreenGeometry.Ratio(monitors, edge, x, y)) is not { } hop
                        || hop.Id == _net.SelfId || !_net.IsConnected(hop.Id)) return false;
                    StartControlling(hop.Id, edge, hop.Ratio, x, y, monitors);
                    return true;

                case InputMode.Controlling:
                    int dx = x - _parkX, dy = y - _parkY;
                    if (dx != 0 || dy != 0) _net.Send(_peer, new MouseMove(dx, dy));
                    return true;

                default:
                    TakeOverLocally();
                    return false;
            }
        }
    }

    public bool OnLocalButton(MouseButton button, bool down)
    {
        lock (_gate)
        {
            switch (_mode)
            {
                case InputMode.Local:
                    if (down) _localButtons |= 1 << (int)button; else _localButtons &= ~(1 << (int)button);
                    return false;
                case InputMode.Controlling:
                    _net.Send(_peer, new MouseButtonMsg(button, down));
                    return true;
                default:
                    TakeOverLocally();
                    return false;
            }
        }
    }

    public bool OnLocalWheel(int delta, bool horizontal)
    {
        lock (_gate)
        {
            switch (_mode)
            {
                case InputMode.Local: return false;
                case InputMode.Controlling:
                    _net.Send(_peer, new MouseWheel(delta, horizontal));
                    return true;
                default:
                    TakeOverLocally();
                    return false;
            }
        }
    }

    public bool OnLocalKey(ushort vk, ushort scan, KeyFlags flags)
    {
        var up = flags.HasFlag(KeyFlags.Up);
        lock (_gate)
        {
            switch (_mode)
            {
                case InputMode.Local:
                    if (!up) { _localDownKeys.Add(vk); return false; }
                    if (_remoteDownKeys.Remove(vk)) return true; // premuto sul remoto: il PC locale non l'ha mai visto giù
                    _localDownKeys.Remove(vk);
                    return false;

                case InputMode.Controlling:
                    // Tasto premuto prima di attraversare: ripetizioni bloccate, rilascio lasciato al PC locale.
                    if (_localDownKeys.Contains(vk))
                    {
                        if (!up) return true;
                        _localDownKeys.Remove(vk);
                        return false;
                    }
                    // Via di fuga: Ctrl+Alt+F12 riporta sempre il controllo qui.
                    if (!up && vk == VK_F12 && CtrlKeys.Any(_remoteDownKeys.Contains) && AltKeys.Any(_remoteDownKeys.Contains))
                    {
                        _net.Send(_peer, new Leave());
                        ReturnLocal(_returnX, _returnY);
                        _remoteDownKeys.Add(vk);
                        return true;
                    }
                    _net.Send(_peer, new KeyMsg(vk, scan, flags));
                    if (up) _remoteDownKeys.Remove(vk); else _remoteDownKeys.Add(vk);
                    return true;

                default:
                    TakeOverLocally();
                    return false;
            }
        }
    }

    // ═══════════════════════ eventi dalla rete ═══════════════════════

    public void OnPeerConnected(Guid peer, string name)
    {
        _layout.EnsureMembers([(peer, name)]);
        _net.Send(peer, new LayoutMsg(_layout.Current));
    }

    public void OnPeerDisconnected(Guid peer)
    {
        lock (_gate)
        {
            if (_mode == InputMode.Controlling && _peer == peer)
            {
                Log.Info("Il PC controllato si è disconnesso: torno al locale");
                ReturnLocal(_returnX, _returnY);
            }
            else if (_mode == InputMode.Controlled && _peer == peer)
            {
                Log.Info("Il PC che mi controllava si è disconnesso: rilascio i tasti");
                SetMode(InputMode.Local, Guid.Empty);
                ReleaseInjected();
            }
        }
    }

    public void OnMessage(Guid from, Message msg, Func<IEnumerable<(Guid, string)>> connectedPeers)
    {
        if (msg is LayoutMsg lm)
        {
            _layout.Merge(lm.Layout, connectedPeers());
            return;
        }

        lock (_gate)
        {
            switch (msg)
            {
                case Enter e: HandleEnter(from, e); break;
                case Leave when _mode == InputMode.Controlled && _peer == from:
                    SetMode(InputMode.Local, Guid.Empty);
                    ReleaseInjected();
                    break;
                case EdgeHit h when _mode == InputMode.Controlling && _peer == from: HandleEdgeHit(from, h); break;
                case TakeOver when _mode == InputMode.Controlling && _peer == from:
                    ReturnLocal(_returnX, _returnY);
                    break;

                case MouseMove m when IsControlledBy(from): InjectMove(m.Dx, m.Dy); break;
                case MouseButtonMsg b when IsControlledBy(from):
                    if (b.Down) _injectedButtons |= 1 << (int)b.Button; else _injectedButtons &= ~(1 << (int)b.Button);
                    _platform.Post(() => _platform.InjectButton(b.Button, b.Down));
                    break;
                case MouseWheel w when IsControlledBy(from):
                    _platform.Post(() => _platform.InjectWheel(w.Delta, w.Horizontal));
                    break;
                case KeyMsg k when IsControlledBy(from):
                    if (k.Flags.HasFlag(KeyFlags.Up)) _injectedKeys.Remove(k.Vk); else _injectedKeys[k.Vk] = k;
                    _platform.Post(() => _platform.InjectKey(k.Vk, k.Scan, k.Flags));
                    break;
            }
        }
    }

    // ═══════════════════════ logica interna (chiamata sotto lock) ═══════════════════════

    private bool IsControlledBy(Guid peer) => _mode == InputMode.Controlled && _peer == peer;

    private void StartControlling(Guid target, Edge edge, float ratio, int x, int y, IReadOnlyList<Rect> monitors)
    {
        _returnX = x;
        _returnY = y;
        (_parkX, _parkY) = monitors.Count > 0 ? monitors[0].Center : (x, y);
        _platform.SetCursorPos(_parkX, _parkY); // siamo già sul thread di input
        _remoteDownKeys.Clear();
        SetMode(InputMode.Controlling, target);
        _net.Send(target, new Enter(ScreenGeometry.Opposite(edge), ratio));
    }

    private void ReturnLocal(int x, int y)
    {
        SetMode(InputMode.Local, Guid.Empty);
        _platform.Post(() => _platform.SetCursorPos(x, y));
    }

    private void HandleEnter(Guid from, Enter e)
    {
        if (_mode == InputMode.Controlling)
        {
            // L'utente sta usando fisicamente l'altro PC: smetto di comandare il mio target.
            _net.Send(_peer, new Leave());
        }
        else if (_mode == InputMode.Controlled && _peer != from)
        {
            _net.Send(_peer, new TakeOver());
            ReleaseInjected();
        }
        _latchedEdge = null;
        SetMode(InputMode.Controlled, from);
        _platform.Post(() =>
        {
            var (x, y) = ScreenGeometry.EntryPoint(_platform.GetMonitors(), e.From, e.Ratio);
            _platform.InjectMoveTo(x, y);
            (_vx, _vy, _vValid) = (x, y, true);
        });
    }

    private void HandleEdgeHit(Guid from, EdgeHit h)
    {
        // Il punto d'uscita sul bordo del target viene riportato sulla disposizione 2D: ne ricavo chi c'è accanto
        // in quel punto e dove entrare lungo il suo bordo. Nessuno accanto → il cursore resta sul target.
        if (_layout.Cross(from, h.Edge, h.Ratio) is not { } hop) return;
        var entry = ScreenGeometry.Opposite(h.Edge);
        if (hop.Id == _net.SelfId)
        {
            _net.Send(from, new Leave());
            var (x, y) = ScreenGeometry.EntryPoint(_platform.GetMonitors(), entry, hop.Ratio);
            ReturnLocal(x, y);
        }
        else if (_net.IsConnected(hop.Id))
        {
            _net.Send(from, new Leave());
            SetMode(InputMode.Controlling, hop.Id);
            _net.Send(hop.Id, new Enter(entry, hop.Ratio));
        }
    }

    private void InjectMove(int dx, int dy)
    {
        var controller = _peer;
        _platform.Post(() =>
        {
            var monitors = _platform.GetMonitors();
            if (!_vValid) { (_vx, _vy) = _platform.GetCursorPos(); _vValid = true; }
            var (cx, cy) = (_vx, _vy);
            var (nx, ny, hit) = ScreenGeometry.MoveBy(monitors, cx, cy, dx, dy);
            if (nx != cx || ny != cy) _platform.InjectMoveTo(nx, ny);
            (_vx, _vy) = (nx, ny);

            lock (_gate)
            {
                if (!IsControlledBy(controller)) return;
                if (hit is not Edge edge) { _latchedEdge = null; return; }
                // Un solo EdgeHit per "spinta" e mai durante un trascinamento.
                if (_latchedEdge == edge || _injectedButtons != 0) return;
                // Se in questo punto del bordo non c'è nessun PC accanto il cursore resta qui; scorrendo lungo
                // il bordo fino a dove un altro PC lo tocca, l'EdgeHit parte (il bordo non resta "agganciato").
                var ratio = ScreenGeometry.Ratio(monitors, edge, nx, ny);
                if (_layout.Cross(_net.SelfId, edge, ratio) is null) return;
                _latchedEdge = edge;
                _net.Send(controller, new EdgeHit(edge, ratio));
            }
        });
    }

    private void TakeOverLocally()
    {
        var controller = _peer;
        Log.Info("Input fisico sul PC controllato: riprendo il controllo locale");
        SetMode(InputMode.Local, Guid.Empty);
        _net.Send(controller, new TakeOver());
        ReleaseInjected();
    }

    private void ReleaseInjected()
    {
        var keys = _injectedKeys.Values.ToList();
        var buttons = _injectedButtons;
        _injectedKeys.Clear();
        _injectedButtons = 0;
        _latchedEdge = null;
        _platform.Post(() =>
        {
            _vValid = false;
            foreach (var k in keys) _platform.InjectKey(k.Vk, k.Scan, k.Flags | KeyFlags.Up);
            for (int b = 0; b <= (int)MouseButton.X2; b++)
                if ((buttons & (1 << b)) != 0) _platform.InjectButton((MouseButton)b, false);
        });
    }
}
