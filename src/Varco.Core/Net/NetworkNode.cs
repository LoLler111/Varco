using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Varco.Core.Crypto;
using Varco.Core.Protocol;

namespace Varco.Core.Net;

public enum PeerState { Disconnected, Connecting, Connected }

public sealed record PeerStatus(Guid? Id, string Name, PeerState State, string? Address, double RttMs, string? LastError);

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

    /// <summary>Un peer è stato raggiunto a questo indirizzo: da salvare in config per la prossima volta.</summary>
    public event Action<Guid, string, string>? PeerLearned;

    public NetworkNode(Guid id, string name, byte[] groupKey, NodeOptions options)
    {
        _opt = options;
        _listener = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { DualMode = true };
        _listener.Bind(new IPEndPoint(IPAddress.IPv6Any, options.TcpPort));
        _listener.Listen(16);
        ListenPort = ((IPEndPoint)_listener.LocalEndPoint!).Port;
        Me = new LocalIdentity(id, name, ListenPort, groupKey);

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
                if (_conns.TryGetValue(id, out var c))
                    result.Add(new PeerStatus(id, c.PeerName, PeerState.Connected, c.RemoteEndPoint.ToString(), c.RttMs, null));
                else
                    result.Add(new PeerStatus(id, s.Name, s.Connecting ? PeerState.Connecting : PeerState.Disconnected, null, -1, s.LastError));
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
            if (target is Guid id && (IsConnected(id) || (slot.Id is null && HasIdSlot(id))))
            {
                // Connesso (o uno slot dedicato gestisce già quel PC): aspetto un segnale.
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
            var hs = await Handshake.RunClientAsync(stream, Me, h.Token).ConfigureAwait(false);
            return new PeerConnection(s, stream, hs, initiatorId: Me.Id, _opt.HeartbeatIntervalMs, _opt.HeartbeatTimeoutMs);
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
            if (msg is not null && msg != slot.LastError)
            {
                slot.LastError = msg;
                Log.Info($"Connessione a {slot.Name} ({ep}) fallita: {msg}");
            }
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            else s.Dispose();
            return null;
        }
    }

    // ───────────────────────────── registro connessioni ─────────────────────────────

    private void Register(PeerConnection conn, IPEndPoint learned, PeerSlot? viaSlot)
    {
        PeerConnection? replaced = null;
        bool accepted;
        lock (_gate)
        {
            if (_cts.IsCancellationRequested) { conn.Close("nodo chiuso"); return; }

            if (_conns.TryGetValue(conn.PeerId, out var existing))
            {
                var inRace = conn.EstablishedAt - existing.EstablishedAt < _opt.RaceWindowMs;
                accepted = inRace ? Rank(conn).CompareTo(Rank(existing)) < 0 : true;
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
        slot.AddEndpoint(learned, front: true);
        slot.LastError = null;

        conn.MessageReceived += (c, m) =>
        {
            if (IsCurrent(c)) MessageReceived?.Invoke(c.PeerId, m);
        };
        _ = conn.Closed.ContinueWith(t => OnConnectionClosed(conn, t.Result), TaskScheduler.Default);

        if (replaced is not null)
        {
            Log.Info($"Connessione verso {conn.PeerName} sostituita da una più recente");
            replaced.Close("sostituita");
            // Il peer potrebbe essersi riavviato: lo stato di input va azzerato.
            SafeInvoke(() => PeerDisconnected?.Invoke(conn.PeerId, "sostituita"));
        }

        Log.Info($"Connesso a {conn.PeerName} {conn.RemoteEndPoint} ({(conn.InitiatorId == Me.Id ? "in uscita" : "in ingresso")})");
        SafeInvoke(() => PeerLearned?.Invoke(conn.PeerId, conn.PeerName, learned.ToString()));
        SafeInvoke(() => PeerConnected?.Invoke(conn));
        conn.Start();
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
        if (!wasCurrent) return;

        Log.Info($"Disconnesso da {conn.PeerName}: {reason}");
        SafeInvoke(() => PeerDisconnected?.Invoke(conn.PeerId, reason));
        if (!_cts.IsCancellationRequested)
        {
            GetOrCreateSlot(conn.PeerId, conn.PeerName).Kick(_opt.BackoffMinMs);
            _discovery?.Announce();
        }
    }

    // ───────────────────────────── eventi esterni ─────────────────────────────

    private void OnPeerSeen(Guid id, string name, IPEndPoint ep)
    {
        var slot = GetOrCreateSlot(id, name);
        slot.AddEndpoint(ep, front: true);
        if (IsConnected(id)) return;

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
        foreach (var c in all) await c.DisposeAsync().ConfigureAwait(false);
    }
}
