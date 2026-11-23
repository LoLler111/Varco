using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Varco.Core.Crypto;
using Varco.Core.Files;
using Varco.Core.Protocol;

namespace Varco.Core.Net;

public enum PeerState { Disconnected, Connecting, Connected }

public sealed record PeerStatus(Guid? Id, string Name, PeerState State, string? Address, double RttMs, string? LastError)
{
    /// <summary>Indirizzo di questo PC usato dalla connessione.</summary>
    public IPEndPoint? LocalEndPoint { get; init; }
    public DateTime? ConnectedSinceUtc { get; init; }
    /// <summary>Connessioni cadute nelle ultime 24 ore (da quando l'agent è partito, se è partito da meno).</summary>
    public int Drops24h { get; init; }
    public string? LastDropReason { get; init; }
    public DateTime? LastDropUtc { get; init; }
    /// <summary>Collegato tramite un altro PC (inoltro): il suo nome. Null = collegamento diretto.</summary>
    public string? Via { get; init; }
}

public sealed class NodeOptions
{
    /// <summary>0 = porta scelta dal sistema (usato nei test).</summary>
    public int TcpPort { get; init; } = 47800;
    /// <summary>0 = discovery UDP disattivata.</summary>
    public int DiscoveryPort { get; init; } = 47801;
    public int HeartbeatIntervalMs { get; init; } = 500;
    public int HeartbeatTimeoutMs { get; init; } = 3000;
    public int ConnectTimeoutMs { get; init; } = 2000;
    public int HandshakeTimeoutMs { get; init; } = 3000;
    public int BackoffMinMs { get; init; } = 500;
    public int BackoffMaxMs { get; init; } = 5000;
    /// <summary>Due connessioni allo stesso peer nate entro questa finestra sono considerate una "gara" e si sceglie in modo deterministico.</summary>
    public int RaceWindowMs { get; init; } = 2000;
    public IReadOnlyList<string> ManualAddresses { get; init; } = [];
    public IReadOnlyList<(Guid Id, string Name, IReadOnlyList<string> Addresses)> KnownPeers { get; init; } = [];
    /// <summary>
    /// Chiavi di gruppo sostituite che un certo PC può ancora usare (vedi <see cref="Config.RetiredGroupKey"/>).
    /// Letta a ogni connessione: segue la configurazione senza ricreare il nodo.
    /// </summary>
    public Func<Guid, IReadOnlyList<byte[]>>? RetiredKeysFor { get; init; }
    /// <summary>
    /// Dopo quanto tempo senza collegamento diretto si prova a passare da un altro PC del gruppo (ms). 0 = mai.
    /// Il collegamento diretto si continua a cercare anche dopo, e appena riesce sostituisce l'inoltro.
    /// </summary>
    public int RelayAfterMs { get; init; } = 8000;
    /// <summary>Dopo un inoltro non riuscito, attesa prima di riprovare (ms).</summary>
    public int RelayRetryMs { get; init; } = 10_000;
}

/// <summary>
/// Gestisce tutte le connessioni verso gli altri PC del gruppo e le tiene in piedi da solo.
///
/// Regole di riconnessione:
///  • per ogni PC conosciuto c'è un "slot" con un ciclo che, se il PC non è connesso, prova tutti gli
///    indirizzi noti in parallelo (ultimo beacon, ultimi indirizzi riusciti, indirizzi manuali);
///  • backoff 0,5 s → 5 s massimo, ma qualsiasi segnale (beacon ricevuto, cambio rete, risveglio,
///    caduta della connessione) azzera l'attesa e fa ritentare subito;
///  • entrambi i lati compongono: non conta chi torna online per primo;
///  • connessioni doppie: entro la finestra di gara vince quella aperta dall'ID più basso,
///    altrimenti vince la più recente (l'altro lato l'ha aperta perché la vecchia era morta).
/// </summary>
public interface IPeerNetwork
{
    Guid SelfId { get; }
    bool IsConnected(Guid id);
    bool Send(Guid id, Message m);
    void Broadcast(Message m);
}

public sealed class NetworkNode : IPeerNetwork, IAsyncDisposable
{
    public Guid SelfId => Me.Id;

    private readonly NodeOptions _opt;
    private readonly Socket _listener;
    private readonly Discovery? _discovery;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, PeerConnection> _conns = new();
    private readonly Dictionary<string, PeerSlot> _slots = new();
    private readonly Dictionary<Guid, long> _lastReply = new();
    private bool _started;

    // Inoltro (vedi RelayTunnel). Tunnel di cui questo PC è un capo, tunnel che questo PC gira da un PC all'altro
    // (id → i due capi), PC collegati direttamente a ciascun PC (dai loro PeerList), PC per cui è in corso un tentativo.
    private readonly Dictionary<Guid, RelayTunnel> _tunnels = new();
    private readonly Dictionary<Guid, (Guid A, Guid B)> _relays = new();
    private readonly Dictionary<Guid, HashSet<Guid>> _peerLists = new();
    private readonly HashSet<Guid> _relayAttempts = new();

    public LocalIdentity Me { get; }
    public int ListenPort { get; }

    public event Action<PeerConnection>? PeerConnected;
    public event Action<Guid, string>? PeerDisconnected;
    public event Action<Guid, Message>? MessageReceived;
    /// <summary>Un PC con Varco (anche di un altro gruppo) si è annunciato: (id, nome, endpoint TCP, stesso gruppo?).</summary>
    public event Action<Guid, string, IPEndPoint, bool>? Announced;

    /// <summary>Un PC chiede di essere richiamato per l'abbinamento: (suo id, suo endpoint TCP, token).</summary>
    public event Action<Guid, IPEndPoint, byte[]>? CallbackRequested;

    /// <summary>
    /// Gestore delle connessioni di abbinamento in ingresso (richiesta, o risposta a una nostra richiamata):
    /// riceve lo stream (ne diventa proprietario), il primo frame e l'indirizzo remoto.
    /// </summary>
    public Func<Stream, byte[], IPEndPoint, Task>? PairingRequested { get; set; }

    /// <summary>Apre i file offerti da questo PC quando un altro del gruppo li incolla (null = nessun file condiviso).</summary>
    public FileOpener? FileProvider { get; set; }

