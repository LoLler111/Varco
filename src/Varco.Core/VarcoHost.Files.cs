using Varco.Core.Control;
using Varco.Core.Files;

namespace Varco.Core;

/// <summary>
/// File copiati su un altro PC. L'agent (SYSTEM) non può fare da "sorgente" per Esplora risorse dell'utente,
/// quindi passa l'offerta all'icona (che gira come utente) e le fornisce i dati quando l'utente incolla.
/// </summary>
public sealed partial class VarcoHost
{
    /// <summary>Null se i file non si condividono (agent senza appunti o senza accesso ai file).</summary>
    public FileOffers? FileOffers { get; }

    private readonly object _filesGate = new();
    private FilesOfferDto? _lastOffer;
    private long _offerSeq;
    private TaskCompletionSource _offerChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void OnFilesOffered(Guid from, FileOffer offer)
    {
        var name = Layout.Current.Entries.FirstOrDefault(e => e.Id == from)?.Name ?? _store.Current.KnownPeers.FirstOrDefault(p => p.Id == from)?.Name ?? "";
        TaskCompletionSource fire;
        lock (_filesGate)
        {
            _lastOffer = new FilesOfferDto
            {
                Seq = ++_offerSeq,
                PeerId = from,
                PeerName = name,
                OfferId = offer.Id,
                Entries = offer.Entries.Select(e => new FileEntryDto
                {
                    Path = e.Path,
                    Size = e.Size,
                    Attributes = (uint)e.Attributes,
                    LastWriteUtc = e.LastWriteUtc,
                    IsDirectory = e.IsDirectory,
                }).ToList(),
            };
            fire = _offerChanged;
            _offerChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        fire.TrySetResult();
    }

    /// <summary>
    /// Attesa lunga per l'icona: risponde subito se c'è un'offerta più recente di <paramref name="seq"/>,
    /// altrimenti appena ne arriva una (o dopo 25 s). Con <paramref name="seq"/> nullo dice solo a che numero siamo.
    /// </summary>
    private async Task<FilesOfferDto> WaitFilesOfferAsync(long? seq)
    {
        Task changed;
        lock (_filesGate)
        {
            if (seq is null) return new FilesOfferDto { Seq = _offerSeq };
            if (_offerSeq > seq && _lastOffer is not null) return _lastOffer;
            changed = _offerChanged.Task;
        }
        await Task.WhenAny(changed, Task.Delay(25_000)).ConfigureAwait(false);
        lock (_filesGate) return _offerSeq > seq && _lastOffer is not null ? _lastOffer : new FilesOfferDto { Seq = _offerSeq };
    }

    /// <summary>Apre un file offerto da un altro PC (lo usa la pipe dei file per l'icona).</summary>
    public Task<FileTransfer.RemoteFile> OpenRemoteFileAsync(Guid peer, Guid offerId, int index, long offset, CancellationToken ct)
    {
        if (!_store.Current.ShareClipboard) throw new IOException("condivisione degli appunti spenta");
        var node = _node ?? throw new IOException("rete non pronta");
        return node.OpenFileAsync(peer, offerId, index, offset, ct);
    }
}
