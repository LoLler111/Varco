using Varco.Core.Clipboard;
using Varco.Core.Control;
using Varco.Core.Files;
using Varco.Core.Input;
using Varco.Core.Protocol;

namespace Varco.Core;

/// <summary>
/// Trascinamento di file (e di testo e immagini, vedi <see cref="DragPayload"/>) da uno schermo all'altro. Chi trascina e chi riceve sono Esplora risorse, che gira come utente:
/// l'agent (SYSTEM) non può fare da bersaglio né da sorgente di un trascinamento, quindi chiede all'icona
/// (vedi <see cref="DragEventDto"/>). I dati dei file viaggiano come per gli appunti (<see cref="FileOffers"/>), quando
/// l'utente rilascia sopra una cartella.
/// </summary>
public sealed partial class VarcoHost : IFileDrag
{
    /// <summary>Oltre questa dimensione (offerta compressa) il trascinamento non passa: si usa copia e incolla.</summary>
    private const int MaxDragOfferBytes = 900 * 1024;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(3);

    private readonly object _dragGate = new();
    private DragEventDto _dragEvent = new();
    private TaskCompletionSource _dragChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _dragPollers;
    private Guid? _probeTarget;
    private long _probeSeq, _sourceSeq;

    /// <summary>Attiva il trascinamento di file: solo l'agent con mouse, tastiera e appunti veri.</summary>
    public void EnableFileDrag()
    {
        if (FileOffers is null || Clipboard is null) return;
        Clipboard.DragSupported = true;
        Coordinator.FileDrag = this;
    }

    private long PublishDrag(string kind, FilesOfferDto? offer = null) => PublishDrag(new DragEventDto { Kind = kind, Offer = offer });