    /// <summary>Un peer è stato raggiunto a questo indirizzo: da salvare in config per la prossima volta.</summary>
    public event Action<Guid, string, string>? PeerLearned;

    public NetworkNode(Guid id, string name, byte[] groupKey, NodeOptions options)
    {
        _opt = options;
        _listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
        _listener.Bind(new IPEndPoint(IPAddress.IPv6Any, options.TcpPort));
        _listener.Listen(16);
        ListenPort = ((IPEndPoint)_listener.LocalEndPoint!).Port;
        Me = new LocalIdentity(id, name, ListenPort, groupKey)
        {
            RetiredKeyFor = (tag, peer) => RetiredKeysFor(peer).FirstOrDefault(k =>
                System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Config.GroupKey.Tag(k), tag)),
        };

        foreach (var kp in options.KnownPeers)
        {
            var slot = GetOrCreateSlot(kp.Id, kp.Name);
            foreach (var a in kp.Addresses)
                if (TryParseEndpoint(a, ListenPortFor(a), out var ep)) slot.AddEndpoint(ep, front: false);
        }
        foreach (var a in options.ManualAddresses)
        {
            if (!TryParseEndpoint(a, options.TcpPort == 0 ? 47800 : options.TcpPort, out var ep)) continue;
            if (IsOwnEndpoint(ep))
            {
                Log.Info($"Indirizzo manuale {a} ignorato: è questo PC");
                continue;
            }
            var slot = new PeerSlot(null, a);
            slot.AddEndpoint(ep, front: true);
            _slots["addr:" + a] = slot;
        }

