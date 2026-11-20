using System.Diagnostics;
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
    /// <summary>
    /// Inizio del controllo di un altro PC: il cursore va parcheggiato in (x, y). Restituisce dove sta adesso.
    /// Se non è (x, y), il cursore resta lì finché la piattaforma non chiama <see cref="InputCoordinator.CompletePark"/>
    /// (Windows: prima lo nasconde dove si trova, vedi WindowsInput.ParkCursor).
    /// </summary>
    (int X, int Y) ParkCursor(int x, int y)
    {
        SetCursorPos(x, y);
        return (x, y);
    }
    void InjectMoveTo(int x, int y);
    void InjectButton(MouseButton button, bool down);
    void InjectWheel(int delta, bool horizontal);
    void InjectKey(ushort vk, ushort scan, KeyFlags flags);
    /// <summary>Stato attuale di Bloc Maiusc/Num/Scorr; null se la piattaforma non lo conosce.</summary>
    LockKeys? GetLockKeys() => null;
    /// <summary>Porta Bloc Maiusc/Num/Scorr allo stato indicato, premendo solo i tasti che sono diversi.</summary>
    void SetLockKeys(LockKeys state) { }
    /// <summary>Esegue Ctrl+Alt+Canc su questo PC (chiesto dal PC che lo comanda).</summary>
    void SendSecureAttention() { }
    /// <summary>
    /// Il Ctrl+Alt+Canc vero premuto qui mentre si comanda un altro PC ha aperto la schermata di sicurezza su questo PC:
    /// chiudila (Esc), perché è andato al PC comandato.
    /// </summary>
    void CancelLocalSecureScreen() { }
    /// <summary>In primo piano, sul monitor che contiene (x, y), c'è un'app a schermo intero (gioco, presentazione)?</summary>
    bool IsFullscreenAppAt(int x, int y) => false;
    /// <summary>Annulla il trascinamento partito su questo PC (è passato a un altro PC), senza lasciar cadere niente.</summary>
    void CancelLocalDrag() { }
    /// <summary>
    /// Il PC che ci comandava è passato altrove: il cursore qui resta fermo dove l'ha lasciato. Lo nasconde finché
    /// qualcuno non muove il mouse di questo PC (o un altro PC torna a comandarlo).
    /// </summary>
    void HideIdleCursor() { }
    /// <summary>
    /// In questo momento si sta spostando o ridimensionando una finestra (Windows: il ciclo di spostamento, GUI_INMOVESIZE)?
    /// Chiamato solo dal thread di input.
    /// </summary>
    bool IsMovingWindow() => false;
}

public enum InputMode { Local, Controlling, Controlled }

