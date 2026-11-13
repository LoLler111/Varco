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
        IClipboardPlatform? clipboard = null, Files.IFileAccess? fileAccess = null)
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
        Coordinator = new InputCoordinator(_net, platform, Layout);
        if (clipboard is not null)
        {
            if (fileAccess is not null) FileOffers = new Files.FileOffers(fileAccess);
            Clipboard = new ClipboardSync(_net, clipboard,
                enabled: () => _store.Current.ShareClipboard,
                canSend: () => Coordinator.Mode != InputMode.Controlling,
                files: FileOffers);
            Clipboard.FilesOffered += OnFilesOffered;
        }
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
                Coordinator.OnPeerConnected(c.PeerId, c.PeerName);
                Clipboard?.OnPeerConnected(c.PeerId);
            };
            node.PeerDisconnected += (id, _) =>
            {
                Coordinator.OnPeerDisconnected(id);
                Clipboard?.OnPeerDisconnected(id);
            };
            node.MessageReceived += (id, m) =>
            {
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
        }
        finally
        {
            _netGate.Release();
        }
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

                case "setAutoUpdate":
                    _store.Update(c => c.AutoUpdate = req.Enabled != false, immediate: true);
                    if (req.Enabled != false) Updater?.CheckSoon();
                    return new ControlResponse { Status = GetStatus() };

                case "filesOffer":
                    return new ControlResponse { FilesOffer = await WaitFilesOfferAsync(req.Seq).ConfigureAwait(false) };

                case "setClipboard":
                    _store.Update(c => c.ShareClipboard = req.Enabled != false, immediate: true);
                    Log.Info($"Appunti condivisi {(req.Enabled != false ? "attivati" : "disattivati")}");
                    Clipboard?.AnnounceCaps();
                    return new ControlResponse { Status = GetStatus() };

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
            c.KnownPeers.Clear();
        }, immediate: true);
        Layout.Reset();
        await RestartNetworkAsync().ConfigureAwait(false);
    }

    public StatusDto GetStatus()
    {
        var cfg = _store.Current;
        var peers = _node?.GetStatus() ?? [];
        var active = Coordinator.ActivePeer;
        var (incoming, outgoing) = PairingStatus();
        return new StatusDto
        {
            MachineId = cfg.MachineId,
            MachineName = cfg.MachineName,
            HasKey = GroupKey.TryParse(cfg.GroupKey, out _),
            Mode = Coordinator.Mode.ToString(),
            ActivePeerName = active is Guid a ? peers.FirstOrDefault(p => p.Id == a)?.Name : null,
            ListenPort = _node?.ListenPort ?? 0,
            Peers = peers.Select(p => new PeerDto
            {
                Id = p.Id,
                Name = p.Name,
                State = p.State.ToString(),
                Address = p.Address,
                RttMs = Math.Round(p.RttMs, 1),
                LastError = p.LastError,
            }).ToList(),
            Layout = Layout.Current.Entries,
            ManualAddresses = cfg.ManualAddresses.ToList(),
            Discovered = DiscoveredPeers(),
            IncomingPair = incoming,
            OutgoingPair = outgoing,
            Version = CurrentVersion.ToString(3),
            AutoUpdate = cfg.AutoUpdate,
            ShareClipboard = cfg.ShareClipboard,
            Update = Updater?.Status,
        };
    }

    public async ValueTask DisposeAsync()
    {
        Updater?.Dispose();
        await _desktopWatch.DisposeAsync().ConfigureAwait(false);
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
