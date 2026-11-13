using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Varco.Core.Config;
using Varco.Core.Control;
using Varco.Core.Crypto;
using Varco.Core.Net;

namespace Varco.Core;

/// <summary>
/// Rilevamento dei PC nella rete e abbinamento con un clic:
/// chi avvia vede un codice, l'altro PC mostra lo stesso codice e chiede "Accetta / Rifiuta".
/// Il gruppo con più PC "vince": l'altro ne riceve la chiave in modo cifrato.
/// </summary>
public sealed partial class VarcoHost
{
    private sealed record IncomingRequest(Guid RequestId, string PeerName, string Code, TaskCompletionSource<bool> Decision);
    private sealed record Seen(string Name, IPEndPoint Endpoint, long At, bool SameGroup);

    private readonly object _pairGate = new();
    private readonly Dictionary<Guid, Seen> _seen = new();
    private IncomingRequest? _incoming;
    private PairProgressDto? _outgoing;
    private long _outgoingFinishedAt;
    private CancellationTokenSource? _outgoingCts;

    /// <summary>Abbinamento in uscita che aspetta di essere richiamato dall'altro PC (token inviato via UDP).</summary>
    private sealed record PendingCallback(byte[] Token, TaskCompletionSource<Stream> Stream);
    private PendingCallback? _callback;
    /// <summary>Richiamate già in corso o riuscite (token → istante), per non rispondere due volte alla stessa.</summary>
    private readonly Dictionary<string, long> _callbacksAnswered = new();

    /// <summary>Solo per i test: sostituisce l'invio UDP della richiesta di richiamata.</summary>
    internal Action<IPAddress, Guid, byte[]>? CallbackSenderForTests { get; set; }

    public static TimeSpan PairDecisionTimeout { get; set; } = TimeSpan.FromSeconds(60);
    /// <summary>Tempo massimo per aprire il canale di abbinamento (diretto o con richiamata).</summary>
    private static readonly TimeSpan PairConnectTimeout = TimeSpan.FromSeconds(10);

    private void OnAnnounced(Guid id, string name, IPEndPoint ep, bool sameGroup)
    {
        bool isNew;
        lock (_pairGate)
        {
            isNew = !_seen.TryGetValue(id, out var old) || !old.Endpoint.Address.Equals(ep.Address);
            _seen[id] = new Seen(name, ep, Environment.TickCount64, sameGroup);
        }
        if (isNew) Log.Info($"Visto {name} su {ep.Address}{(sameGroup ? " (già collegato)" : "")}");
    }

    private PairOffer MyOffer() =>
        new(_store.Current.MachineId, _store.Current.MachineName, _node?.ListenPort ?? _store.Current.TcpPort, _store.Current.KnownPeers.Count);

    private List<DiscoveredDto> DiscoveredPeers()
    {
        var known = _store.Current.KnownPeers.Select(p => p.Id).ToHashSet();
        var now = Environment.TickCount64;
        lock (_pairGate)
        {
            return _seen.Where(kv => !kv.Value.SameGroup && !known.Contains(kv.Key) && now - kv.Value.At < 8000)
                .Select(kv => new DiscoveredDto { Id = kv.Key, Name = kv.Value.Name, Address = kv.Value.Endpoint.Address.ToString() })
                .OrderBy(d => d.Name)
                .ToList();
        }
    }

    private (PairRequestDto? In, PairProgressDto? Out) PairingStatus()
    {
        lock (_pairGate)
        {
            // Il risultato finale resta visibile qualche secondo, poi sparisce.
            if (_outgoing is { State: "Done" or "Rejected" or "Failed" } && Environment.TickCount64 - _outgoingFinishedAt > 15_000)
                _outgoing = null;
            var inc = _incoming is null || _incoming.RequestId == Guid.Empty ? null : new PairRequestDto { RequestId = _incoming.RequestId, PeerName = _incoming.PeerName, Code = _incoming.Code };
            return (inc, _outgoing is null ? null : new PairProgressDto
            {
                PeerName = _outgoing.PeerName, Code = _outgoing.Code, State = _outgoing.State, Message = _outgoing.Message,
            });
        }
    }

    // ═══════════════════════ lato di chi avvia ═══════════════════════

