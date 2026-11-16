using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Varco.Core.Input;

namespace Varco.Core.Control;

/// <summary>Protocollo locale (named pipe, una riga JSON per richiesta) fra l'icona nella tray e l'agent.</summary>
public static class ControlProtocol
{
    public const string PipeName = "Varco.Control";

    /// <summary>
    /// Pipe dei dati dei file incollati: l'icona manda una riga JSON (<see cref="FileReadRequest"/>), l'agent risponde con
    /// la lunghezza (int64; -1 = errore, seguito da una riga di testo) e poi i byte del file.
    /// </summary>
    public const string FilesPipeName = "Varco.Files";

    /// <summary>Evento con cui l'agent chiede al servizio di eseguire Ctrl+Alt+Canc (SendSAS). Solo SYSTEM.</summary>
    public const string SasEventName = @"Global\Varco.SAS";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

public sealed class ControlRequest
{
    /// <summary>
    /// status | pair (Id o Address) | pairAnswer (RequestId, Accept) | pairCancel | forget (Id) | leaveGroup |
    /// setRow (Order) | setLayout (Positions) | addAddress | removeAddress | reconnect | getKey | setKey |
    /// checkUpdate | installUpdateNow (anche sugli altri PC collegati) | setAutoUpdate (Enabled) | setClipboard (Enabled) | setMonitorStandby (Enabled) |
    /// setEdgeOption (Option = corners | delay | fullscreen | scrollLock, Enabled) | exportLogs (risposta in Zip) | setPaused (Enabled = true disattiva la connessione) |
    /// filesOffer (Seq, attesa lunga) | localFiles (Paths: file copiati su questo PC, letti dall'icona) |
    /// dragEvents (Seq, attesa lunga) | dragFiles (Seq, Paths: file trascinati contro il bordo, vuoto = nessuno) |
    /// dragData (Seq, Formats: testo, HTML, immagini trascinati contro il bordo) |
    /// dragReady (Seq: finestrella da cui parte il trascinamento pronta sotto il cursore)
    /// </summary>
    public string Cmd { get; set; } = "status";
    public string? Key { get; set; }
    public string? Address { get; set; }
    public List<Guid>? Order { get; set; }
    public Guid? Id { get; set; }
    public Guid? RequestId { get; set; }
    public bool? Accept { get; set; }
    /// <summary>setLayout: nuove posizioni dei PC nella disposizione 2D.</summary>
    public List<LayoutPositionDto>? Positions { get; set; }
    public bool? Enabled { get; set; }
    /// <summary>setEdgeOption: quale regola dei bordi cambiare.</summary>
    public string? Option { get; set; }
    /// <summary>filesOffer: ultimo numero di offerta già visto dall'icona (null = dimmi solo a che punto siamo).</summary>
    public long? Seq { get; set; }
    /// <summary>localFiles: percorsi dei file e delle cartelle appena copiati in Esplora risorse. dragFiles: quelli trascinati.</summary>
    public List<string>? Paths { get; set; }
    /// <summary>dragData: i formati letti da ciò che si trascina (stessi nomi degli appunti condivisi).</summary>
    public List<ClipFormatDto>? Formats { get; set; }
}

/// <summary>Un formato di dati trascinati: nome come negli appunti condivisi ("CF_UNICODETEXT", "HTML Format", "PNG"…) e byte grezzi.</summary>
public sealed class ClipFormatDto
{
    public string Name { get; set; } = "";
    public byte[] Data { get; set; } = [];
}

public sealed class ControlResponse
{
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public string? Key { get; set; }
    public StatusDto? Status { get; set; }
    public FilesOfferDto? FilesOffer { get; set; }
    public DragEventDto? DragEvent { get; set; }
    /// <summary>exportLogs: zip con i log e la configurazione senza chiavi.</summary>
    public byte[]? Zip { get; set; }
}

/// <summary>
/// Trascinamento di file fra i PC, dall'agent all'icona:
///  • Probe: pulsante premuto contro il bordo; l'icona mette sotto il cursore una finestrella invisibile e, se Esplora
///    risorse ci passa sopra dei file, ne manda i percorsi con dragFiles.
///  • Source: un altro PC trascina dei file (o testo, immagini) qui; l'icona mette sotto il cursore la finestrella da
///    cui far partire il trascinamento (con <see cref="Offer"/> o <see cref="Data"/>) e risponde dragReady.
///  • End: finita la prova o annullato il trascinamento: via le finestrelle.
/// </summary>
public sealed class DragEventDto
{
    public long Seq { get; set; }
    /// <summary>None | Probe | Source | End</summary>
    public string Kind { get; set; } = "None";
    public FilesOfferDto? Offer { get; set; }
    /// <summary>Source di testo o immagini: i formati da offrire al rilascio.</summary>
    public List<ClipFormatDto>? Data { get; set; }
    public string? PeerName { get; set; }
}

public sealed class StatusDto
{
    public Guid MachineId { get; set; }
    public string MachineName { get; set; } = "";
    public bool HasKey { get; set; }
    /// <summary>Connessione disattivata dall'utente.</summary>
    public bool Paused { get; set; }
    public string Mode { get; set; } = "Local";
    public string? ActivePeerName { get; set; }
    public int ListenPort { get; set; }
    public List<PeerDto> Peers { get; set; } = [];
    public List<LayoutEntry> Layout { get; set; } = [];
    public List<string> ManualAddresses { get; set; } = [];
    /// <summary>PC con Varco trovati nella rete ma non ancora abbinati.</summary>
    public List<DiscoveredDto> Discovered { get; set; } = [];
    /// <summary>Un altro PC chiede di abbinarsi: va mostrata la richiesta all'utente.</summary>
    public PairRequestDto? IncomingPair { get; set; }
    /// <summary>Abbinamento avviato da questo PC.</summary>
    public PairProgressDto? OutgoingPair { get; set; }
    /// <summary>Quando è partito l'agent (le statistiche di connessione partono da lì).</summary>
    public DateTime AgentStartedUtc { get; set; }
    /// <summary>Versione dell'agent in esecuzione (x.y.z).</summary>
    public string Version { get; set; } = "";
    public bool AutoUpdate { get; set; } = true;
    public bool ShareClipboard { get; set; } = true;
    public bool MonitorStandby { get; set; } = true;
    public bool EdgeCorners { get; set; } = true;
    public bool EdgeDelay { get; set; }
    public bool EdgeBlockFullscreen { get; set; } = true;
    public bool EdgeScrollLock { get; set; } = true;
    /// <summary>Null se gli aggiornamenti non sono attivi (agent avviato a mano per sviluppo).</summary>
    public UpdateStatusDto? Update { get; set; }
}

/// <summary>File copiati su un altro PC, da mettere negli appunti come file virtuali.</summary>
public sealed class FilesOfferDto
{
    public long Seq { get; set; }
    public Guid PeerId { get; set; }
    public string PeerName { get; set; } = "";
    public Guid OfferId { get; set; }
    public List<FileEntryDto> Entries { get; set; } = [];
}

public sealed class FileEntryDto
{
    public string Path { get; set; } = "";
    public long Size { get; set; }
    public uint Attributes { get; set; }
    public DateTime LastWriteUtc { get; set; }
    public bool IsDirectory { get; set; }
}

/// <summary>Richiesta sulla pipe <see cref="ControlProtocol.FilesPipeName"/>.</summary>
public sealed class FileReadRequest
{
    public Guid PeerId { get; set; }
    public Guid OfferId { get; set; }
    public int Index { get; set; }
    public long Offset { get; set; }
}

public sealed class UpdateStatusDto
{
    /// <summary>Idle | Checking | UpToDate | Downloading | Waiting | Installing | Error</summary>
    public string State { get; set; } = "Idle";
    /// <summary>Ultima versione pubblicata, se nota.</summary>
    public string? Latest { get; set; }
    public string? Message { get; set; }
    public DateTime? LastCheckUtc { get; set; }
}

/// <summary>Posizione di un PC nella disposizione (pixel virtuali, angolo in alto a sinistra).</summary>
public sealed class LayoutPositionDto
{
    public Guid Id { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
}

public sealed class DiscoveredDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
}

public sealed class PairRequestDto
{
    public Guid RequestId { get; set; }
    public string PeerName { get; set; } = "";
    public string Code { get; set; } = "";
}

public sealed class PairProgressDto
{
    public string PeerName { get; set; } = "";
    public string? Code { get; set; }
    /// <summary>Connecting | WaitingConfirm | Done | Rejected | Failed</summary>
    public string State { get; set; } = "Connecting";
    public string? Message { get; set; }
}

public sealed class PeerDto
{
    public Guid? Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Connected | Connecting | Disconnected | Paused (connessione disattivata su questo PC)</summary>
    public string State { get; set; } = "Disconnected";
    public string? Address { get; set; }
    public double RttMs { get; set; }
    public string? LastError { get; set; }
    /// <summary>Da dove passa la connessione: "Wi-Fi", "Cavo", "VPN"… (null se non collegato o sconosciuto).</summary>
    public string? Link { get; set; }
    /// <summary>Nome della scheda di rete di questo PC usata dalla connessione.</summary>
    public string? Adapter { get; set; }
    /// <summary>Indirizzo di questo PC usato dalla connessione.</summary>
    public string? LocalAddress { get; set; }
    public DateTime? ConnectedSinceUtc { get; set; }
    /// <summary>Connessioni cadute nelle ultime 24 ore (da quando l'agent è partito, se è partito da meno).</summary>
    public int Drops24h { get; set; }
    public string? LastDropReason { get; set; }
    public DateTime? LastDropUtc { get; set; }
}

public static class ControlClient
{
    public static async Task<ControlResponse> SendAsync(ControlRequest request, int timeoutMs = 1500)
    {
        using var cts = new CancellationTokenSource(timeoutMs);
        await using var pipe = new NamedPipeClientStream(".", ControlProtocol.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(cts.Token).ConfigureAwait(false);
        var line = JsonSerializer.Serialize(request, ControlProtocol.Json) + "\n";
        await pipe.WriteAsync(Encoding.UTF8.GetBytes(line), cts.Token).ConfigureAwait(false);
        await pipe.FlushAsync(cts.Token).ConfigureAwait(false);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        var resp = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false)
            ?? throw new IOException("Nessuna risposta dall'agent");
        return JsonSerializer.Deserialize<ControlResponse>(resp, ControlProtocol.Json)
            ?? throw new IOException("Risposta non valida");
    }
}