/// <summary>
/// Trascinamento di file da un PC all'altro (implementato dall'host, con l'aiuto dell'icona che gira con l'utente).
/// I metodi vengono chiamati con il lock del coordinatore, anche dal thread degli hook: non devono bloccare.
/// </summary>
public interface IFileDrag
{
    /// <summary>Si possono trascinare file verso quel PC (versione, appunti condivisi attivi su tutti e due)?</summary>
    bool CanDragTo(Guid target);
    /// <summary>
    /// Pulsante sinistro premuto contro il bordo verso <paramref name="target"/>: scopri se si stanno trascinando dei file.
    /// La risposta arriva con <see cref="InputCoordinator.OnDragProbeResult"/>.
    /// </summary>
    void Probe(Guid target);
    /// <summary>Fine della prova (pulsante rilasciato o trascinamento passato all'altro PC).</summary>
    void EndProbe();
    /// <summary>Lato target: parte un trascinamento dal PC che comanda. Quando è pronto si chiama <see cref="InputCoordinator.OnDragSourceReady"/>.</summary>
    void Incoming(Guid from, byte[] offer);
    /// <summary>Lato target: trascinamento annullato (pulsante rilasciato prima di partire, controllo passato altrove).</summary>
    void CancelIncoming();
}

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
    private const ushort VK_END = 0x23;
    private static readonly ushort[] CtrlKeys = [0x11, 0xA2, 0xA3];
    private static readonly ushort[] AltKeys = [0x12, 0xA4, 0xA5];
    private static readonly ushort[] WinKeys = [0x5B, 0x5C];
    /// <summary>Ctrl+Alt+Canc vero: il desktop deve passare a Winlogon entro questo tempo dal rilascio di Ctrl+Alt (ms).</summary>
    private const int SecureAttentionWindowMs = 500;

    private readonly object _gate = new();
    private readonly IPeerNetwork _net;
    private readonly IInputPlatform _platform;
    private readonly LayoutManager _layout;

    private InputMode _mode = InputMode.Local;
    private Guid _peer; // target se Controlling, controller se Controlled

    // lato controller
    private int _parkX, _parkY;
    /// <summary>Dove sta davvero il cursore mentre si comanda: i movimenti si misurano da qui (vedi ParkCursor).</summary>
    private int _refX, _refY;
    private int _returnX, _returnY;
    // Ritorno al PC locale in corso: dove deve finire il cursore e quando è stato deciso (vedi ReturnLocal).
    private (int X, int Y)? _homing;
    private long _homingSince;
    private int _localButtons;
    private readonly HashSet<ushort> _localDownKeys = new();
    private readonly HashSet<ushort> _remoteDownKeys = new();
    /// <summary>Tasti usati come scorciatoia (Ctrl+Alt+Fine): il rilascio non va inviato a nessuno.</summary>
    private readonly HashSet<ushort> _swallowUp = new();
    // Ctrl+Alt (senza Win) premuti sul PC che comanda: fino a quando. long.MaxValue = ancora premuti.
    private long _ctrlAltUntil = long.MinValue / 2;
    private long _ctrlAltSince;
    // Ctrl premuti davvero (non il Ctrl finto che Windows aggiunge ad AltGr) e quel Ctrl finto, per non scambiare AltGr
    // (@, #, [ ] sulla tastiera italiana) per Ctrl+Alt.
    private readonly HashSet<ushort> _realCtrlDown = new();
    private bool _altGrCtrlDown;
    private long _lastLocalSecureAttention = long.MinValue / 2;
    /// <summary>Scancode e flag con cui sono stati premuti Ctrl e Alt: servono per rilasciarli sul PC comandato.</summary>
    private readonly Dictionary<ushort, (ushort Scan, KeyFlags Flags)> _modifierScan = new();
    // Movimenti raggruppati: un mouse da 1000 Hz farebbe 1000 pacchetti al secondo, che sul Wi-Fi arrivano a raffiche.
    private int _pendDx, _pendDy;
    private long _lastMoveSent = long.MinValue / 2;
    private readonly Timer _flushTimer;

    /// <summary>
    /// Intervallo minimo tra due movimenti inviati (ms). Gli spostamenti nel frattempo vengono sommati.
    /// 4 ms = 250 aggiornamenti al secondo: più della frequenza dei monitor fino a 240 Hz (con 8 ms il cursore sul target
    /// si muoveva a 125 Hz e un mouse da 125 Hz, con gli eventi a cavallo degli 8 ms, finiva a scatti di 16 ms), ma
    /// abbastanza pochi pacchetti da non intasare il Wi-Fi.
    /// </summary>
    public int MoveIntervalMs { get; init; } = 4;

    // Rotella raggruppata come i movimenti: una rotella a scorrimento libero o ad alta risoluzione manda centinaia di
    // eventi al secondo. Inviati e iniettati uno per uno, sul Wi-Fi arrivano a raffiche e il PC controllato riceve decine
    // di scatti tutti insieme: la coda di input si intasa e il cursore resta fermo finché lo scorrimento non è finito.
    private int _pendWheelV, _pendWheelH;
    private long _lastWheelSent = long.MinValue / 2;
    private long _flushDue = long.MaxValue;

    /// <summary>Intervallo minimo tra due invii della rotella (ms). Gli scatti nel frattempo vengono sommati.</summary>
    public int WheelIntervalMs { get; init; } = 16;

    /// <summary>Delta massimo in un solo evento: WM_MOUSEWHEEL lo porta in uno short. Multiplo di 120 (uno scatto).</summary>
    private const int MaxWheelDelta = 32760;

    // lato target
    private readonly Dictionary<ushort, KeyMsg> _injectedKeys = new();
    private int _injectedButtons;
    private Edge? _latchedEdge;
    // Posizione "virtuale" del cursore iniettato: GetCursorPos subito dopo SendInput potrebbe non essere ancora aggiornato.
    // Usata solo sul thread di input.
    private int _vx, _vy;
    private bool _vValid;
    // Scatti di rotella ricevuti e non ancora iniettati: quelli che arrivano mentre il precedente è ancora in coda
    // si sommano a lui invece di diventare un altro evento.
    private sealed class WheelSlot { public int Delta; public bool Horizontal; }
    private WheelSlot? _wheelSlot;
    // Spostamento di una finestra: posizione già decisa (_vx, _vy) ma non ancora iniettata, e quando è stato iniettato
    // l'ultimo movimento. Solo sul thread di input.
    private bool _movePending;
    private long _lastMoveInjected = long.MinValue / 2;
    private readonly Timer _moveFlushTimer;

    /// <summary>
    /// Mentre si sposta o ridimensiona una finestra, intervallo minimo fra due movimenti iniettati (ms); nel frattempo
    /// conta solo l'ultima posizione. Alcune app (WhatsApp, WinUI) spostano la finestra al massimo una volta per fotogramma
    /// e con il mouse iniettato non saltano all'ultima posizione: ripercorrono tutte quelle in coda, una per fotogramma.
    /// Con un movimento ogni 4 ms la coda cresceva e la finestra restava indietro di centinaia di millisecondi (misurato:
    /// a 4 e 8 ms resta indietro, da 12 ms in su segue il cursore al pixel). Il mouse fisico non ha questo problema.
    /// </summary>
    public int WindowMoveIntervalMs { get; init; } = 16;

    public InputCoordinator(IPeerNetwork net, IInputPlatform platform, LayoutManager layout)
    {
        _net = net;
        _platform = platform;
        _layout = layout;
        _flushTimer = new Timer(_ =>
        {
            lock (_gate)
            {
                _flushDue = long.MaxValue;
                FlushPending();
            }
        });
        _moveFlushTimer = new Timer(_ => _platform.Post(FlushThrottledMove));
        SyncDesktopSize();
    }

    /// <summary>Comunica al layout i monitor attuali (cambiano se si cambia risoluzione, si collega o si sposta un monitor).</summary>
    public void SyncDesktopSize()
    {
        var monitors = _platform.GetMonitors();
        if (monitors.Count == 0) return;
        _layout.SetSelfMonitors(monitors);
    }

    public InputMode Mode { get { lock (_gate) return _mode; } }
    public Guid? ActivePeer { get { lock (_gate) return _mode == InputMode.Local ? null : _peer; } }

    public event Action<InputMode, Guid?>? ModeChanged;

    /// <summary>Trascinamento di file fra i PC; null = non disponibile.</summary>
    public IFileDrag? FileDrag { get; set; }

    /// <summary>Regole dei bordi di questo PC (lette a ogni contatto col bordo: cambiano senza riavviare).</summary>
    public Func<EdgeSettings> EdgeSettings { get; set; } = () => Input.EdgeSettings.Default;
    // Da quando il cursore è contro il bordo (per la sosta): sul PC che comanda e su quello comandato.
    private (Edge Edge, long Since)? _edgeDwellLocal, _edgeDwellTarget;

    // Trascinamento, lato di chi trascina: PC su cui si sta provando, risultato pronto, prova già fallita con questo clic.
    private Guid? _dragProbing;
    private (Guid Target, byte[] Offer)? _dragReady;
    private bool _dragProbeDone;
    // Lato target: in attesa che l'icona prepari il trascinamento (i movimenti si accumulano), e trascinamento in corso.
    private bool _dragHold;
    private long _dragHoldSince;
    private int _heldDx, _heldDy;
    private bool _dragActive;
    /// <summary>Il trascinamento in corso è stato interrotto da un cambio di modalità: vedi <see cref="ReleaseInjected"/>.</summary>
    private bool _dragInterrupted;
    private const int DragHoldMaxMs = 3000;
    private const int LeftBit = 1 << (int)MouseButton.Left;

    private void SetMode(InputMode mode, Guid peer)
    {
        _mode = mode;
        _peer = peer;
        _homing = null;
        _edgeDwellLocal = _edgeDwellTarget = null;
        _pendDx = _pendDy = 0; // spostamenti e rotella non inviati: appartenevano al target precedente
        _pendWheelV = _pendWheelH = 0;
        if (_dragHold || _dragActive)
        {
            _dragInterrupted = _dragActive;
            _dragHold = _dragActive = false;
            try { FileDrag?.CancelIncoming(); } catch (Exception ex) { Log.Error("Trascinamento", ex); }
        }
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
                    if (_homing is { } home)
                    {
                        _homing = null;
                        // Primo movimento dopo il ritorno: la sua posizione può essere calcolata dal cursore ancora
                        // parcheggiato al centro. Lo scarto e rimetto il cursore sul bordo da qui, dal thread degli hook:
                        // Windows sta aspettando questa risposta, quindi nessun evento vecchio può più sovrascriverlo.
                        if (ElapsedMs(_homingSince) < HomingWindowMs)
                        {
                            _platform.SetCursorPos(home.X, home.Y);
                            return true;
                        }
                    }
                    // Durante un trascinamento non si passa, a meno che si stiano trascinando dei file (vedi DragAcross).
                    if (_localButtons != 0) return _localButtons == LeftBit && DragAcross(x, y);
                    var monitors = _platform.GetMonitors();
                    if (ScreenGeometry.DetectEdge(monitors, x, y) is not Edge edge) { _edgeDwellLocal = null; return false; }
                    // Chi c'è oltre il bordo proprio in questo punto? (disposizione 2D: dipende anche dall'altezza)
                    if (_layout.Cross(_net.SelfId, edge, ScreenGeometry.Ratio(monitors, edge, x, y)) is not { } hop
                        || hop.Id == _net.SelfId || !_net.IsConnected(hop.Id)) return false;
                    if (!EdgeAllowed(monitors, edge, x, y, ref _edgeDwellLocal)) return false;
                    StartControlling(hop.Id, edge, hop.Ratio, x, y, monitors);
                    return true;

                case InputMode.Controlling:
                    _pendDx += x - _refX;
                    _pendDy += y - _refY;
                    var waited = ElapsedMs(_lastMoveSent);
                    if (waited >= MoveIntervalMs) FlushMoves();
                    else ScheduleFlush(MoveIntervalMs - waited); // invia la coda se il mouse si ferma
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
                    if (button == MouseButton.Left && !down) EndDragProbe();
                    return false;
                case InputMode.Controlling:
                    FlushPending(); // il clic deve arrivare dopo lo spostamento (e lo scorrimento) che lo precede
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
                    if (horizontal) _pendWheelH += delta; else _pendWheelV += delta;
                    var waited = ElapsedMs(_lastWheelSent);
                    if (waited >= WheelIntervalMs) FlushPending();
                    else ScheduleFlush(WheelIntervalMs - waited);
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
                    if (up && _swallowUp.Remove(vk)) return true;
                    // Ctrl+Alt+Fine = Ctrl+Alt+Canc sul PC comandato (come in Desktop remoto): quello vero agisce sempre qui.
                    if (!up && vk == VK_END && CtrlKeys.Any(_remoteDownKeys.Contains) && AltKeys.Any(_remoteDownKeys.Contains))
                    {
                        if (_swallowUp.Add(vk)) // le ripetizioni del tasto tenuto premuto non contano
                        {
                            Log.Info("Ctrl+Alt+Fine: chiedo Ctrl+Alt+Canc al PC comandato");
                            FlushPending();
                            _net.Send(_peer, new SecureAttention());
                        }
                        return true;
                    }
                    // Via di fuga: Ctrl+Alt+F12 riporta sempre il controllo qui.
                    if (!up && vk == VK_F12 && CtrlKeys.Any(_remoteDownKeys.Contains) && AltKeys.Any(_remoteDownKeys.Contains))
                    {
                        _net.Send(_peer, new Leave());
                        ReturnLocal(_returnX, _returnY);
                        _remoteDownKeys.Add(vk);
                        return true;
                    }
                    FlushPending();
                    _net.Send(_peer, new KeyMsg(vk, scan, flags));
                    // Bloc Maiusc/Num/Scorr cambiano su tutti e due i PC: il tasto passa anche al PC locale, così la spia
                    // della tastiera resta giusta e al ritorno gli stati coincidono. Non va in _remoteDownKeys: il PC
                    // locale lo vede premere e deve vederlo anche rilasciare, pure se nel frattempo il controllo è tornato qui.
                    if (KeyCodes.LockKeyOf(vk) is not null) return false;
                    if (up) _remoteDownKeys.Remove(vk); else _remoteDownKeys.Add(vk);
                    if (CtrlKeys.Contains(vk) || AltKeys.Contains(vk)) _modifierScan[vk] = (scan, flags & ~KeyFlags.Up);
                    TrackCtrlAlt(vk, scan, up);
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
                HideIdleCursor();
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
                    HideIdleCursor();
                    break;
                case EdgeHit h when _mode == InputMode.Controlling && _peer == from: HandleEdgeHit(from, h); break;
                case TakeOver when _mode == InputMode.Controlling && _peer == from:
                    ReturnLocal(_returnX, _returnY);
                    break;

                case MouseMove m when IsControlledBy(from):
                    if (_dragHold && !DragHoldExpired())
                    {
                        _heldDx += m.Dx;
                        _heldDy += m.Dy;
                        break;
                    }
                    InjectMove(m.Dx, m.Dy);
                    break;
                case MouseButtonMsg b when IsControlledBy(from):
                    if (b.Button == MouseButton.Left && !b.Down && _dragHold)
                    {
                        // Rilasciato prima che il trascinamento partisse: qui il pulsante non è mai stato premuto.
                        Log.Info("Pulsante rilasciato prima dell'inizio del trascinamento: annullato");
                        _dragHold = false;
                        FileDrag?.CancelIncoming();
                        FlushHeldMoves();
                        break;
                    }
                    if (b.Button == MouseButton.Left && !b.Down) _dragActive = false; // il rilascio lascia cadere i file
                    if (b.Down) _injectedButtons |= 1 << (int)b.Button; else _injectedButtons &= ~(1 << (int)b.Button);
                    PostInput(() => _platform.InjectButton(b.Button, b.Down));
                    break;
                case DragFiles d when IsControlledBy(from):
                    if (FileDrag is null || _injectedButtons != 0) break;
                    Log.Info("Il PC che comanda sta trascinando dei file qui");
                    _dragHold = true;
                    _dragHoldSince = Stopwatch.GetTimestamp();
                    _heldDx = _heldDy = 0;
                    FileDrag.Incoming(from, d.Offer);
                    break;
                case MouseWheel w when IsControlledBy(from): InjectWheel(w.Delta, w.Horizontal); break;
                case KeyMsg k when IsControlledBy(from):
                    if (k.Flags.HasFlag(KeyFlags.Up)) _injectedKeys.Remove(k.Vk); else _injectedKeys[k.Vk] = k;
                    PostInput(() => _platform.InjectKey(k.Vk, k.Scan, k.Flags));
                    break;
                case LockKeysMsg l when IsControlledBy(from):
                    PostInput(() => _platform.SetLockKeys(l.State));
                    break;
                case SecureAttention when IsControlledBy(from):
                    // Solo dal PC che ci sta comandando: non basta essere collegati per aprire la schermata di sicurezza.
                    Log.Info("Il PC che comanda chiede Ctrl+Alt+Canc");
                    _platform.SendSecureAttention();
                    break;
            }
        }
    }

    // ═══════════════════════ logica interna (chiamata sotto lock) ═══════════════════════

    private bool IsControlledBy(Guid peer) => _mode == InputMode.Controlled && _peer == peer;

    private static double ElapsedMs(long since) => (Stopwatch.GetTimestamp() - since) * 1000.0 / Stopwatch.Frequency;

    /// <summary>Invia al target gli spostamenti accumulati (se ce ne sono).</summary>
    private void FlushMoves()
    {
        if (_mode != InputMode.Controlling || (_pendDx == 0 && _pendDy == 0)) return;
        _net.Send(_peer, new MouseMove(_pendDx, _pendDy));
        _pendDx = _pendDy = 0;
        _lastMoveSent = Stopwatch.GetTimestamp();
    }

    /// <summary>Invia spostamenti e rotella accumulati: prima il cursore, poi lo scorrimento nel punto dove è arrivato.</summary>
    private void FlushPending()
    {
        FlushMoves();
        if (_mode != InputMode.Controlling || (_pendWheelV == 0 && _pendWheelH == 0)) return;
        _pendWheelV = SendWheel(_pendWheelV, false);
        _pendWheelH = SendWheel(_pendWheelH, true);
        _lastWheelSent = Stopwatch.GetTimestamp();
        if (_pendWheelV != 0 || _pendWheelH != 0) ScheduleFlush(WheelIntervalMs); // oltre il massimo di un evento: il resto dopo
    }

    /// <summary>Invia il delta (al massimo <see cref="MaxWheelDelta"/>) e restituisce quanto resta da inviare.</summary>
    private int SendWheel(int pending, bool horizontal)
    {
        if (pending == 0) return 0;
        var now = Math.Clamp(pending, -MaxWheelDelta, MaxWheelDelta);
        _net.Send(_peer, new MouseWheel(now, horizontal));
        return pending - now;
    }

    /// <summary>Programma l'invio di ciò che è in attesa, senza mai ritardare un invio già programmato prima.</summary>
    private void ScheduleFlush(double inMs)
    {
        var due = Stopwatch.GetTimestamp() + (long)(inMs * Stopwatch.Frequency / 1000);
        if (due >= _flushDue) return;
        _flushDue = due;
        _flushTimer.Change(Math.Max(1, (int)Math.Ceiling(inMs)), Timeout.Infinite);
    }

    /// <summary>
    /// Accoda un'azione sul thread di input. Chiude lo scatto di rotella in attesa: quelli che arrivano dopo non devono
    /// essere iniettati prima di questa azione.
    /// </summary>
    private void PostInput(Action action)
    {
        _wheelSlot = null;
        _platform.Post(() =>
        {
            FlushThrottledMove(); // il clic o il tasto arriva dove il cursore è già arrivato
            action();
        });
    }

    /// <summary>
    /// Inietta il movimento verso (x, y), oppure lo rimanda se si sta spostando una finestra e l'ultimo è troppo recente
    /// (vedi <see cref="WindowMoveIntervalMs"/>): allo scadere parte l'ultima posizione. Solo sul thread di input.
    /// </summary>
    private void InjectCursorMove(int x, int y)
    {
        var since = ElapsedMs(_lastMoveInjected);
        if (since < WindowMoveIntervalMs && _platform.IsMovingWindow())
        {
            if (_movePending) return;
            _movePending = true;
            _moveFlushTimer.Change(Math.Max(1, (int)Math.Ceiling(WindowMoveIntervalMs - since)), Timeout.Infinite);
            return;
        }
        _movePending = false;
        _platform.InjectMoveTo(x, y);
        _lastMoveInjected = Stopwatch.GetTimestamp();
    }

    /// <summary>Inietta il movimento rimandato da <see cref="InjectCursorMove"/>, se c'è. Solo sul thread di input.</summary>
    private void FlushThrottledMove()
    {
        if (!_movePending) return;
        _movePending = false;
        _platform.InjectMoveTo(_vx, _vy);
        _lastMoveInjected = Stopwatch.GetTimestamp();
    }

    private void InjectWheel(int delta, bool horizontal)
    {
        // Ancora in coda uno scatto nella stessa direzione: si somma a quello (un solo evento, una sola rinfrescata della lista).
        if (_wheelSlot is { } open && open.Horizontal == horizontal && Math.Abs((long)open.Delta + delta) <= MaxWheelDelta)
        {
            open.Delta += delta;
            return;
        }
        var slot = new WheelSlot { Delta = delta, Horizontal = horizontal };
        _wheelSlot = slot; // sostituisce (e quindi chiude) un eventuale scatto aperto nell'altra direzione
        _platform.Post(() =>
        {
            int d;
            lock (_gate)
            {
                d = slot.Delta;
                if (_wheelSlot == slot) _wheelSlot = null;
            }
            FlushThrottledMove();
            if (d != 0) _platform.InjectWheel(d, slot.Horizontal);
        });
    }

    /// <summary>
    /// Il desktop che riceve l'input è cambiato (schermata di blocco, richiesta UAC, Ctrl+Alt+Canc e ritorno).
    /// Va chiamato dal thread degli hook appena agganciato al nuovo desktop.
    ///
    /// Gli hook valgono per un solo desktop: nel breve intervallo in cui passano al nuovo, il mouse fisico muove davvero
    /// il cursore locale. Se si stava comandando un altro PC il cursore non è più nel punto di parcheggio e ogni movimento
    /// successivo verrebbe misurato da lì: il cursore sull'altro PC andrebbe alla deriva da solo. Lo riparcheggio.
    /// Sul PC comandato invece rileggo la posizione vera del cursore alla prossima iniezione.
    /// </summary>
    /// <summary>
    /// Il desktop di input è cambiato (schermata di blocco, UAC, Ctrl+Alt+Canc). Va chiamato dal nuovo thread di input.
    /// <paramref name="previous"/> e <paramref name="current"/> sono i nomi dei desktop ("Default", "Winlogon"), se noti.
    /// </summary>
    public void OnInputDesktopChanged(string? previous = null, string? current = null)
    {
        lock (_gate)
        {
            if (_mode == InputMode.Controlling)
            {
                (_refX, _refY) = _platform.ParkCursor(_parkX, _parkY);
                if (IsLocalSecureAttention(previous, current)) ForwardLocalSecureAttention();
            }
        }
        _platform.Post(() => (_vValid, _movePending) = (false, false)); // la posizione rimandata valeva per il desktop di prima
    }

    /// <summary>
    /// Ctrl+Alt (senza Win) premuti? AltGr non conta: con i layout che lo hanno Windows lo manda come Ctrl sinistro finto
    /// (scancode 0x21D) più Alt destro. Finché quel Ctrl finto è giù, l'Alt destro è AltGr e non un Alt.
    /// </summary>
    private void TrackCtrlAlt(ushort vk, ushort scan, bool up)
    {
        if (CtrlKeys.Contains(vk))
        {
            if (KeyCodes.IsAltGrFakeCtrl(vk, scan)) _altGrCtrlDown = !up;
            else if (up) _realCtrlDown.Remove(vk);
            else _realCtrlDown.Add(vk);
        }
        var ctrl = _realCtrlDown.Count > 0;
        var alt = _remoteDownKeys.Contains(0x12) || _remoteDownKeys.Contains(0xA4) || (_remoteDownKeys.Contains(0xA5) && !_altGrCtrlDown);
        var held = ctrl && alt && !WinKeys.Any(_remoteDownKeys.Contains);
        if (held && _ctrlAltUntil != long.MaxValue) _ctrlAltSince = Environment.TickCount64;
        if (held) _ctrlAltUntil = long.MaxValue;
        else if (_ctrlAltUntil == long.MaxValue) _ctrlAltUntil = Environment.TickCount64;
    }

    /// <summary>
    /// Il Ctrl+Alt+Canc vero non arriva agli hook: Windows apre subito la schermata di sicurezza su questo PC. Lo si
    /// riconosce così: si comanda un altro PC, Ctrl+Alt sono premuti (o appena rilasciati: i rilasci durante il cambio
    /// di desktop non li vede nessun hook) e il desktop passa dal normale a Winlogon.
    /// </summary>
    private bool IsLocalSecureAttention(string? previous, string? current)
    {
        if (!string.Equals(previous, "Default", StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.Equals(current, "Winlogon", StringComparison.OrdinalIgnoreCase)) return false;
        var now = Environment.TickCount64;
        if (now - _ctrlAltUntil > SecureAttentionWindowMs) return false;
        return now - _lastLocalSecureAttention > 2000; // un solo Ctrl+Alt+Canc per pressione
    }

    private void ForwardLocalSecureAttention()
    {
        _lastLocalSecureAttention = Environment.TickCount64;
        Log.Info("Ctrl+Alt+Canc premuto mentre si comanda un altro PC: lo mando a quel PC e chiudo la schermata qui " +
                 $"(schermata vista {Environment.TickCount64 - _ctrlAltSince} ms dopo aver premuto Ctrl+Alt)");
        FlushPending();
        // Ctrl e Alt si rilasciano subito sul PC comandato: i rilasci fisici forse non li vedremo (cambio di desktop).
        // Se arrivano lo stesso, un rilascio in più non fa nulla.
        foreach (var vk in _remoteDownKeys.Where(k => CtrlKeys.Contains(k) || AltKeys.Contains(k)).ToList())
        {
            var (scan, flags) = _modifierScan.GetValueOrDefault(vk);
            _net.Send(_peer, new KeyMsg(vk, scan, flags | KeyFlags.Up));
            _remoteDownKeys.Remove(vk);
        }
        _ctrlAltUntil = long.MinValue / 2;
        _realCtrlDown.Clear();
        _altGrCtrlDown = false;
        _net.Send(_peer, new SecureAttention());
        _platform.CancelLocalSecureScreen();
    }

    /// <summary>
    /// Il cursore non è ancora nel punto di parcheggio (vedi <see cref="IInputPlatform.ParkCursor"/>): ce lo porta ora.
    /// Va chiamato dal thread degli hook, così nessun movimento può essere misurato dal punto sbagliato.
    /// </summary>
    public void CompletePark()
    {
        lock (_gate)
        {
            if (_mode != InputMode.Controlling || (_refX == _parkX && _refY == _parkY)) return;
            _platform.SetCursorPos(_parkX, _parkY);
            (_refX, _refY) = (_parkX, _parkY);
        }
    }

    private void StartControlling(Guid target, Edge edge, float ratio, int x, int y, IReadOnlyList<Rect> monitors)
    {
        _returnX = x;
        _returnY = y;
        (_parkX, _parkY) = monitors.Count > 0 ? monitors[0].Center : (x, y);
        (_refX, _refY) = _platform.ParkCursor(_parkX, _parkY); // siamo già sul thread degli hook
        _remoteDownKeys.Clear();
        _swallowUp.Clear();
        _ctrlAltUntil = long.MinValue / 2;
        _realCtrlDown.Clear();
        _altGrCtrlDown = false;
        SetMode(InputMode.Controlling, target);
        _net.Send(target, new Enter(ScreenGeometry.Opposite(edge), ratio));
        SendLockKeys(target);
    }

    /// <summary>
    /// Manda al target lo stato dei tasti di blocco di questo PC, che ha la tastiera fisica e quindi le spie giuste.
    /// Letto sul thread di input (agganciato al desktop attivo, anche sulla schermata di blocco), poi inviato solo se
    /// nel frattempo il controllo non è passato altrove.
    /// </summary>
    private void SendLockKeys(Guid target)
    {
        _platform.Post(() =>
        {
            if (_platform.GetLockKeys() is not { } state) return;
            lock (_gate)
                if (_mode == InputMode.Controlling && _peer == target) _net.Send(target, new LockKeysMsg(state));
        });
    }

    /// <summary>Entro quanto tempo dal ritorno il primo movimento del mouse viene corretto (ms).</summary>
    private const int HomingWindowMs = 500;

    /// <summary>
    /// Riporta il controllo qui e il cursore nel punto (x, y).
    ///
    /// Spostare il cursore solo dal thread di iniezione non basta: se in quel momento il mouse fisico si sta muovendo,
    /// Windows ha già calcolato la posizione dell'evento successivo partendo dal centro (dove il cursore era parcheggiato)
    /// e sta aspettando la risposta dell'hook. Se SetCursorPos arriva in mezzo, appena l'hook risponde Windows applica
    /// quella posizione vecchia e il cursore riparte dal centro invece che dal bordo. Per questo il primo movimento
    /// dopo il ritorno passa da <see cref="OnLocalMouseMove"/>, che lo scarta e rimette il cursore sul bordo.
    /// </summary>
    private void ReturnLocal(int x, int y)
    {
        SetMode(InputMode.Local, Guid.Empty);
        var home = (x, y);
        _homing = home;
        _homingSince = Stopwatch.GetTimestamp();
        // Per quando il mouse è fermo (Ctrl+Alt+F12, disconnessione, input fisico sull'altro PC): nessun evento da correggere.
        _platform.Post(() =>
        {
            lock (_gate)
                if (_homing != home) return; // già sistemato dall'hook, o il controllo è ripartito
            _platform.SetCursorPos(x, y);
        });
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
        PostInput(() =>
        {
            var (x, y) = ScreenGeometry.EntryPoint(_platform.GetMonitors(), e.From, e.Ratio);
            _platform.InjectMoveTo(x, y);
            _lastMoveInjected = Stopwatch.GetTimestamp();
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
            SendLockKeys(hop.Id);
        }
    }

    private void InjectMove(int dx, int dy)
    {
        var controller = _peer;
        _wheelSlot = null; // come PostInput, ma senza iniettare prima il movimento rimandato: questo lo sostituisce
        _platform.Post(() =>
        {
            var monitors = _platform.GetMonitors();
            if (!_vValid) { (_vx, _vy) = _platform.GetCursorPos(); _vValid = true; }
            var (cx, cy) = (_vx, _vy);
            var (nx, ny, hit) = ScreenGeometry.MoveBy(monitors, cx, cy, dx, dy);
            if (nx != cx || ny != cy) InjectCursorMove(nx, ny);
            (_vx, _vy) = (nx, ny);

            lock (_gate)
            {
                if (!IsControlledBy(controller)) return;
                if (hit is not Edge edge) { _latchedEdge = null; _edgeDwellTarget = null; return; }
                // Un solo EdgeHit per "spinta" e mai durante un trascinamento.
                if (_latchedEdge == edge || _injectedButtons != 0) return;
                // Se in questo punto del bordo non c'è nessun PC accanto il cursore resta qui; scorrendo lungo
                // il bordo fino a dove un altro PC lo tocca, l'EdgeHit parte (il bordo non resta "agganciato").
                var ratio = ScreenGeometry.Ratio(monitors, edge, nx, ny);
                if (_layout.Cross(_net.SelfId, edge, ratio) is null) return;
                // Lo schermo intero conta solo sul PC con il mouse fisico: qui (un video a tutto schermo) si resterebbe bloccati.
                if (!EdgeAllowed(monitors, edge, nx, ny, ref _edgeDwellTarget, checkFullscreen: false)) return;
                _latchedEdge = edge;
                _net.Send(controller, new EdgeHit(edge, ratio));
            }
        });
    }

    /// <summary>
    /// Il bordo toccato in (x, y) fa passare all'altro PC, secondo le regole di <see cref="Input.EdgeSettings"/>?
    /// <paramref name="dwell"/> ricorda da quando si è contro il bordo, per la sosta.
    /// </summary>
    private bool EdgeAllowed(IReadOnlyList<Rect> monitors, Edge edge, int x, int y, ref (Edge Edge, long Since)? dwell, bool checkFullscreen = true)
    {
        EdgeSettings s;
        try { s = EdgeSettings(); }
        catch (Exception ex) { Log.Error("Impostazioni dei bordi", ex); s = Input.EdgeSettings.Default; }
        if (s.ScrollLockHolds && _platform.GetLockKeys() is { } locks && locks.HasFlag(LockKeys.Scroll)) return false;
        if (s.CornerGuard && EdgeGuard.InCorner(monitors, edge, x, y)) { dwell = null; return false; }
        if (checkFullscreen && s.BlockFullscreen && _platform.IsFullscreenAppAt(x, y)) return false;
        if (s.DelayMs > 0)
        {
            var now = Stopwatch.GetTimestamp();
            if (dwell is not { } d || d.Edge != edge) { dwell = (edge, now); return false; }
            if (ElapsedMs(d.Since) < s.DelayMs) return false;
        }
        dwell = null;
        return true;
    }

    private void TakeOverLocally()
    {
        var controller = _peer;
        Log.Info("Input fisico sul PC controllato: riprendo il controllo locale");
        SetMode(InputMode.Local, Guid.Empty);
        _net.Send(controller, new TakeOver());
        ReleaseInjected();
    }

    // ═══════════════════════ trascinamento di file ═══════════════════════

    /// <summary>
    /// Lato di chi trascina, con il solo pulsante sinistro premuto. Al primo contatto col bordo verso un PC collegato si
    /// chiede all'host se si stanno trascinando dei file (lo scopre l'icona con una finestrella invisibile sotto il
    /// cursore, su cui Esplora risorse "passa" i file). Se sì, al movimento successivo contro il bordo si passa all'altro
    /// PC come sempre, ma con il pulsante ancora premuto e i file al seguito; qui il trascinamento viene annullato.
    /// </summary>
    private bool DragAcross(int x, int y)
    {
        if (FileDrag is not { } drag) return false;
        var monitors = _platform.GetMonitors();
        if (ScreenGeometry.DetectEdge(monitors, x, y) is not Edge edge) { _edgeDwellLocal = null; return false; }
        if (_layout.Cross(_net.SelfId, edge, ScreenGeometry.Ratio(monitors, edge, x, y)) is not { } hop
            || hop.Id == _net.SelfId || !_net.IsConnected(hop.Id)) return false;
        if (!EdgeAllowed(monitors, edge, x, y, ref _edgeDwellLocal)) return false;

        if (_dragReady is { } ready && ready.Target == hop.Id)
        {
            Log.Info("Trascinamento verso l'altro PC");
            _dragReady = null;
            _dragProbing = null;
            _localButtons = 0; // il rilascio del pulsante ora va all'altro PC
            StartControlling(hop.Id, edge, hop.Ratio, x, y, monitors);
            _net.Send(hop.Id, new DragFiles(ready.Offer));
            _platform.CancelLocalDrag();
            drag.EndProbe();
            return true;
        }
        if (_dragProbing is null && _dragReady is null && !_dragProbeDone && drag.CanDragTo(hop.Id))
        {
            _dragProbing = hop.Id;
            drag.Probe(hop.Id);
        }
        return false;
    }

    /// <summary>Risposta alla prova: i file trascinati (offerta serializzata), oppure null se non si trascinano file.</summary>
    public void OnDragProbeResult(Guid target, byte[]? offer)
    {
        lock (_gate)
        {
            if (_dragProbing != target || _mode != InputMode.Local || _localButtons != LeftBit) return;
            _dragProbing = null;
            if (offer is null) _dragProbeDone = true; // non si riprova fino al prossimo clic
            else _dragReady = (target, offer);
        }
    }

    private void EndDragProbe()
    {
        var had = _dragProbing is not null || _dragReady is not null;
        _dragProbing = null;
        _dragReady = null;
        _dragProbeDone = false;
        if (had) FileDrag?.EndProbe();
    }

    /// <summary>
    /// Lato target: l'icona ha messo sotto il cursore la finestrella da cui parte il trascinamento. Premo il pulsante
    /// (cade su di lei e il trascinamento parte), poi applico i movimenti arrivati nel frattempo.
    /// </summary>
    public void OnDragSourceReady()
    {
        lock (_gate)
        {
            if (!_dragHold || _mode != InputMode.Controlled) return;
            _dragHold = false;
            _dragActive = true;
            _injectedButtons |= LeftBit;
            PostInput(() => _platform.InjectButton(MouseButton.Left, true));
            FlushHeldMoves();
        }
    }

    private bool DragHoldExpired()
    {
        if (ElapsedMs(_dragHoldSince) < DragHoldMaxMs) return false;
        Log.Warn("Il trascinamento dei file non è partito in tempo: annullato");
        _dragHold = false;
        FileDrag?.CancelIncoming();
        FlushHeldMoves();
        return true;
    }

    private void FlushHeldMoves()
    {
        if (_heldDx == 0 && _heldDy == 0) return;
        var (dx, dy) = (_heldDx, _heldDy);
        _heldDx = _heldDy = 0;
        InjectMove(dx, dy);
    }

    /// <summary>
    /// Dopo le iniezioni ancora in coda (il cursore deve essere arrivato dove resterà), e solo se nel frattempo nessuno
    /// è tornato a comandare questo PC o l'ha usato fisicamente.
    /// </summary>
    private void HideIdleCursor()
    {
        PostInput(() =>
        {
            lock (_gate) if (_mode != InputMode.Local) return;
            _platform.HideIdleCursor();
        });
    }

    private void ReleaseInjected()
    {
        var keys = _injectedKeys.Values.ToList();
        var buttons = _injectedButtons;
        // Trascinamento di file in corso: SetMode l'ha già fatto annullare all'icona. Il pulsante si rilascia un attimo
        // dopo, così i file non cadono dove si trova il cursore in quel momento.
        var dragging = _dragInterrupted;
        _dragInterrupted = false;
        _injectedKeys.Clear();
        _injectedButtons = 0;
        _latchedEdge = null;
        PostInput(() =>
        {
            _vValid = false;
            if (dragging && (buttons & LeftBit) != 0) Thread.Sleep(200);
            foreach (var k in keys) _platform.InjectKey(k.Vk, k.Scan, k.Flags | KeyFlags.Up);
            for (int b = 0; b <= (int)MouseButton.X2; b++)
                if ((buttons & (1 << b)) != 0) _platform.InjectButton((MouseButton)b, false);
        });
    }
}
