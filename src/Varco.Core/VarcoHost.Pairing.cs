using System.Net;
using System.Net.Sockets;
using Varco.Core.Config;
using Varco.Core.Control;
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

    public static TimeSpan PairDecisionTimeout { get; set; } = TimeSpan.FromSeconds(60);

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
        lock (_pairGate)
        {
            if (_outgoing is { State: "Connecting" or "WaitingConfirm" })
                return new ControlResponse { Ok = false, Error = "C'è già un collegamento in corso." };
            if (id is Guid g && _seen.TryGetValue(g, out var s))
            {
                ep = s.Endpoint;
                name = s.Name;
            }
            else if (!string.IsNullOrWhiteSpace(address) && NetworkNode.TryParseEndpoint(address, _store.Current.TcpPort, out var parsed))
            {
                ep = parsed;
                name = address.Trim();
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
        _ = Task.Run(() => RunOutgoingPairing(ep, name, ct));
        return new ControlResponse();
    }

    private async Task RunOutgoingPairing(IPEndPoint ep, string name, CancellationToken ct)
    {
        try
        {
            using var socket = new Socket(ep.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            using (var connect = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                connect.CancelAfter(4000);
                await socket.ConnectAsync(ep, connect.Token).ConfigureAwait(false);
            }

            PairingSession session;
            using (var hs = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                hs.CancelAfter(5000);
                session = await PairingProtocol.InitiateAsync(new NetworkStream(socket, ownsSocket: false), MyOffer(), hs.Token).ConfigureAwait(false);
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
            Finish("Failed", ex is SocketException ? $"{name} non è raggiungibile." : $"Collegamento non riuscito: {ex.Message}");
        }
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