        if (options.DiscoveryPort > 0)
        {
            _discovery = new Discovery(Me, options.DiscoveryPort, ManualDiscoveryTargets);
            _discovery.PeerSeen += OnPeerSeen;
            _discovery.Announced += (id, name, ep, same) => Announced?.Invoke(id, name, ep, same);
            _discovery.CallbackRequested += (id, ep, token) => CallbackRequested?.Invoke(id, ep, token);
        }
    }

    private IReadOnlyList<byte[]> RetiredKeysFor(Guid peer)
    {
        try { return _opt.RetiredKeysFor?.Invoke(peer) ?? []; }
        catch (Exception ex) { Log.Error("Chiavi sostituite", ex); return []; }
    }

    private int ListenPortFor(string _) => _opt.TcpPort == 0 ? 47800 : _opt.TcpPort;

    public void Start()
    {
        lock (_gate)
        {
            if (_started) return;
            _started = true;
            foreach (var s in _slots.Values) StartSlot(s);
        }
        _ = Task.Run(AcceptLoop);
        if (_opt.RelayAfterMs > 0) _ = Task.Run(RelayLoop);
        _discovery?.Start();
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
        Log.Info($"Nodo in ascolto su TCP {ListenPort}, discovery UDP {(_opt.DiscoveryPort > 0 ? _opt.DiscoveryPort : "off")}");
    }

    // ───────────────────────────── API pubblica ─────────────────────────────

    public bool IsConnected(Guid id)
    {
        lock (_gate) return _conns.ContainsKey(id);
    }

    /// <summary>Collegato senza passare da un altro PC?</summary>
    public bool IsDirectlyConnected(Guid id)
    {
        lock (_gate) return _conns.TryGetValue(id, out var c) && !c.IsRelayed;
    }

    /// <summary>Collegato tramite un altro PC: il suo id (null se diretto o non collegato).</summary>
    public Guid? RelayedVia(Guid id)
    {
        lock (_gate) return _conns.TryGetValue(id, out var c) ? c.Via?.PeerId : null;
    }

    /// <summary>Chiave di gruppo con cui è nata la connessione attuale verso quel PC (null se non è collegato).</summary>
    public byte[]? GroupKeyOf(Guid id)
    {
        lock (_gate) return _conns.TryGetValue(id, out var c) ? c.GroupKey : null;
    }

    public IReadOnlyList<Guid> ConnectedPeers
    {
        get { lock (_gate) return _conns.Keys.ToList(); }
    }

    public bool Send(Guid id, Message m)
    {
        PeerConnection? c;
        lock (_gate) _conns.TryGetValue(id, out c);
        if (c is null) return false;
        c.Send(m);
        return true;
    }

    public void Broadcast(Message m)
    {
        List<PeerConnection> all;
        lock (_gate) all = _conns.Values.ToList();
        foreach (var c in all) c.Send(m);
    }

    /// <summary>Fa ritentare subito tutti i PC non connessi e annuncia la nostra presenza.</summary>
    public void Kick(string reason)
    {
        Log.Info($"Riconnessione immediata: {reason}");
        List<PeerSlot> slots;
        lock (_gate) slots = _slots.Values.ToList();
        foreach (var s in slots) s.Kick(_opt.BackoffMinMs);
        _discovery?.Announce();
    }

    /// <summary>Aggiunge (o aggiorna) un PC del gruppo da contattare subito, es. appena abbinato.</summary>
    public void AddPeerHint(Guid id, string name, IPEndPoint endpoint)
    {
        var slot = GetOrCreateSlot(id, name);
        slot.AddEndpoint(endpoint, front: true);
        slot.Kick(_opt.BackoffMinMs);
        _discovery?.Announce();
    }

    /// <summary>Scarica l'elemento <paramref name="index"/> di un'offerta di file del PC <paramref name="peer"/> (deve essere connesso).</summary>
    /// <remarks>
    /// Prima prova a collegarsi direttamente. Se non ci riesce (il firewall dell'altro PC blocca le connessioni in ingresso,
    /// anche se quella di mouse e tastiera l'ha aperta lui) gli chiede di collegarsi lui: vedi <see cref="FileDialBack"/>.
    /// </remarks>
    public async Task<FileTransfer.RemoteFile> OpenFileAsync(Guid peer, Guid offerId, int index, long offset, CancellationToken ct)
    {
        PeerConnection? c;
        lock (_gate) _conns.TryGetValue(peer, out c);
        if (c is null) throw new IOException("il PC che ha copiato i file non è collegato");
        if (c.IsRelayed)
            throw new IOException($"{c.PeerName} è collegato tramite {c.Via!.PeerName}: i file passano solo fra PC che si vedono direttamente");
        if (!c.FilesNeedDialBack)
        {
            var ep = new IPEndPoint(Normalize(c.RemoteEndPoint.Address), c.PeerListenPort);
            try
            {
                if (TestFirewallOnPeers) throw new FileTransfer.PeerUnreachableException("impossibile raggiungere l'altro PC (test)");
                var direct = await FileTransfer.ConnectAsync(ep, _opt.ConnectTimeoutMs, ct).ConfigureAwait(false);
                return await FileTransfer.RequestAsync(direct, sendMagic: true, Me, peer, offerId, index, offset, _opt.HandshakeTimeoutMs, ct).ConfigureAwait(false);
            }
            catch (FileTransfer.PeerUnreachableException ex)
            {
                Log.Info($"File da {c.PeerName}: {ex.Message}, gli chiedo di collegarsi lui");
                c.FilesNeedDialBack = true;
            }
        }
        var back = await AwaitFileDialBackAsync(c, ct).ConfigureAwait(false);
        return await FileTransfer.RequestAsync(back, sendMagic: false, Me, peer, offerId, index, offset, _opt.HandshakeTimeoutMs, ct).ConfigureAwait(false);
    }

    private readonly Dictionary<Guid, TaskCompletionSource<Stream>> _fileDialBacks = new();

    /// <summary>Solo test: le connessioni dirette per i file verso gli altri PC "non passano", come col loro firewall.</summary>
    internal bool TestFirewallOnPeers { get; set; }

    /// <summary>Manda <see cref="FileDialBack"/> e aspetta che l'altro PC apra la connessione per i file verso di noi.</summary>
    private async Task<Stream> AwaitFileDialBackAsync(PeerConnection c, CancellationToken ct)
    {
        var token = new Guid(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
        var arrived = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate) _fileDialBacks[token] = arrived;
        try
        {
            c.Send(new FileDialBack(token));
            using var t = CancellationTokenSource.CreateLinkedTokenSource(ct);
            t.CancelAfter(_opt.ConnectTimeoutMs + _opt.HandshakeTimeoutMs);
            using (t.Token.Register(() => arrived.TrySetCanceled()))
            {
                try { return await arrived.Task.ConfigureAwait(false); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    c.FilesNeedDialBack = false; // la prossima volta si riprova anche la via diretta
                    throw new IOException($"{c.PeerName} non è raggiungibile per i file né può collegarsi lui a questo PC: " +
                                          "controlla che abbia la stessa versione di Varco e che il firewall non la blocchi");
                }
            }
        }
        finally
        {
            lock (_gate) _fileDialBacks.Remove(token);
        }
    }

    /// <summary>Un PC che incolla i nostri file non riesce a collegarsi: ci colleghiamo noi a lui.</summary>
    private void OnFileDialBack(PeerConnection c, Guid token)
    {
        if (c.IsRelayed) return; // non può raggiungerci direttamente, e noi nemmeno lui
        var ep = new IPEndPoint(Normalize(c.RemoteEndPoint.Address), c.PeerListenPort);
        Log.Info($"{c.PeerName} non riesce a collegarsi per i file: mi collego io a {ep}");
        _ = Task.Run(async () =>
        {
            try
            {
                await FileTransfer.DialBackAsync(ep, token, Me, c.PeerId, FileProvider, ThrottleMs, _opt.ConnectTimeoutMs,
                    _opt.HandshakeTimeoutMs, _cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidDataException
                                           or HandshakeException or System.Security.Cryptography.CryptographicException)
            {
                Log.Info($"File per {c.PeerName} (richiamata) non inviato: {ex.Message}");
            }
        });
    }

    /// <summary>
    /// Mentre si usa il mouse verso quel PC, i file rallentano se la latenza sale: prima il cursore, poi la copia.
    /// Senza mouse in uso (o con latenza bassa, es. via cavo) vanno alla massima velocità.
    /// </summary>
    private int ThrottleMs(Guid peer)
    {
        PeerConnection? c;
        lock (_gate) _conns.TryGetValue(peer, out c);
        if (c is null || !c.InputActive) return 0;
        return c.RttMs switch { > 40 => 20, > 15 => 4, _ => 0 };
    }

    /// <summary>Chiede a un PC di aprire lui la connessione di abbinamento verso di noi (vedi <see cref="Discovery.SendCallback"/>).</summary>
    public void SendPairCallback(IPAddress address, Guid target, byte[] token) => _discovery?.SendCallback(address, target, token);

    /// <summary>L'endpoint è questo stesso PC (un suo indirizzo, o loopback, sulla nostra porta)?</summary>
    public bool IsOwnEndpoint(IPEndPoint ep)
    {
        if (ep.Port != ListenPort) return false;
        var a = Normalize(ep.Address);
        return IPAddress.IsLoopback(a) || a.Equals(IPAddress.Any) || Discovery.LocalNetworks().Any(n => n.Address.Equals(a));
    }

    /// <summary>Chiude tutte le connessioni e le riapre (es. dopo il risveglio dallo standby: meglio non fidarsi dei socket vecchi).</summary>
    public void ResetAll(string reason)
    {
        List<PeerConnection> all;
        lock (_gate) all = _conns.Values.ToList();
        foreach (var c in all) c.Close(reason);
        Kick(reason);
    }

    public IReadOnlyList<PeerStatus> GetStatus()
    {
        lock (_gate)
        {
            var result = new List<PeerStatus>();
            var resolvedIds = new HashSet<Guid>();
            foreach (var s in _slots.Values.Where(s => s.Id is not null))
            {
                var id = s.Id!.Value;
                resolvedIds.Add(id);
                var (drops, lastReason, lastAt) = s.DropStats();
                var status = _conns.TryGetValue(id, out var c)
                    ? new PeerStatus(id, c.PeerName, PeerState.Connected, c.Via is { } via ? $"tramite {via.PeerName}" : c.RemoteEndPoint.ToString(), c.RttMs, null)
                    {
                        LocalEndPoint = c.Via?.LocalEndPoint ?? c.LocalEndPoint,
                        ConnectedSinceUtc = c.EstablishedUtc,
                        Via = c.Via?.PeerName,
                    }
                    : new PeerStatus(id, s.Name, s.Connecting ? PeerState.Connecting : PeerState.Disconnected, null, -1, s.LastError);
                result.Add(status with { Drops24h = drops, LastDropReason = lastReason, LastDropUtc = lastAt });
            }
            foreach (var s in _slots.Values.Where(s => s.Id is null && s.ResolvedId is null))
                result.Add(new PeerStatus(null, s.Name, s.Connecting ? PeerState.Connecting : PeerState.Disconnected, null, -1, s.LastError));
            return result;
        }
    }

    // ───────────────────────────── accettazione ─────────────────────────────

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket s;
            try { s = await _listener.AcceptAsync(_cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex)
            {
                Log.Warn($"Accept: {ex.SocketErrorCode}");
                await Task.Delay(200).ConfigureAwait(false);
                continue;
            }
            _ = Task.Run(() => HandleIncoming(s));
        }
    }

    private async Task HandleIncoming(Socket s)
    {
        ConfigureSocket(s);
        var remote = s.RemoteEndPoint?.ToString();
        var stream = new NetworkStream(s, ownsSocket: true);
        try
        {
            using var t = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            t.CancelAfter(_opt.HandshakeTimeoutMs);
            var first = await Framing.ReadFrameAsync(stream, t.Token).ConfigureAwait(false);
            if (PairingProtocol.IsPairingFrame(first) || PairingProtocol.IsCallbackFrame(first))
            {
                // Richiesta di abbinamento: la gestisce l'host (serve la conferma dell'utente, niente timeout di handshake).
                var handler = PairingRequested;
                var remoteEp = (IPEndPoint)s.RemoteEndPoint!;
                if (handler is null) { await stream.DisposeAsync().ConfigureAwait(false); return; }
                await handler(stream, first, new IPEndPoint(Normalize(remoteEp.Address), remoteEp.Port)).ConfigureAwait(false);
                return;
            }
            if (FileTransfer.TryReadDialBack(first, out var token))
            {
                // L'altro PC risponde alla nostra richiesta di richiamata: lo stream va a chi sta aspettando il file.
                TaskCompletionSource<Stream>? waiting;
                lock (_gate) _fileDialBacks.Remove(token, out waiting);
                if (waiting is null || !waiting.TrySetResult(stream)) await stream.DisposeAsync().ConfigureAwait(false);
                return;
            }
            if (FileTransfer.IsFileFrame(first))
            {
                // Trasferimento di un file incollato su un altro PC: connessione a parte, niente timeout oltre l'handshake.
                await using (stream)
                    await FileTransfer.ServeAsync(stream, Me, FileProvider, ThrottleMs, _opt.HandshakeTimeoutMs, _cts.Token).ConfigureAwait(false);
                return;
            }
            var hs = await Handshake.RunServerAsync(stream, Me, t.Token, first).ConfigureAwait(false);
            var conn = new PeerConnection(s, stream, hs, initiatorId: hs.PeerId, _opt.HeartbeatIntervalMs, _opt.HeartbeatTimeoutMs);
            var learned = new IPEndPoint(Normalize(conn.RemoteEndPoint.Address), conn.PeerListenPort);
            Register(conn, learned, viaSlot: null);
        }
        catch (Exception ex)
        {
            Log.Debug($"Handshake in ingresso da {remote} fallito: {ex.Message}");
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    // ───────────────────────────── slot / composizione ─────────────────────────────

    private sealed class PeerSlot(Guid? id, string name)
    {
        private readonly object _gate = new();
        private readonly List<IPEndPoint> _endpoints = new();
        private readonly SemaphoreSlim _signal = new(0, 1);

        public Guid? Id { get; } = id;
        /// <summary>Per gli slot "indirizzo manuale": l'ID scoperto dopo il primo handshake.</summary>
        public Guid? ResolvedId { get; set; }
        public string Name { get; set; } = name;
        public bool Connecting { get; set; }
        public string? LastError { get; set; }
        public int BackoffMs { get; set; }
        public bool Started { get; set; }
        /// <summary>Da quando non c'è collegamento con questo PC (TickCount64): serve a decidere quando passare da un altro PC.</summary>
        public long DownSince { get; set; } = Environment.TickCount64;
        /// <summary>Prossimo tentativo di inoltro possibile (dopo uno non riuscito).</summary>
        public long NextRelayAt { get; set; }
        private readonly Queue<DateTime> _drops = new();
        private string? _lastDropReason;
        private DateTime? _lastDropUtc;

        /// <summary>La connessione con questo PC è caduta (per la diagnostica).</summary>
        public void RecordDrop(string reason)
        {
            lock (_gate)
            {
                _drops.Enqueue(DateTime.UtcNow);
                _lastDropReason = reason;
                _lastDropUtc = DateTime.UtcNow;
                Trim();
            }
        }

        public (int Count, string? LastReason, DateTime? LastAt) DropStats()
        {
            lock (_gate)
            {
                Trim();
                return (_drops.Count, _lastDropReason, _lastDropUtc);
            }
        }

        private void Trim()
        {
            while (_drops.Count > 0 && DateTime.UtcNow - _drops.Peek() > TimeSpan.FromHours(24)) _drops.Dequeue();
        }

        public void AddEndpoint(IPEndPoint ep, bool front)
        {
            lock (_gate)
            {
                var idx = _endpoints.FindIndex(e => e.Equals(ep));
                if (idx >= 0)
                {
                    if (!front || idx == 0) return;
                    _endpoints.RemoveAt(idx);
                }
                if (front) _endpoints.Insert(0, ep); else _endpoints.Add(ep);
                if (_endpoints.Count > 6) _endpoints.RemoveAt(_endpoints.Count - 1);
            }
        }

        public List<IPEndPoint> Endpoints() { lock (_gate) return _endpoints.ToList(); }

        public void Kick(int backoffMin)
        {
            BackoffMs = backoffMin;
            try { if (_signal.CurrentCount == 0) _signal.Release(); } catch (SemaphoreFullException) { }
        }

        public async Task WaitAsync(int ms, CancellationToken ct)
        {
            try { await _signal.WaitAsync(ms, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    private PeerSlot GetOrCreateSlot(Guid id, string name)
    {
        lock (_gate)
        {
            var key = id.ToString();
            if (!_slots.TryGetValue(key, out var slot))
            {
                slot = new PeerSlot(id, name);
                _slots[key] = slot;
                if (_started) StartSlot(slot);
            }
            else if (!string.IsNullOrEmpty(name))
            {
                slot.Name = name;
            }
            return slot;
        }
    }

    private void StartSlot(PeerSlot slot)
    {
        if (slot.Started) return;
        slot.Started = true;
        slot.BackoffMs = _opt.BackoffMinMs;
        _ = Task.Run(() => SlotLoop(slot));
    }

    private async Task SlotLoop(PeerSlot slot)
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            var target = slot.Id ?? slot.ResolvedId;
            if (target is Guid id && (IsDirectlyConnected(id) || (slot.Id is null && HasIdSlot(id))))
            {
                // Connesso (o uno slot dedicato gestisce già quel PC): aspetto un segnale. Se il collegamento passa da
                // un altro PC si continua a cercare quello diretto, che appena riesce lo sostituisce.
                await slot.WaitAsync(2000, ct).ConfigureAwait(false);
                continue;
            }

            var eps = slot.Endpoints();
            if (eps.Count == 0)
            {
                await slot.WaitAsync(5000, ct).ConfigureAwait(false);
                continue;
            }

            slot.Connecting = true;
            var conn = await TryConnectAny(slot, eps, ct).ConfigureAwait(false);
            slot.Connecting = false;

            if (conn is not null)
            {
                slot.LastError = null;
                slot.BackoffMs = _opt.BackoffMinMs;
                if (slot.Id is null) slot.ResolvedId = conn.PeerId;
                Register(conn, conn.RemoteEndPoint, slot);
                continue;
            }

            await slot.WaitAsync(slot.BackoffMs, ct).ConfigureAwait(false);
            slot.BackoffMs = Math.Min(slot.BackoffMs * 2, _opt.BackoffMaxMs);
        }
    }

    private bool HasIdSlot(Guid id)
    {
        lock (_gate) return _slots.ContainsKey(id.ToString());
    }

    /// <summary>Prova tutti gli indirizzi in parallelo; vince il primo handshake riuscito.</summary>
    private async Task<PeerConnection?> TryConnectAny(PeerSlot slot, List<IPEndPoint> eps, CancellationToken ct)
    {
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = eps.Select(ep => ConnectOne(slot, ep, race.Token)).ToList();
        PeerConnection? winner = null;
        while (tasks.Count > 0)
        {
            var done = await Task.WhenAny(tasks).ConfigureAwait(false);
            tasks.Remove(done);
            var c = await done.ConfigureAwait(false);
            if (c is null) continue;
            if (winner is null)
            {
                winner = c;
                race.Cancel();
            }
            else
            {
                c.Close("tentativo parallelo superfluo");
            }
        }
        return winner;
    }

    private async Task<PeerConnection?> ConnectOne(PeerSlot slot, IPEndPoint ep, CancellationToken ct)
    {
        var (conn, rejected) = await ConnectOne(slot, ep, Me, ct).ConfigureAwait(false);
        if (conn is not null || !rejected || slot.Id is not Guid id) return conn;
        // Il PC risponde ma non accetta la nostra chiave: forse era spento quando la chiave è cambiata e ha ancora quella vecchia.
        // Ci si presenta con la vecchia; appena collegato riceve la nuova (vedi VarcoHost.GroupKeys).
        foreach (var old in RetiredKeysFor(id))
        {
            (conn, _) = await ConnectOne(slot, ep, Me with { GroupKey = old }, ct).ConfigureAwait(false);
            if (conn is not null)
            {
                Log.Info($"{slot.Name} ha ancora la chiave di gruppo precedente: collegato con quella");
                return conn;
            }
        }
        return null;
    }

    /// <returns>La connessione, oppure null e se il PC ha risposto ma ha rifiutato l'handshake.</returns>
    private async Task<(PeerConnection?, bool Rejected)> ConnectOne(PeerSlot slot, IPEndPoint ep, LocalIdentity me, CancellationToken ct)
    {
        var s = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        ConfigureSocket(s);
        NetworkStream? stream = null;
        try
        {
            using (var t = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                t.CancelAfter(_opt.ConnectTimeoutMs);
                await s.ConnectAsync(ep, t.Token).ConfigureAwait(false);
            }
            stream = new NetworkStream(s, ownsSocket: true);
            using var h = CancellationTokenSource.CreateLinkedTokenSource(ct);
            h.CancelAfter(_opt.HandshakeTimeoutMs);
            var hs = await Handshake.RunClientAsync(stream, me, h.Token).ConfigureAwait(false);
            return (new PeerConnection(s, stream, hs, initiatorId: Me.Id, _opt.HeartbeatIntervalMs, _opt.HeartbeatTimeoutMs), false);
        }
        catch (Exception ex)
        {
            var msg = ex switch
            {
                HandshakeException he => he.Message,
                SocketException se => se.SocketErrorCode.ToString(),
                OperationCanceledException => ct.IsCancellationRequested ? null : "timeout",
                _ => ex.Message,
            };
            if (msg is not null && msg != slot.LastError && ReferenceEquals(me, Me))
            {
                slot.LastError = msg;
                Log.Info($"Connessione a {slot.Name} ({ep}) fallita: {msg}");
            }
            // Connesso ma chiuso durante l'handshake (chiave diversa: l'altro chiude senza rispondere).
            var rejected = stream is not null && !ct.IsCancellationRequested && ex is not OperationCanceledException;
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            else s.Dispose();
            return (null, rejected);
        }
    }

    // ───────────────────────────── registro connessioni ─────────────────────────────

    /// <param name="learned">Indirizzo a cui l'altro PC risponde; null per le connessioni tramite un altro PC.</param>
    private void Register(PeerConnection conn, IPEndPoint? learned, PeerSlot? viaSlot)
    {
        PeerConnection? replaced = null;
        bool accepted;
        lock (_gate)
        {
            if (_cts.IsCancellationRequested) { conn.Close("nodo chiuso"); return; }

            if (_conns.TryGetValue(conn.PeerId, out var existing))
            {
                // Il collegamento diretto vince sempre su quello tramite un altro PC.
                if (existing.IsRelayed != conn.IsRelayed) accepted = !conn.IsRelayed;
                else
                {
                    var inRace = conn.EstablishedAt - existing.EstablishedAt < _opt.RaceWindowMs;
                    accepted = inRace ? Rank(conn).CompareTo(Rank(existing)) < 0 : true;
                }
                if (accepted) replaced = existing;
            }
            else
            {
                accepted = true;
            }

            if (accepted) _conns[conn.PeerId] = conn;
        }

        if (!accepted)
        {
            Log.Debug($"Connessione doppia verso {conn.PeerName} scartata");
            conn.Close("connessione doppia");
            return;
        }

        var slot = GetOrCreateSlot(conn.PeerId, conn.PeerName);
        if (learned is not null) slot.AddEndpoint(learned, front: true);
        slot.LastError = null;

        conn.MessageReceived += (c, m) =>
        {
            if (!IsCurrent(c)) return;
            if (m is FileDialBack fd) { if (FileProvider is not null) OnFileDialBack(c, fd.Token); return; }
            if (OnRelayMessage(c, m)) return;
            MessageReceived?.Invoke(c.PeerId, m);
        };
        _ = conn.Closed.ContinueWith(t => OnConnectionClosed(conn, t.Result), TaskScheduler.Default);

        if (replaced is not null)
        {
            Log.Info($"Connessione verso {conn.PeerName} sostituita da una più recente");
            replaced.Close("sostituita");
            // Il peer potrebbe essersi riavviato: lo stato di input va azzerato.
            SafeInvoke(() => PeerDisconnected?.Invoke(conn.PeerId, "sostituita"));
        }

        if (conn.Via is { } via)
            Log.Info($"Connesso a {conn.PeerName} tramite {via.PeerName}: i due PC non si vedono direttamente " +
                     $"({(conn.InitiatorId == Me.Id ? "in uscita" : "in ingresso")})");
        else
            Log.Info($"Connesso a {conn.PeerName} {conn.RemoteEndPoint} ({(conn.InitiatorId == Me.Id ? "in uscita" : "in ingresso")})" +
                     (replaced?.IsRelayed == true ? ": di nuovo collegamento diretto" : ""));
        if (learned is not null) SafeInvoke(() => PeerLearned?.Invoke(conn.PeerId, conn.PeerName, learned.ToString()));
        SafeInvoke(() => PeerConnected?.Invoke(conn));
        conn.Start();
        if (!conn.IsRelayed || replaced is { IsRelayed: false }) AnnouncePeerList();
    }

    /// <summary>Ordine di preferenza in caso di gara: prima la connessione aperta dall'ID più basso, poi SessionId più basso.</summary>
    private (int, string) Rank(PeerConnection c)
    {
        var lower = Me.Id.CompareTo(c.PeerId) < 0 ? Me.Id : c.PeerId;
        return (c.InitiatorId == lower ? 0 : 1, Convert.ToHexString(c.SessionId));
    }

    private bool IsCurrent(PeerConnection c)
    {
        lock (_gate) return _conns.TryGetValue(c.PeerId, out var cur) && ReferenceEquals(cur, c);
    }

    private void OnConnectionClosed(PeerConnection conn, string reason)
    {
        bool wasCurrent;
        lock (_gate)
        {
            wasCurrent = _conns.TryGetValue(conn.PeerId, out var cur) && ReferenceEquals(cur, conn);
            if (wasCurrent) _conns.Remove(conn.PeerId);
        }
        CloseTunnelsOf(conn);
        if (!wasCurrent) return;
        if (!conn.IsRelayed) AnnouncePeerList();

        Log.Info($"Disconnesso da {conn.PeerName}: {reason}");
        SafeInvoke(() => PeerDisconnected?.Invoke(conn.PeerId, reason));
        if (!_cts.IsCancellationRequested)
        {
            var slot = GetOrCreateSlot(conn.PeerId, conn.PeerName);
            slot.RecordDrop(reason);
            slot.DownSince = Environment.TickCount64;
            slot.Kick(_opt.BackoffMinMs);
            _discovery?.Announce();
        }
    }

    // ───────────────────────────── inoltro tramite un altro PC ─────────────────────────────

    /// <summary>Manda a tutti i PC collegati direttamente l'elenco di quelli collegati direttamente a questo.</summary>
    private void AnnouncePeerList()
    {
        List<PeerConnection> direct;
        lock (_gate) direct = _conns.Values.Where(c => !c.IsRelayed).ToList();
        var msg = new PeerList(direct.Select(c => new PeerRef(c.PeerId, c.PeerName)).ToArray());
        foreach (var c in direct) c.Send(msg);
    }

    /// <summary>
    /// Ogni secondo: per ogni PC del gruppo senza collegamento da almeno <see cref="NodeOptions.RelayAfterMs"/>, se un
    /// PC collegato direttamente a questo lo è anche a lui, si passa da quello. Per non aprire due tunnel in parallelo
    /// prova solo il PC con l'id più basso; l'altro aspetta.
    /// </summary>
    private async Task RelayLoop()
    {
        var ct = _cts.Token;
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(1000, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            var now = Environment.TickCount64;
            List<(PeerSlot Slot, Guid Id, PeerConnection Carrier)> attempts = [];
            lock (_gate)
            {
                foreach (var slot in _slots.Values)
                {
                    if (slot.Id is not Guid id || _conns.ContainsKey(id) || _relayAttempts.Contains(id)) continue;
                    if (Me.Id.CompareTo(id) > 0 || now - slot.DownSince < _opt.RelayAfterMs || now < slot.NextRelayAt) continue;
                    var carrier = _conns.Values.FirstOrDefault(c => !c.IsRelayed && _peerLists.TryGetValue(c.PeerId, out var l) && l.Contains(id));
                    if (carrier is null) continue;
                    _relayAttempts.Add(id);
                    attempts.Add((slot, id, carrier));
                }
            }
            foreach (var (slot, id, carrier) in attempts) _ = Task.Run(() => TryRelayAsync(slot, id, carrier));
        }
    }

    private async Task TryRelayAsync(PeerSlot slot, Guid id, PeerConnection carrier)
    {
        var tunnel = new RelayTunnel(Guid.NewGuid(), carrier, ForgetTunnel);
        lock (_gate) _tunnels[tunnel.Id] = tunnel;
        try
        {
            Log.Info($"{slot.Name} non si vede direttamente da {(Environment.TickCount64 - slot.DownSince) / 1000} s: provo tramite {carrier.PeerName}");
            carrier.Send(new RelayOpen(tunnel.Id, id, Forwarded: false));
            using var t = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            t.CancelAfter(_opt.HandshakeTimeoutMs);
            var hs = await Handshake.RunClientAsync(tunnel, Me, t.Token).ConfigureAwait(false);
            if (hs.PeerId != id) throw new HandshakeException("ha risposto un altro PC");
            Register(new PeerConnection(tunnel, hs, initiatorId: Me.Id, _opt.HeartbeatIntervalMs, _opt.HeartbeatTimeoutMs), null, null);
        }
        catch (Exception ex)
        {
            tunnel.Dispose();
            slot.NextRelayAt = Environment.TickCount64 + _opt.RelayRetryMs;
            var why = ex switch
            {
                OperationCanceledException => "nessuna risposta",
                EndOfStreamException => $"{carrier.PeerName} non riesce a raggiungerlo",
                _ => ex.Message,
            };
            Log.Info($"Collegamento con {slot.Name} tramite {carrier.PeerName} non riuscito: {why}");
        }
        finally
        {
            lock (_gate) _relayAttempts.Remove(id);
        }
    }

    private void ForgetTunnel(RelayTunnel t)
    {
        lock (_gate)
            if (_tunnels.TryGetValue(t.Id, out var cur) && ReferenceEquals(cur, t)) _tunnels.Remove(t.Id);
    }

    /// <summary>
    /// Messaggi dell'inoltro, da qualunque connessione diretta: elenco dei PC collegati, tunnel da girare a un altro PC
    /// (questo PC fa da tramite) o tunnel di cui questo PC è un capo. Restituisce true se il messaggio era dell'inoltro.
    /// </summary>
    private bool OnRelayMessage(PeerConnection c, Message m)
    {
        switch (m)
        {
            case PeerList pl:
                if (c.IsRelayed) return true; // solo i collegamenti diretti fanno da tramite: niente catene
                lock (_gate) _peerLists[c.PeerId] = [.. pl.Connected.Select(p => p.Id)];
                // PC del gruppo mai visti da qui (collegati al gruppo tramite un altro PC mentre questo era spento):
                // li si conosce così, e se non si vedono direttamente ci si collega tramite chi li vede.
                foreach (var p in pl.Connected)
                    if (p.Id != Me.Id && !HasIdSlot(p.Id))
                    {
                        Log.Info($"{c.PeerName} è collegato a {p.Name}, che questo PC non conosceva");
                        GetOrCreateSlot(p.Id, p.Name);
                    }
                return true;

            case RelayOpen { Forwarded: false } ro:
                ForwardTunnel(c, ro);
                return true;

            case RelayOpen { Forwarded: true } ro:
                AcceptTunnel(c, ro);
                return true;

            case RelayData rd:
            {
                RelayTunnel? mine;
                PeerConnection? next = null;
                lock (_gate)
                {
                    if (_tunnels.TryGetValue(rd.Tunnel, out mine) && !ReferenceEquals(mine.Carrier, c)) mine = null;
                    if (mine is null && OtherEnd(rd.Tunnel, c.PeerId) is Guid other) _conns.TryGetValue(other, out next);
                }
                if (mine is not null) mine.Deliver(rd.Data);
                else if (next is { IsRelayed: false }) next.Send(rd);
                else if (!c.IsRelayed)
                {
                    lock (_gate) _relays.Remove(rd.Tunnel);
                    c.Send(new RelayClose(rd.Tunnel));
                }
                return true;
            }

            case RelayClose rc:
            {
                RelayTunnel? mine;
                PeerConnection? next = null;
                lock (_gate)
                {
                    if (_tunnels.TryGetValue(rc.Tunnel, out mine) && !ReferenceEquals(mine.Carrier, c)) mine = null;
                    if (mine is null && OtherEnd(rc.Tunnel, c.PeerId) is Guid other)
                    {
                        _relays.Remove(rc.Tunnel);
                        _conns.TryGetValue(other, out next);
                    }
                }
                mine?.RemoteClosed();
                next?.Send(rc);
                return true;
            }
        }
        return false;
    }

    /// <summary>L'altro capo di un tunnel che questo PC gira, se <paramref name="from"/> è uno dei due capi.</summary>
    private Guid? OtherEnd(Guid tunnel, Guid from) =>
        _relays.TryGetValue(tunnel, out var ends) ? ends.A == from ? ends.B : ends.B == from ? ends.A : null : null;

    /// <summary>Questo PC fa da tramite: un PC collegato chiede di raggiungerne un altro collegato.</summary>
    private void ForwardTunnel(PeerConnection from, RelayOpen ro)
    {
        PeerConnection? to = null;
        string? refuse = null;
        lock (_gate)
        {
            if (from.IsRelayed) refuse = "la richiesta arriva già tramite un altro PC";
            else if (ro.Peer == Me.Id) refuse = "chiede questo PC";
            else if (!_conns.TryGetValue(ro.Peer, out to) || to.IsRelayed) refuse = "non è collegato direttamente a questo PC";
            else if (!_peerLists.ContainsKey(ro.Peer)) refuse = "ha una versione di Varco senza inoltro";
            else if (_relays.ContainsKey(ro.Tunnel)) refuse = "tunnel già aperto";
            else _relays[ro.Tunnel] = (from.PeerId, ro.Peer);
        }
        if (refuse is not null)
        {
            Log.Info($"{from.PeerName} chiede di raggiungere {to?.PeerName ?? ro.Peer.ToString()[..8]} tramite questo PC: rifiutato ({refuse})");
            from.Send(new RelayClose(ro.Tunnel));
            return;
        }
        Log.Info($"{from.PeerName} e {to!.PeerName} non si vedono: i loro dati passano da questo PC");
        to.Send(new RelayOpen(ro.Tunnel, from.PeerId, Forwarded: true));
    }

    /// <summary>Un PC del gruppo vuole collegarsi a questo tramite <paramref name="carrier"/>: handshake sopra il tunnel.</summary>
    private void AcceptTunnel(PeerConnection carrier, RelayOpen ro)
    {
        if (carrier.IsRelayed || IsDirectlyConnected(ro.Peer))
        {
            carrier.Send(new RelayClose(ro.Tunnel));
            return;
        }
        var tunnel = new RelayTunnel(ro.Tunnel, carrier, ForgetTunnel);
        lock (_gate)
        {
            if (_tunnels.ContainsKey(ro.Tunnel)) { carrier.Send(new RelayClose(ro.Tunnel)); return; }
            _tunnels[ro.Tunnel] = tunnel;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                using var t = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                t.CancelAfter(_opt.HandshakeTimeoutMs);
                var hs = await Handshake.RunServerAsync(tunnel, Me, t.Token).ConfigureAwait(false);
                if (hs.PeerId != ro.Peer) throw new HandshakeException("si è presentato un PC diverso da quello annunciato");
                Register(new PeerConnection(tunnel, hs, initiatorId: hs.PeerId, _opt.HeartbeatIntervalMs, _opt.HeartbeatTimeoutMs), null, null);
            }
            catch (Exception ex)
            {
                Log.Debug($"Handshake tramite {carrier.PeerName} fallito: {ex.Message}");
                tunnel.Dispose();
            }
        });
    }

    /// <summary>
    /// La connessione si è chiusa: i tunnel che passavano da lei si chiudono, e gli altri capi dei tunnel che questo PC
    /// girava per lei lo sanno subito (senza aspettare il timeout dell'heartbeat).
    /// </summary>
    private void CloseTunnelsOf(PeerConnection conn)
    {
        List<RelayTunnel> mine;
        List<(Guid Tunnel, PeerConnection? Other)> relayed = [];
        lock (_gate)
        {
            mine = _tunnels.Values.Where(t => ReferenceEquals(t.Carrier, conn)).ToList();
            if (!conn.IsRelayed)
            {
                // L'elenco resta se nel frattempo è arrivata una connessione diretta nuova (lo manda lei).
                if (!_conns.TryGetValue(conn.PeerId, out var cur) || cur.IsRelayed) _peerLists.Remove(conn.PeerId);
                // I tunnel girati per lei passavano da questa connessione: il suo capo li ha già chiusi.
                foreach (var (id, ends) in _relays.Where(kv => kv.Value.A == conn.PeerId || kv.Value.B == conn.PeerId).ToList())
                {
                    _relays.Remove(id);
                    _conns.TryGetValue(ends.A == conn.PeerId ? ends.B : ends.A, out var other);
                    relayed.Add((id, other));
                }
            }
        }
        foreach (var t in mine) t.RemoteClosed();
        foreach (var (id, other) in relayed) other?.Send(new RelayClose(id));
    }

    // ───────────────────────────── eventi esterni ─────────────────────────────

    private void OnPeerSeen(Guid id, string name, IPEndPoint ep)
    {
        var slot = GetOrCreateSlot(id, name);
        slot.AddEndpoint(ep, front: true);
        if (IsDirectlyConnected(id)) return;

        slot.Kick(_opt.BackoffMinMs);
        // Rispondo in unicast (max 1/s per peer): così anche l'altro lato ci trova subito.
        var now = Environment.TickCount64;
        lock (_gate)
        {
            if (_lastReply.TryGetValue(id, out var last) && now - last < 1000) return;
            _lastReply[id] = now;
        }
        _discovery?.ReplyTo(ep.Address);
    }

    private void OnNetworkChanged(object? sender, EventArgs e) => Kick("indirizzi di rete cambiati");

    private void OnNetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) =>
        Kick(e.IsAvailable ? "rete disponibile" : "rete non disponibile");

    private IEnumerable<IPEndPoint> ManualDiscoveryTargets()
    {
        if (_opt.DiscoveryPort <= 0) yield break;
        foreach (var a in _opt.ManualAddresses)
            if (TryParseEndpoint(a, 0, out var ep)) yield return new IPEndPoint(ep.Address, _opt.DiscoveryPort);
    }

    // ───────────────────────────── utilità ─────────────────────────────

    private static void ConfigureSocket(Socket s)
    {
        s.NoDelay = true;
        try
        {
            // Rete di sicurezza oltre all'heartbeat applicativo.
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 5);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 1);
            s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
        catch { }
    }

    private static IPAddress Normalize(IPAddress a) => a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;

    public static bool TryParseEndpoint(string text, int defaultPort, out IPEndPoint ep)
    {
        ep = null!;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();
        if (IPEndPoint.TryParse(text, out var parsed))
        {
            ep = new IPEndPoint(Normalize(parsed.Address), parsed.Port == 0 ? defaultPort : parsed.Port);
            return ep.Port != 0;
        }
        // nome host (eventualmente con porta)
        var host = text;
        var port = defaultPort;
        var colon = text.LastIndexOf(':');
        if (colon > 0 && int.TryParse(text[(colon + 1)..], out var p)) { host = text[..colon]; port = p; }
        try
        {
            var addr = Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
            if (addr is null || port == 0) return false;
            ep = new IPEndPoint(addr, port);
            return true;
        }
        catch { return false; }
    }

    private static void SafeInvoke(Action a)
    {
        try { a(); }
        catch (Exception ex) { Log.Error("Errore in un gestore di eventi", ex); }
    }

    public async ValueTask DisposeAsync()
    {
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _cts.Cancel();
        try { _listener.Close(); } catch { }
        _discovery?.Dispose();
        List<PeerConnection> all;
        lock (_gate) { all = _conns.Values.ToList(); _conns.Clear(); }
        // Prima quelle tramite un altro PC: chiudono i loro tunnel mentre la connessione con lui è ancora aperta.
        foreach (var c in all.OrderBy(c => c.IsRelayed ? 0 : 1)) await c.DisposeAsync().ConfigureAwait(false);
    }
}
