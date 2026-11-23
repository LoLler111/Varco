using Varco.Core.Config;
using Varco.Core.Clipboard;
using Varco.Core.Control;
using Varco.Core.Input;
using Varco.Core.Net;
using Varco.Core.Protocol;

namespace Varco.Core;

/// <summary>
/// Mette insieme configurazione, rete, layout e input. Indipendente da Windows (l'input arriva da <see cref="IInputPlatform"/>).
/// </summary>
public sealed partial class VarcoHost : IAsyncDisposable
{
    private readonly ConfigStore _store;
    private readonly NetworkProxy _net;
    private readonly int? _tcpPortOverride;
    private readonly int? _discoveryPortOverride;
    private readonly SemaphoreSlim _netGate = new(1, 1);
    private NetworkNode? _node;
    private long _lastResume;

    public LayoutManager Layout { get; }
    public InputCoordinator Coordinator { get; }
    public NetworkNode? Node => _node;

    /// <summary>Aggiornamenti automatici: impostato dall'agent solo quando gira installato (non in sviluppo).</summary>
    public Update.Updater? Updater { get; private set; }

    public static Version CurrentVersion => Update.UpdateSignature.Normalize(typeof(VarcoHost).Assembly.GetName().Version ?? new Version(0, 0, 0));
    /// <summary>Quando è partito questo host: le statistiche di connessione partono da qui.</summary>
    public DateTime StartedUtc { get; } = DateTime.UtcNow;

    public void EnableUpdates()
    {
        var cfg = _store.Current;
        Updater = new Update.Updater(new Update.UpdaterOptions
        {
            FeedUrl = string.IsNullOrWhiteSpace(cfg.UpdateFeedUrl) ? Update.Updater.GitHubFeed : cfg.UpdateFeedUrl,
            CurrentVersion = CurrentVersion,
            DownloadDirectory = Path.Combine(_store.Directory, "updates"),
            CanInstallNow = () => Coordinator.Mode == InputMode.Local,
            Enabled = () => _store.Current.AutoUpdate,
        });
        Updater.Start();
    }

    /// <summary>Null se l'agent gira senza appunti (es. --no-input per i test di rete).</summary>
    public ClipboardSync? Clipboard { get; }

    /// <param name="fileAccess">Con gli appunti attivi abilita anche i file copiati in Esplora risorse.</param>
    public VarcoHost(ConfigStore store, IInputPlatform platform, int? tcpPort = null, int? discoveryPort = null,
        IClipboardPlatform? clipboard = null, Files.IFileAccess? fileAccess = null, Audio.IAudioPlatform? audio = null)
    {
        _store = store;
        _tcpPortOverride = tcpPort;
        _discoveryPortOverride = discoveryPort;
        var cfg = store.Current;
        _net = new NetworkProxy(cfg.MachineId);
        Layout = new LayoutManager(cfg.MachineId, cfg.MachineName, cfg.Layout);
        Layout.Changed += l =>
        {
            _store.Update(c => c.Layout = l);
            _net.Broadcast(new LayoutMsg(l));
        };
        MediaRouter = new MediaKeyRouter(cfg.MachineId);
        Audio = new Audio.AudioShare(_net, audio, () => _store.Current.AudioRouting,
            r => _store.Update(c => c.AudioRouting = r, immediate: true), NameOf);
        Coordinator = new InputCoordinator(_net, platform, Layout)
        {
            EdgeSettings = () =>
            {
                var c = _store.Current;
                return new EdgeSettings(c.EdgeCorners, c.EdgeDelay ? EdgeSettings.StandardDelayMs : 0, c.EdgeBlockFullscreen, c.EdgeScrollLock);
            },
            MediaRouter = MediaRouter,
            MediaKeysFollowAudio = () => _store.Current.MediaKeysFollowAudio,
            PointerScaleCompensation = () => _store.Current.PointerScaleCompensation,
            VolumeKeysTarget = Audio.VolumeKeysTarget,
        };
        if (clipboard is not null)
        {
            if (fileAccess is not null) FileOffers = new Files.FileOffers(fileAccess);
            Clipboard = new ClipboardSync(_net, clipboard,
                enabled: () => _store.Current.ShareClipboard,
                canSend: () => Coordinator.Mode != InputMode.Controlling,
                files: FileOffers);
            Clipboard.FilesOffered += OnFilesOffered;
        }
        _role = new ControlRole(cfg.MachineId);
        _activityWatch = new Timer(_ => SendInputActivity(), null, 500, 500);
        // Cambi di risoluzione o monitor collegati/scollegati: la dimensione del desktop finisce nel layout condiviso.
        _desktopWatch = new Timer(_ =>
        {
            try { Coordinator.SyncDesktopSize(); }
            catch (Exception ex) { Log.Error("Dimensione desktop", ex); }
        }, null, 2000, 2000);
    }