    private ControlResponse StartPairing(Guid? id, string? address)
    {
        IPEndPoint ep;
        string name;
        var target = Guid.Empty;
        string? manualAddress = null;
        lock (_pairGate)
        {
            if (_outgoing is { State: "Connecting" or "WaitingConfirm" })
                return new ControlResponse { Ok = false, Error = "C'è già un collegamento in corso." };
            if (id is Guid g && _seen.TryGetValue(g, out var s))
            {
                ep = s.Endpoint;
                name = s.Name;
                target = g;
            }
            else if (!string.IsNullOrWhiteSpace(address) && NetworkNode.TryParseEndpoint(address, _node?.ListenPort ?? _store.Current.TcpPort, out var parsed))
            {
                if (_node?.IsOwnEndpoint(parsed) == true)
                    return new ControlResponse { Ok = false, Error = "Questo è l'indirizzo di questo PC. Scrivi quello dell'altro PC: lo trovi in fondo alla finestra di Varco su quel PC." };
                ep = parsed;
                name = address.Trim();
                manualAddress = name;
            }
            else
            {
                return new ControlResponse { Ok = false, Error = "PC non trovato: controlla che sia acceso e sulla stessa rete." };
            }
            _outgoing = new PairProgressDto { PeerName = name, State = "Connecting" };
            _outgoingCts?.Dispose();
            _outgoingCts = new CancellationTokenSource(PairDecisionTimeout + TimeSpan.FromSeconds(20));
        }
        var ct = _outgoingCts.Token;
        _ = Task.Run(() => RunOutgoingPairing(ep, name, target, manualAddress, ct));
        return new ControlResponse();
    }

    private async Task RunOutgoingPairing(IPEndPoint ep, string name, Guid target, string? manualAddress, CancellationToken ct)
    {
        try
        {
            await using var stream = await OpenPairingStreamAsync(ep, target, ct).ConfigureAwait(false);

            PairingSession session;
            using (var hs = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                hs.CancelAfter(5000);
                session = await PairingProtocol.InitiateAsync(stream, MyOffer(), hs.Token).ConfigureAwait(false);
            }
            using (session)
            {
                SetOutgoing(o => { o.PeerName = session.Peer.Name; o.Code = session.Code; o.State = "WaitingConfirm"; });
                Log.Info($"Abbinamento con {session.Peer.Name}: codice {session.Code}, in attesa di conferma");

                var (accept, theirKey) = PairingProtocol.DecodeDecision(await session.ReceiveAsync(ct).ConfigureAwait(false));
                if (!accept)
                {
                    Finish("Rejected", $"{session.Peer.Name} ha rifiutato il collegamento.");
                    return;
                }

                var peerEp = new IPEndPoint(ep.Address, session.Peer.ListenPort);
                if (theirKey is not null)
                {
                    await session.SendAsync([1], ct).ConfigureAwait(false);
                    await AdoptGroupAsync(theirKey, session.Peer, peerEp).ConfigureAwait(false);
                }
                else
                {
                    await session.SendAsync(CurrentKey(), ct).ConfigureAwait(false);
                    await session.ReceiveAsync(ct).ConfigureAwait(false); // conferma
                    AddPartner(session.Peer, peerEp);
                }
                // Collegato scrivendo l'indirizzo: lo ricordo, servirà se la ricerca automatica non lo trova.
                if (manualAddress is not null)
                    _store.Update(c => { if (!c.ManualAddresses.Contains(manualAddress)) c.ManualAddresses.Add(manualAddress); }, immediate: true);
                Finish("Done", $"Collegato a {session.Peer.Name}.");
                Log.Info($"Abbinato a {session.Peer.Name}");
            }
        }
        catch (OperationCanceledException)
        {
            Finish("Failed", ct.IsCancellationRequested ? "Nessuna risposta: sull'altro PC la richiesta non è stata confermata in tempo." : $"{name} non risponde.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Abbinamento con {name} fallito: {ex.Message}");
            Finish("Failed", ex is SocketException
                ? $"{name} non è raggiungibile: controlla che sia acceso, sulla stessa rete e con Varco aggiornato."
                : $"Collegamento non riuscito: {ex.Message}");
        }
    }