    private long PublishDrag(DragEventDto e)
    {
        TaskCompletionSource fire;
        long seq;
        lock (_dragGate)
        {
            e.Seq = seq = _dragEvent.Seq + 1;
            _dragEvent = e;
            fire = _dragChanged;
            _dragChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        fire.TrySetResult();
        return seq;
    }

    // ───────────── IFileDrag (chiamati con il lock del coordinatore: niente attese) ─────────────

    bool IFileDrag.CanDragTo(Guid target) =>
        _store.Current.ShareClipboard && Clipboard is { } c && c.CapsOf(target).HasFlag(PeerCaps.DragFiles)
        && Volatile.Read(ref _dragPollers) > 0; // l'icona è aperta e ascolta

    void IFileDrag.Probe(Guid target)
    {
        long seq;
        lock (_dragGate)
        {
            _probeTarget = target;
            _probeSeq = seq = PublishDrag("Probe");
        }
        _ = Task.Delay(ProbeTimeout).ContinueWith(_ =>
        {
            lock (_dragGate) if (_probeSeq != seq || _probeTarget is null) return;
            Log.Info("Trascinamento contro il bordo: l'icona non ha risposto in tempo");
            Coordinator.OnDragProbeResult(target, null);
        }, TaskScheduler.Default);
    }

    void IFileDrag.EndProbe()
    {
        lock (_dragGate)
        {
            if (_probeTarget is null && _dragEvent.Kind != "Probe") return;
            _probeTarget = null;
        }
        PublishDrag("End");
    }

    void IFileDrag.Incoming(Guid from, byte[] offer)
    {
        FileOffer? parsed = null;
        try
        {
            var raw = ClipboardSync.Decompress(offer);
            if (DragPayload.TryUnwrap(raw) is { } data)
            {
                if (data.Formats.Count == 0)
                {
                    Log.Warn("Trascinamento senza formati utilizzabili (versione più recente?)");
                    return;
                }
                Log.Info($"Trascinamento in arrivo da {PeerName(from)}: {DragPayload.Describe(data)}");
                var dto = new DragEventDto
                {
                    Kind = "Source",
                    PeerName = PeerName(from),
                    Data = data.Formats.Select(f => new ClipFormatDto { Name = f.Name, Data = f.Data }).ToList(),
                };
                lock (_dragGate) _sourceSeq = PublishDrag(dto);
                return;
            }
            parsed = FileOffer.Deserialize(raw);
        }
        catch (Exception ex) { Log.Warn($"File trascinati non validi: {ex.Message}"); }
        if (parsed is null)
        {
            Log.Warn("File trascinati non validi o di una versione più recente");
            return; // senza risposta il coordinatore annulla da solo dopo qualche secondo
        }
        var offerDto = ToDto(from, parsed);
        lock (_dragGate) _sourceSeq = PublishDrag("Source", offerDto);
    }

    void IFileDrag.CancelIncoming()
    {
        lock (_dragGate) _sourceSeq = 0;
        PublishDrag("End");
    }

    // ───────────── comandi dell'icona ─────────────

    /// <summary>Attesa lunga: il prossimo evento di trascinamento dopo <paramref name="seq"/> (con null dice solo a che punto siamo).</summary>
    private async Task<DragEventDto> WaitDragEventAsync(long? seq)
    {
        Interlocked.Increment(ref _dragPollers);
        try
        {
            Task changed;
            lock (_dragGate)
            {
                if (seq is null) return new DragEventDto { Seq = _dragEvent.Seq };
                if (_dragEvent.Seq > seq) return _dragEvent;
                changed = _dragChanged.Task;
            }
            await Task.WhenAny(changed, Task.Delay(25_000)).ConfigureAwait(false);
            lock (_dragGate) return _dragEvent.Seq > seq ? _dragEvent : new DragEventDto { Seq = _dragEvent.Seq };
        }
        finally
        {
            Interlocked.Decrement(ref _dragPollers);
        }
    }

    /// <summary>L'icona ha visto quali file si stanno trascinando contro il bordo (nessuno = non si trascinano file).</summary>
    private ControlResponse OnDraggedFiles(long? seq, List<string>? paths)
    {
        Guid target;
        lock (_dragGate)
        {
            if (seq != _probeSeq || _probeTarget is not Guid t) return new ControlResponse();
            target = t;
        }
        var valid = (paths ?? []).Where(p => !string.IsNullOrWhiteSpace(p) && Path.IsPathFullyQualified(p)).Take(FileOffers.MaxEntries).ToList();
        _ = Task.Run(() =>
        {
            byte[]? wire = null;
            try
            {
                if (valid.Count > 0 && FileOffers?.Create(valid) is { } offer)
                {
                    wire = ClipboardSync.Compress(offer.Serialize());
                    if (wire.Length > MaxDragOfferBytes)
                    {
                        Log.Info($"Troppi file trascinati ({offer.Entries.Count} elementi): per questi usa copia e incolla");
                        wire = null;
                    }
                    else
                    {
                        Log.Info($"Si stanno trascinando {offer.FileCount} file verso il bordo: possono passare all'altro PC");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("File trascinati", ex);
                wire = null;
            }
            Coordinator.OnDragProbeResult(target, wire);
        });
        return new ControlResponse();
    }

    /// <summary>
    /// L'icona ha visto che contro il bordo si trascina testo o un'immagine (da un browser, da Word…), non dei file.
    /// Passa solo verso i PC che lo sanno ricevere e se sta in un messaggio.
    /// </summary>
    private ControlResponse OnDraggedData(long? seq, List<ClipFormatDto>? formats)
    {
        Guid target;
        lock (_dragGate)
        {
            if (seq != _probeSeq || _probeTarget is not Guid t) return new ControlResponse();
            target = t;
        }
        var content = new ClipContent((formats ?? []).Where(f => DragPayload.Formats.Contains(f.Name) && f.Data.Length > 0)
            .Select(f => new ClipFormat(f.Name, f.Data)).ToList());
        _ = Task.Run(() =>
        {
            byte[]? wire = null;
            try
            {
                if (content.Formats.Count == 0) { }
                else if (Clipboard?.CapsOf(target).HasFlag(PeerCaps.DragData) != true)
                    Log.Info("Si trascina testo o un'immagine, ma l'altro PC ha una versione di Varco che riceve solo file");
                else
                {
                    wire = DragPayload.Wrap(content);
                    if (wire.Length > MaxDragOfferBytes)
                    {
                        Log.Info($"Troppo grande da trascinare ({DragPayload.Describe(content)}): usa copia e incolla");
                        wire = null;
                    }
                    else
                    {
                        Log.Info($"Si trascina verso il bordo: {DragPayload.Describe(content)}. Può passare all'altro PC");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Dati trascinati", ex);
                wire = null;
            }
            Coordinator.OnDragProbeResult(target, wire);
        });
        return new ControlResponse();
    }

    /// <summary>L'icona ha messo sotto il cursore la finestrella da cui far partire il trascinamento.</summary>
    private ControlResponse OnDragSourceReady(long? seq)
    {
        lock (_dragGate) if (seq != _sourceSeq || seq == 0) return new ControlResponse();
        Coordinator.OnDragSourceReady();
        return new ControlResponse();
    }

    private string PeerName(Guid id) =>
        Layout.Current.Entries.FirstOrDefault(e => e.Id == id)?.Name ?? _store.Current.KnownPeers.FirstOrDefault(p => p.Id == id)?.Name ?? "";

    private FilesOfferDto ToDto(Guid from, FileOffer offer) => new()
    {
        PeerId = from,
        PeerName = PeerName(from),
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
}