    private readonly Timer _desktopWatch;

    public Task StartAsync() => RestartNetworkAsync();

    public async Task RestartNetworkAsync()
    {
        await _netGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_node is not null)
            {
                _net.Node = null;
                await _node.DisposeAsync().ConfigureAwait(false);
                _node = null;
                if (Coordinator.ActivePeer is Guid active) Coordinator.OnPeerDisconnected(active);
            }

            var cfg = _store.Current;
            if (cfg.Paused)
            {
                Log.Info("Connessione disattivata dall'utente: rete chiusa");
                return;
            }
            if (!GroupKey.TryParse(cfg.GroupKey, out var key))
            {
                // Primo avvio: ogni PC nasce come gruppo di uno. L'abbinamento poi unisce i gruppi.
                var fresh = GroupKey.Generate();
                _store.Update(c => c.GroupKey = fresh, immediate: true);
                GroupKey.TryParse(fresh, out key);
                Log.Info("Creata una nuova chiave di gruppo per questo PC");
            }

            var opts = new NodeOptions
            {
                TcpPort = _tcpPortOverride ?? cfg.TcpPort,
                DiscoveryPort = _discoveryPortOverride ?? cfg.DiscoveryPort,
                HeartbeatIntervalMs = cfg.HeartbeatIntervalMs,
                HeartbeatTimeoutMs = cfg.HeartbeatTimeoutMs,
                ManualAddresses = cfg.ManualAddresses.ToList(),
                KnownPeers = cfg.KnownPeers.Select(p => (p.Id, p.Name, (IReadOnlyList<string>)p.LastAddresses.ToList())).ToList(),
                RetiredKeysFor = RetiredKeysFor,
            };

            NetworkNode node;
            try
            {
                node = new NetworkNode(cfg.MachineId, cfg.MachineName, key, opts);
            }
            catch (Exception ex)
            {
                Log.Error($"Impossibile aprire la porta {opts.TcpPort}/{opts.DiscoveryPort}, riprovo tra 5 s", ex);
                _ = Task.Delay(5000).ContinueWith(_ => RestartNetworkAsync(), TaskScheduler.Default);
                return;
            }