    /// <summary>
    /// Apre il canale verso l'altro PC. Prima prova a collegarsi direttamente; se dopo poco non ci riesce
    /// (es. il firewall del portatile sul Wi-Fi "Pubblico" blocca le connessioni in ingresso) gli chiede via UDP
    /// di aprire lui la connessione verso di noi. Vince la prima che arriva; l'abbinamento poi procede identico.
    /// </summary>
    private async Task<Stream> OpenPairingStreamAsync(IPEndPoint ep, Guid target, CancellationToken ct)
    {
        var token = RandomNumberGenerator.GetBytes(PairingProtocol.CallbackTokenSize);
        var callback = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_pairGate) _callback = new PendingCallback(token, callback);

        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        race.CancelAfter(PairConnectTimeout);
        var direct = ConnectDirectAsync(ep, race.Token);
        var asking = AskForCallbackAsync(ep.Address, target, token, race.Token);
        var timeout = Task.Delay(Timeout.Infinite, race.Token);
        Stream? winner = null;
        Exception? directError = null;
        try
        {
            var pending = new List<Task> { direct, callback.Task, timeout };
            while (winner is null)
            {
                var done = await Task.WhenAny(pending).ConfigureAwait(false);
                if (done == direct)
                {
                    if (direct.IsCompletedSuccessfully) { winner = direct.Result; break; }
                    directError = direct.Exception?.InnerException;
                    pending.Remove(direct);
                    Log.Info($"Connessione diretta a {ep} non riuscita ({directError?.Message}): aspetto che l'altro PC ci richiami");
                }
                else if (done == callback.Task)
                {
                    winner = callback.Task.Result;
                    Log.Info($"Canale di abbinamento con {ep.Address} aperto dall'altro PC (richiamata)");
                }
                else
                {
                    ct.ThrowIfCancellationRequested();
                    throw directError as SocketException ?? new SocketException((int)SocketError.TimedOut);
                }
            }
            return winner;
        }
        finally
        {
            race.Cancel();
            lock (_pairGate) if (ReferenceEquals(_callback?.Stream, callback)) _callback = null;
            // Il canale perdente (arrivato in contemporanea o in ritardo) va chiuso.
            if (!callback.TrySetCanceled() && callback.Task.IsCompletedSuccessfully && !ReferenceEquals(callback.Task.Result, winner))
                await callback.Task.Result.DisposeAsync().ConfigureAwait(false);
            _ = direct.ContinueWith(t => { if (t.IsCompletedSuccessfully && !ReferenceEquals(t.Result, winner)) t.Result.Dispose(); }, TaskScheduler.Default);
            try { await asking.ConfigureAwait(false); } catch { }
        }
    }

    private static async Task<Stream> ConnectDirectAsync(IPEndPoint ep, CancellationToken ct)
    {
        var socket = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            using var connect = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connect.CancelAfter(4000);
            await socket.ConnectAsync(ep, connect.Token).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Se la connessione diretta tarda, ripete la richiesta di richiamata finché qualcosa non arriva.</summary>
    private async Task AskForCallbackAsync(IPAddress address, Guid target, byte[] token, CancellationToken ct)
    {
        try
        {
            await Task.Delay(800, ct).ConfigureAwait(false);
            while (true)
            {
                if (CallbackSenderForTests is { } send) send(address, target, token);
                else _node?.SendPairCallback(address, target, token);
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    // ═══════════════════════ richiamata: lato di chi la riceve ═══════════════════════

    /// <summary>Un PC non riesce a raggiungerci e chiede che siamo noi ad aprire la connessione di abbinamento.</summary>
    internal void OnCallbackRequested(Guid from, IPEndPoint tcpEndpoint, byte[] token)
    {
        var key = Convert.ToHexString(token);
        var now = Environment.TickCount64;
        lock (_pairGate)
        {
            if (_incoming is not null) return; // sto già gestendo una richiesta
            foreach (var old in _callbacksAnswered.Where(kv => now - kv.Value > 30_000).Select(kv => kv.Key).ToList())
                _callbacksAnswered.Remove(old);
            if (_callbacksAnswered.ContainsKey(key) || _callbacksAnswered.Count >= 16) return;
            _callbacksAnswered[key] = now;
        }
        Log.Info($"{tcpEndpoint.Address} chiede di essere richiamato per l'abbinamento");
        _ = Task.Run(() => AnswerCallbackAsync(tcpEndpoint, token, key));
    }

    private async Task AnswerCallbackAsync(IPEndPoint ep, byte[] token, string key)
    {
        var socket = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        NetworkStream? stream = null;
        try
        {
            using (var t = new CancellationTokenSource(5000))
            {
                await socket.ConnectAsync(ep, t.Token).ConfigureAwait(false);
                stream = new NetworkStream(socket, ownsSocket: true);
                await Framing.WriteFrameAsync(stream, PairingProtocol.EncodeCallback(token), t.Token).ConfigureAwait(false);
                var hello = await Framing.ReadFrameAsync(stream, t.Token).ConfigureAwait(false);
                if (!PairingProtocol.IsPairingFrame(hello)) throw new InvalidDataException("risposta inattesa");
                var owned = stream;
                stream = null; // da qui in poi lo stream è della richiesta di abbinamento
                await HandlePairingRequestAsync(owned, hello, ep).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Log.Info($"Richiamata verso {ep} non riuscita: {(ex is OperationCanceledException ? "timeout" : ex.Message)}");
            lock (_pairGate) _callbacksAnswered.Remove(key); // alla prossima richiesta si riprova
            if (stream is not null) await stream.DisposeAsync().ConfigureAwait(false);
            else socket.Dispose();
        }
    }

    /// <summary>Connessione di abbinamento in ingresso: una nuova richiesta, oppure l'altro PC che ci richiama.</summary>
    private async Task OnPairingConnectionAsync(Stream stream, byte[] first, IPEndPoint remote)
    {
        if (!PairingProtocol.IsCallbackFrame(first))
        {
            await HandlePairingRequestAsync(stream, first, remote).ConfigureAwait(false);
            return;
        }
        var token = PairingProtocol.DecodeCallback(first);
        lock (_pairGate)
        {
            if (_callback is { } cb && CryptographicOperations.FixedTimeEquals(cb.Token, token) && cb.Stream.TrySetResult(stream))
                return; // lo stream ora appartiene all'abbinamento in uscita
        }
        Log.Debug($"Richiamata da {remote} non attesa: ignorata");
        await stream.DisposeAsync().ConfigureAwait(false);
    }

    private void SetOutgoing(Action<PairProgressDto> change)
    {
        lock (_pairGate) if (_outgoing is not null) change(_outgoing);
    }

    private void Finish(string state, string message)
    {
        lock (_pairGate)
        {
            _outgoing ??= new PairProgressDto();
            _outgoing.State = state;
            _outgoing.Message = message;
            _outgoingFinishedAt = Environment.TickCount64;
        }
    }

    private void CancelPairing()
    {
        lock (_pairGate)
        {
            _outgoingCts?.Cancel();
            if (_outgoing is { State: "Done" or "Rejected" or "Failed" }) _outgoing = null;
        }
    }

    // ═══════════════════════ lato di chi riceve la richiesta ═══════════════════════

    private async Task HandlePairingRequestAsync(Stream stream, byte[] first, IPEndPoint remote)
    {
        PairingSession? session = null;
        IncomingRequest? request = null;
        try
        {
            lock (_pairGate)
            {
                if (_incoming is not null) return; // una richiesta alla volta
                request = new IncomingRequest(Guid.Empty, "", "", new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously));
                _incoming = request; // segnaposto: blocca richieste concorrenti durante l'handshake
            }

            using (var hs = new CancellationTokenSource(5000))
                session = await PairingProtocol.RespondAsync(stream, first, MyOffer(), hs.Token).ConfigureAwait(false);

            lock (_pairGate)
            {
                request = request with { RequestId = Guid.NewGuid(), PeerName = session.Peer.Name, Code = session.Code };
                _incoming = request;
            }
            Log.Info($"{session.Peer.Name} ({remote.Address}) chiede di collegarsi, codice {session.Code}");

            bool accept;
            try { accept = await request.Decision.Task.WaitAsync(PairDecisionTimeout).ConfigureAwait(false); }
            catch (TimeoutException) { accept = false; }
            lock (_pairGate) _incoming = null;

            using var io = new CancellationTokenSource(10_000);
            if (!accept)
            {
                await session.SendAsync(PairingProtocol.EncodeDecision(false, null), io.Token).ConfigureAwait(false);
                Log.Info($"Collegamento con {session.Peer.Name} rifiutato");
                return;
            }

            var peerEp = new IPEndPoint(remote.Address, session.Peer.ListenPort);
            var myGroupWins = _store.Current.KnownPeers.Count > session.Peer.PeerCount;
            if (myGroupWins)
            {
                await session.SendAsync(PairingProtocol.EncodeDecision(true, CurrentKey()), io.Token).ConfigureAwait(false);
                await session.ReceiveAsync(io.Token).ConfigureAwait(false); // conferma
                AddPartner(session.Peer, peerEp);
            }
            else
            {
                await session.SendAsync(PairingProtocol.EncodeDecision(true, null), io.Token).ConfigureAwait(false);
                var key = await session.ReceiveAsync(io.Token).ConfigureAwait(false);
                if (key.Length != GroupKey.KeyBytes) throw new InvalidDataException("chiave di gruppo non valida");
                await session.SendAsync([1], io.Token).ConfigureAwait(false);
                await AdoptGroupAsync(key, session.Peer, peerEp).ConfigureAwait(false);
            }
            Log.Info($"Abbinato a {session.Peer.Name}");
        }
        catch (Exception ex)
        {
            Log.Warn($"Richiesta di collegamento da {remote.Address} interrotta: {ex.Message}");
        }
        finally
        {
            lock (_pairGate) if (ReferenceEquals(_incoming, request) || _incoming?.RequestId == request?.RequestId) _incoming = null;
            session?.Dispose();
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private ControlResponse AnswerPairing(Guid? requestId, bool accept)
    {
        lock (_pairGate)
        {
            if (_incoming is null || _incoming.RequestId != requestId)
                return new ControlResponse { Ok = false, Error = "La richiesta non è più valida." };
            _incoming.Decision.TrySetResult(accept);
        }
        return new ControlResponse();
    }

    // ═══════════════════════ effetti dell'abbinamento ═══════════════════════

    private byte[] CurrentKey() =>
        GroupKey.TryParse(_store.Current.GroupKey, out var k) ? k : throw new InvalidOperationException("Chiave di gruppo mancante");

    /// <summary>Entro nel gruppo dell'altro PC: nuova chiave, lui come primo PC noto, rete riavviata.</summary>
    private Task AdoptGroupAsync(byte[] key, PairOffer peer, IPEndPoint ep)
    {
        _store.Update(c =>
        {
            c.GroupKey = GroupKey.Format(key);
            c.KnownPeers.Clear();
            c.KnownPeers.Add(new KnownPeer { Id = peer.Id, Name = peer.Name, LastAddresses = [ep.ToString()], LastSeenUtc = DateTime.UtcNow });
        }, immediate: true);
        Layout.Reset();
        // Il riavvio chiude anche la connessione su cui stiamo ancora lavorando: lo faccio subito dopo.
        _ = Task.Run(async () =>
        {
            await Task.Delay(300).ConfigureAwait(false);
            await RestartNetworkAsync().ConfigureAwait(false);
        });
        return Task.CompletedTask;
    }

    /// <summary>L'altro PC entra nel mio gruppo: lo registro e lo contatto subito.</summary>
    private void AddPartner(PairOffer peer, IPEndPoint ep)
    {
        _store.Update(c =>
        {
            var kp = c.KnownPeers.FirstOrDefault(p => p.Id == peer.Id);
            if (kp is null) c.KnownPeers.Add(kp = new KnownPeer { Id = peer.Id });
            kp.Name = peer.Name;
            kp.LastAddresses.Remove(ep.ToString());
            kp.LastAddresses.Insert(0, ep.ToString());
            kp.LastSeenUtc = DateTime.UtcNow;
        }, immediate: true);
        _node?.AddPeerHint(peer.Id, peer.Name, ep);
    }

    /// <summary>Dimentica un PC (es. dismesso): esce dai PC noti e dalla disposizione.</summary>
    private async Task ForgetAsync(Guid id)
    {
        _store.Update(c => c.KnownPeers.RemoveAll(p => p.Id == id), immediate: true);
        var remaining = Layout.Current.Entries.Where(e => e.Id != id).OrderBy(e => e.Y).ThenBy(e => e.X).Select(e => (e.Id, e.Name)).ToList();
        Layout.SetRow(remaining);
        await RestartNetworkAsync().ConfigureAwait(false);
    }
}