            node.PeerConnected += c =>
            {
                CheckGroupKey(c);
                Coordinator.OnPeerConnected(c.PeerId, c.PeerName);
                Clipboard?.OnPeerConnected(c.PeerId);
                c.Send(new AudioActive(Volatile.Read(ref _localAudio) == 1));
                c.Send(new InputActivity(IdleMs()));
                Audio.OnPeerConnected(c.PeerId);
            };
            node.PeerDisconnected += (id, _) =>
            {
                Coordinator.OnPeerDisconnected(id);
                Clipboard?.OnPeerDisconnected(id);
                MediaRouter.Forget(id);
                _role.Forget(id);
                Audio.OnPeerDisconnected(id);
            };
            node.MessageReceived += (id, m) =>
            {
                if (m is InstallUpdate iu) { OnInstallUpdateRequested(id, iu.Version); return; }
                if (m is GroupKeyChange gk) { OnGroupKeyChange(id, gk); return; }
                if (Audio.OnMessage(id, m)) return;
                if (m is AudioActive aa) { MediaRouter.SetPlaying(id, aa.Playing, Environment.TickCount64); return; }
                if (m is LockScreen) { OnRemoteLock(id); return; }
                if (m is InputActivity ia) { _role.OnPeerActivity(id, ia.IdleMs, Environment.TickCount64); return; }
                if (Clipboard?.OnMessage(id, m) == true) return;
                Coordinator.OnMessage(id, m, ConnectedPeers);
            };
            node.PeerLearned += RememberPeer;
            node.Announced += OnAnnounced;
            node.CallbackRequested += OnCallbackRequested;
            node.PairingRequested = OnPairingConnectionAsync;
            if (FileOffers is { } offers)
                node.FileProvider = (peer, offer, index, offset) => _store.Current.ShareClipboard
                    ? offers.Open(offer, index, offset)
                    : (null, 0, "condivisione degli appunti spenta");
            _node = node;
            _net.Node = node;
            node.Start();
            Audio.Start();
        }
        finally
        {
            _netGate.Release();
        }
    }

    /// <summary>Nome di un PC del gruppo (anche scollegato) per log e Mixer volume.</summary>
    private string NameOf(Guid id)
    {
        var cfg = _store.Current;
        if (id == cfg.MachineId) return cfg.MachineName;
        return ConnectedPeers().FirstOrDefault(p => p.Item1 == id).Item2
            ?? cfg.KnownPeers.FirstOrDefault(p => p.Id == id)?.Name
            ?? Layout.Current.Entries.FirstOrDefault(e => e.Id == id)?.Name
            ?? id.ToString()[..8];
    }

    private IEnumerable<(Guid, string)> ConnectedPeers() =>
        _node?.GetStatus().Where(s => s.State == PeerState.Connected && s.Id is not null).Select(s => (s.Id!.Value, s.Name)) ?? [];

    private void RememberPeer(Guid id, string name, string address)
    {
        _store.Update(c =>
        {
            var kp = c.KnownPeers.FirstOrDefault(p => p.Id == id);
            if (kp is null) c.KnownPeers.Add(kp = new KnownPeer { Id = id });
            kp.Name = name;
            kp.LastSeenUtc = DateTime.UtcNow;
            kp.LastAddresses.Remove(address);
            kp.LastAddresses.Insert(0, address);
            if (kp.LastAddresses.Count > 4) kp.LastAddresses.RemoveRange(4, kp.LastAddresses.Count - 4);
        });
    }

    /// <summary>Dopo il risveglio i socket vecchi non sono affidabili: chiudo tutto e riconnetto subito.</summary>
    public void OnResume()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Exchange(ref _lastResume, now) < 10_000) return; // la notifica arriva doppia
        _node?.ResetAll("ripresa dallo standby");
    }

    public void OnSuspend() => Coordinator.OnPeerDisconnected(Coordinator.ActivePeer ?? Guid.Empty);

    // ───────────────────────────── tasti multimediali ─────────────────────────────

    /// <summary>Chi sta suonando nel gruppo, per mandare lì i tasti multimediali.</summary>
    public MediaKeyRouter MediaRouter { get; }
    private int _localAudio; // 1 = suona

    /// <summary>C'è almeno un altro PC collegato? Solo allora serve sapere se qui suona qualcosa.</summary>
    public bool HasConnectedPeers => _node?.ConnectedPeers.Count > 0;

    /// <summary>Esce del suono da questo PC? (dall'agent, che lo controlla ogni pochi secondi). Lo sanno anche gli altri PC.</summary>
    public void SetLocalAudio(bool playing)
    {
        MediaRouter.SetPlaying(MediaRouter.Self, playing, Environment.TickCount64);
        if (Interlocked.Exchange(ref _localAudio, playing ? 1 : 0) == (playing ? 1 : 0)) return;
        Log.Info(playing ? "Esce del suono da questo PC" : "Da questo PC non esce più suono");
        _net.Broadcast(new AudioActive(playing));
    }

    // ───────────────────────────── audio fra i PC ─────────────────────────────

    /// <summary>Audio degli altri PC sul PC centrale.</summary>
    public Audio.AudioShare Audio { get; }

    private ControlResponse SetAudioRouting(Guid? hub, List<Guid>? sources)
    {
        var h = hub ?? Guid.Empty;
        var cfg = _store.Current;
        var known = cfg.KnownPeers.Select(p => p.Id).Append(cfg.MachineId).ToHashSet();
        if (h != Guid.Empty && !known.Contains(h)) return new ControlResponse { Ok = false, Error = "PC centrale sconosciuto" };
        Audio.SetRouting(h, (sources ?? []).Where(known.Contains), DateTime.UtcNow);
        return new ControlResponse { Status = GetStatus() };
    }

    // ───────────────────────────── blocco di gruppo ─────────────────────────────

    private readonly GroupLockPolicy _lockPolicy = new();

    /// <summary>Blocca questo PC (impostato dall'agent). Null = non disponibile (test, agent senza input).</summary>
    public Action? LockWorkstation { get; set; }

    /// <summary>Ultimo input fisico (non iniettato) su questo PC, in ms di TickCount64: impostato dall'agent con gli hook.</summary>
    public Func<long>? LastPhysicalInput { get; set; }

    // ───────────────────────────── master e slave ─────────────────────────────

    /// <summary>Chi è il master del gruppo (il PC toccato più di recente), per l'icona nella tray.</summary>
    private readonly ControlRole _role;
    private readonly Timer _activityWatch;
    private long _sentLastInput = -1, _sentAt;

    private uint IdleMs() => ControlRole.IdleMs(LastPhysicalInput?.Invoke() ?? 0, Environment.TickCount64);

    /// <summary>Dice agli altri PC quando è stato toccato questo, al massimo una volta al secondo e solo se è cambiato.</summary>
    private void SendInputActivity()
    {
        try
        {
            var last = LastPhysicalInput?.Invoke() ?? 0;
            var now = Environment.TickCount64;
            if (last == Interlocked.Read(ref _sentLastInput) || now - Interlocked.Read(ref _sentAt) < 1000) return;
            Interlocked.Exchange(ref _sentLastInput, last);
            Interlocked.Exchange(ref _sentAt, now);
            _net.Broadcast(new InputActivity(ControlRole.IdleMs(last, now)));
        }
        catch (Exception ex) { Log.Error("Attività di mouse e tastiera", ex); }
    }

    /// <summary>Il master del gruppo se non è questo PC; null = questo PC è master.</summary>
    private Guid? Master(IReadOnlyCollection<PeerStatus> peers)
    {
        switch (Coordinator.Mode)
        {
            case InputMode.Controlling: return null;
            case InputMode.Controlled: return Coordinator.ActivePeer;
        }
        var connected = peers.Where(p => p.State == PeerState.Connected && p.Id is not null).Select(p => p.Id!.Value).ToHashSet();
        return _role.Master(LastPhysicalInput?.Invoke() ?? 0, connected.Contains);
    }

    /// <summary>Questo PC si è appena bloccato. Se l'ha bloccato a mano chi ci sta davanti, si bloccano anche gli altri.</summary>
    /// <param name="lastPhysicalInputMs">Ultimo input fisico visto qui (TickCount64); 0 = mai.</param>
    public void OnSessionLocked(long lastPhysicalInputMs)
    {
        if (!_store.Current.GroupLock) return;
        var now = Environment.TickCount64;
        if (!_lockPolicy.ShouldPropagate(now, lastPhysicalInputMs))
        {
            Log.Info($"Schermo bloccato (ultimo input fisico {(lastPhysicalInputMs == 0 ? "mai" : $"{(now - lastPhysicalInputMs) / 1000} s fa")}, " +
                     "o chiesto da un altro PC): gli altri PC restano come sono");
            return;
        }
        var peers = ConnectedPeers().Select(p => p.Item2).ToList();
        Log.Info(peers.Count == 0 ? "Schermo bloccato: nessun altro PC collegato da bloccare"
                                  : $"Schermo bloccato a mano: blocco anche {string.Join(", ", peers)}");
        _net.Broadcast(new LockScreen());
    }

    private void OnRemoteLock(Guid from)
    {
        var name = ConnectedPeers().FirstOrDefault(p => p.Item1 == from).Item2 ?? from.ToString()[..8];
        if (!_store.Current.GroupLock)
        {
            Log.Info($"{name} chiede di bloccare lo schermo, ma qui il blocco di gruppo è spento");
            return;
        }
        Log.Info($"{name} è stato bloccato: blocco anche questo PC");
        _lockPolicy.OnRemoteLock(Environment.TickCount64);
        try { LockWorkstation?.Invoke(); }
        catch (Exception ex) { Log.Error("Blocco dello schermo", ex); }
    }

    // ───────────────────────────── aggiornamento immediato ─────────────────────────────

    /// <summary>
    /// «Aggiorna subito tutti i PC»: avvisa i PC collegati e poco dopo installa anche qui. Prima gli altri, perché
    /// l'installazione riavvia questo agent e chiude le connessioni.
    /// </summary>
    private ControlResponse InstallUpdateEverywhere()
    {
        var updater = Updater!;
        var version = updater.Status.Latest ?? "";
        var peers = ConnectedPeers().Select(p => p.Item2).ToList();
        Log.Info($"Aggiornamento immediato{(version.Length > 0 ? $" alla {version}" : "")} richiesto dall'utente; " +
            (peers.Count == 0 ? "nessun altro PC collegato" : $"avviso {string.Join(", ", peers)}"));
        _net.Broadcast(new InstallUpdate(version));
        _ = Task.Delay(peers.Count == 0 ? 0 : 3000).ContinueWith(_ => updater.InstallNow(), TaskScheduler.Default);
        return new ControlResponse { Status = GetStatus() };
    }

    private void OnInstallUpdateRequested(Guid from, string version)
    {
        var name = ConnectedPeers().FirstOrDefault(p => p.Item1 == from).Item2 ?? from.ToString()[..8];
        if (Updater is null)
        {
            Log.Info($"{name} chiede di aggiornare subito, ma qui gli aggiornamenti non sono attivi (Varco non installato)");
            return;
        }
        if (Update.UpdateSignature.TryParseVersion(version, out var v) && v <= CurrentVersion)
        {
            Log.Info($"{name} chiede di aggiornare subito alla {version}: questo PC ha già la {CurrentVersion.ToString(3)}");
            return;
        }
        Log.Info($"{name} chiede di aggiornare subito{(version.Length > 0 ? $" alla {version}" : "")}");
        Updater.InstallNow();
    }

    // ───────────────────────────── comandi dalla tray ─────────────────────────────

    public async Task<ControlResponse> HandleAsync(ControlRequest req)
    {
        try
        {
            switch (req.Cmd)
            {
                case "status":
                    return new ControlResponse { Status = GetStatus() };

                case "getKey":
                    return new ControlResponse { Key = _store.Current.GroupKey };

                case "checkUpdate":
                    if (Updater is null) return Fail("Gli aggiornamenti funzionano solo con Varco installato.");
                    Updater.CheckSoon();
                    return new ControlResponse { Status = GetStatus() };

                case "installUpdateNow":
                    if (Updater is null) return Fail("Gli aggiornamenti funzionano solo con Varco installato.");
                    return InstallUpdateEverywhere();

                case "setAutoUpdate":
                    _store.Update(c => c.AutoUpdate = req.Enabled != false, immediate: true);
                    if (req.Enabled != false) Updater?.CheckSoon();
                    return new ControlResponse { Status = GetStatus() };

                case "filesOffer":
                    return new ControlResponse { FilesOffer = await WaitFilesOfferAsync(req.Seq).ConfigureAwait(false) };

                case "localFiles":
                    return OfferLocalFiles(req.Paths);

                case "dragEvents":
                    return new ControlResponse { DragEvent = await WaitDragEventAsync(req.Seq).ConfigureAwait(false) };

                case "dragFiles":
                    return OnDraggedFiles(req.Seq, req.Paths);

                case "dragData":
                    return OnDraggedData(req.Seq, req.Formats);

                case "dragReady":
                    return OnDragSourceReady(req.Seq);

                case "setClipboard":
                    _store.Update(c => c.ShareClipboard = req.Enabled != false, immediate: true);
                    Log.Info($"Appunti condivisi {(req.Enabled != false ? "attivati" : "disattivati")}");
                    Clipboard?.AnnounceCaps();
                    return new ControlResponse { Status = GetStatus() };

                case "setMonitorStandby":
                    _store.Update(c => c.MonitorStandby = req.Enabled != false, immediate: true);
                    Log.Info($"Standby dei monitor mentre si comanda un altro PC {(req.Enabled != false ? "attivato" : "disattivato")}");
                    return new ControlResponse { Status = GetStatus() };

                case "setKeepScreenOnWithAudio":
                    _store.Update(c => c.KeepScreenOnWithAudio = req.Enabled != false, immediate: true);
                    Log.Info($"Schermo acceso mentre suona musica o un video {(req.Enabled != false ? "attivato" : "disattivato")}");
                    return new ControlResponse { Status = GetStatus() };

                case "setEdgeOption":
                {
                    var on = req.Enabled != false;
                    Action<VarcoConfig>? set = req.Option switch
                    {
                        "corners" => c => c.EdgeCorners = on,
                        "delay" => c => c.EdgeDelay = on,
                        "fullscreen" => c => c.EdgeBlockFullscreen = on,
                        "scrollLock" => c => c.EdgeScrollLock = on,
                        _ => null,
                    };
                    if (set is null) return new ControlResponse { Ok = false, Error = $"Regola dei bordi sconosciuta: {req.Option}" };
                    _store.Update(set, immediate: true);
                    Log.Info($"Bordi: {req.Option} {(on ? "attivato" : "disattivato")}");
                    return new ControlResponse { Status = GetStatus() };
                }

                case "setInputOption":
                {
                    var on = req.Enabled != false;
                    Action<VarcoConfig>? set = req.Option switch
                    {
                        "groupLock" => c => c.GroupLock = on,
                        "mediaKeys" => c => c.MediaKeysFollowAudio = on,
                        "pointerScale" => c => c.PointerScaleCompensation = on,
                        _ => null,
                    };
                    if (set is null) return new ControlResponse { Ok = false, Error = $"Opzione sconosciuta: {req.Option}" };
                    _store.Update(set, immediate: true);
                    Log.Info($"Opzione {req.Option} {(on ? "attivata" : "disattivata")}");
                    return new ControlResponse { Status = GetStatus() };
                }

                case "setAudio":
                    return SetAudioRouting(req.Id, req.Sources);

                case "setPaused":
                {
                    var paused = req.Enabled == true;
                    if (paused == _store.Current.Paused) return new ControlResponse { Status = GetStatus() };
                    if (paused) CancelPairing();
                    _store.Update(c => c.Paused = paused, immediate: true);
                    Log.Info($"Connessione {(paused ? "disattivata" : "riattivata")} dall'utente");
                    await RestartNetworkAsync().ConfigureAwait(false);
                    return new ControlResponse { Status = GetStatus() };
                }

                case "newGroup":
                {
                    var key = GroupKey.Generate();
                    await JoinGroupAsync(key).ConfigureAwait(false);
                    return new ControlResponse { Key = key };
                }

                case "setKey":
                    if (!GroupKey.TryParse(req.Key, out var bytes))
                        return Fail("Chiave non valida: controlla di averla copiata per intero (8 blocchi da 4 caratteri).");
                    await JoinGroupAsync(GroupKey.Format(bytes)).ConfigureAwait(false);
                    return new ControlResponse { Key = _store.Current.GroupKey };

                case "setRow":
                {
                    if (req.Order is null || req.Order.Count == 0) return Fail("Ordine vuoto");
                    var names = Layout.Current.Entries.ToDictionary(e => e.Id, e => e.Name);
                    foreach (var kp in _store.Current.KnownPeers) names.TryAdd(kp.Id, kp.Name);
                    Layout.SetRow(req.Order.Select(id => (id, names.GetValueOrDefault(id, id.ToString()[..8]))).ToList());
                    return new ControlResponse { Status = GetStatus() };
                }

                case "setLayout":
                    if (req.Positions is null || req.Positions.Count == 0) return Fail("Disposizione vuota");
                    Layout.SetPositions(req.Positions.Select(p => (p.Id, p.X, p.Y)));
                    return new ControlResponse { Status = GetStatus() };

                case "addAddress":
                    if (string.IsNullOrWhiteSpace(req.Address)) return Fail("Indirizzo vuoto");
                    if (_node is { } n && NetworkNode.TryParseEndpoint(req.Address, n.ListenPort, out var own) && n.IsOwnEndpoint(own))
                        return Fail("Questo è l'indirizzo di questo PC: scrivi quello dell'altro PC.");
                    _store.Update(c => { if (!c.ManualAddresses.Contains(req.Address.Trim())) c.ManualAddresses.Add(req.Address.Trim()); }, immediate: true);
                    await RestartNetworkAsync().ConfigureAwait(false);
                    return new ControlResponse { Status = GetStatus() };

                case "removeAddress":
                    _store.Update(c => c.ManualAddresses.Remove(req.Address?.Trim() ?? ""), immediate: true);
                    await RestartNetworkAsync().ConfigureAwait(false);
                    return new ControlResponse { Status = GetStatus() };

                case "pair":
                    if (_store.Current.Paused) return Fail("La connessione è disattivata: riattivala per collegare un altro PC.");
                    return StartPairing(req.Id, req.Address);

                case "pairAnswer":
                    return AnswerPairing(req.RequestId, req.Accept == true);

                case "pairCancel":
                    CancelPairing();
                    return new ControlResponse();

                case "forget":
                    if (req.Id is not Guid fid) return Fail("PC non indicato");
                    await ForgetAsync(fid).ConfigureAwait(false);
                    return new ControlResponse { Status = GetStatus() };

                case "leaveGroup":
                    await JoinGroupAsync(GroupKey.Generate()).ConfigureAwait(false);
                    return new ControlResponse { Status = GetStatus() };

                case "exportLogs":
                    return new ControlResponse { Zip = Diagnostics.ExportLogs(_store, GetStatus()) };

                case "reconnect":
                    _node?.ResetAll("richiesta dall'utente");
                    return new ControlResponse { Status = GetStatus() };

                default:
                    return Fail($"Comando sconosciuto: {req.Cmd}");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Comando {req.Cmd}", ex);
            return Fail(ex.Message);
        }

        static ControlResponse Fail(string msg) => new() { Ok = false, Error = msg };
    }

    private async Task JoinGroupAsync(string key)
    {
        if (key == _store.Current.GroupKey) return;
        Log.Info("Nuova chiave di gruppo impostata");
        _store.Update(c =>
        {
            c.GroupKey = key;
            ResetGroupHistory(c);
            c.KnownPeers.Clear();
        }, immediate: true);
        Layout.Reset();
        await RestartNetworkAsync().ConfigureAwait(false);
    }

    public StatusDto GetStatus()
    {
        var cfg = _store.Current;
        var peers = _node?.GetStatus() ?? [];
        // In pausa il nodo non c'è: mostro comunque i PC noti, così la finestra non sembra quella del primo avvio.
        if (cfg.Paused && _node is null)
            peers = cfg.KnownPeers.Select(p => new PeerStatus(p.Id, p.Name, PeerState.Disconnected, null, -1, null)).ToList();
        var active = Coordinator.ActivePeer;
        var (incoming, outgoing) = PairingStatus();
        return new StatusDto
        {
            MachineId = cfg.MachineId,
            MachineName = cfg.MachineName,
            HasKey = GroupKey.TryParse(cfg.GroupKey, out _),
            Paused = cfg.Paused,
            Mode = Coordinator.Mode.ToString(),
            ActivePeerName = active is Guid a ? peers.FirstOrDefault(p => p.Id == a)?.Name : null,
            ControllerName = Master(peers) is Guid master ? peers.FirstOrDefault(p => p.Id == master)?.Name : null,
            ListenPort = _node?.ListenPort ?? 0,
            Peers = peers.Select(p => new PeerDto
            {
                Id = p.Id,
                Name = p.Name,
                State = cfg.Paused ? "Paused" : p.State.ToString(),
                Address = p.Address,
                RttMs = Math.Round(p.RttMs, 1),
                LastError = p.LastError,
                Link = NetworkLinks.Describe(p.LocalEndPoint?.Address)?.Kind,
                Adapter = NetworkLinks.Describe(p.LocalEndPoint?.Address)?.Adapter,
                LocalAddress = p.LocalEndPoint is { } le ? (le.Address.IsIPv4MappedToIPv6 ? le.Address.MapToIPv4() : le.Address).ToString() : null,
                ConnectedSinceUtc = p.ConnectedSinceUtc,
                Via = p.Via,
                Drops24h = p.Drops24h,
                LastDropReason = p.LastDropReason,
                LastDropUtc = p.LastDropUtc,
            }).ToList(),
            AgentStartedUtc = StartedUtc,
            Layout = Layout.Current.Entries,
            ManualAddresses = cfg.ManualAddresses.ToList(),
            Discovered = DiscoveredPeers(),
            IncomingPair = incoming,
            OutgoingPair = outgoing,
            Version = CurrentVersion.ToString(3),
            AutoUpdate = cfg.AutoUpdate,
            ShareClipboard = cfg.ShareClipboard,
            MonitorStandby = cfg.MonitorStandby,
            KeepScreenOnWithAudio = cfg.KeepScreenOnWithAudio,
            EdgeCorners = cfg.EdgeCorners,
            EdgeDelay = cfg.EdgeDelay,
            EdgeBlockFullscreen = cfg.EdgeBlockFullscreen,
            EdgeScrollLock = cfg.EdgeScrollLock,
            GroupLock = cfg.GroupLock,
            MediaKeysFollowAudio = cfg.MediaKeysFollowAudio,
            PointerScaleCompensation = cfg.PointerScaleCompensation,
            AudioHub = cfg.AudioRouting.Hub,
            AudioSources = cfg.AudioRouting.Sources.ToList(),
            AudioSending = Audio.Sending,
            AudioReceiving = Audio.Receiving(Environment.TickCount64),
            Update = Updater?.Status,
        };
    }

    public async ValueTask DisposeAsync()
    {
        Updater?.Dispose();
        Audio.Dispose();
        await _desktopWatch.DisposeAsync().ConfigureAwait(false);
        await _activityWatch.DisposeAsync().ConfigureAwait(false);
        _net.Node = null;
        if (_node is not null) await _node.DisposeAsync().ConfigureAwait(false);
        _store.SaveNow();
    }

    /// <summary>Permette di ricreare il nodo di rete (cambio chiave/indirizzi) senza toccare il coordinatore.</summary>
    private sealed class NetworkProxy(Guid self) : IPeerNetwork
    {
        public volatile NetworkNode? Node;
        public Guid SelfId { get; } = self;
        public bool IsConnected(Guid id) => Node?.IsConnected(id) ?? false;
        public bool Send(Guid id, Message m) => Node?.Send(id, m) ?? false;
        public void Broadcast(Message m) => Node?.Broadcast(m);
    }
}
