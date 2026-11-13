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

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
}

public sealed class ControlRequest
{
    /// <summary>
    /// status | pair (Id o Address) | pairAnswer (RequestId, Accept) | pairCancel | forget (Id) | leaveGroup |
    /// setRow (Order) | setLayout (Positions) | addAddress | removeAddress | reconnect | getKey | setKey |
    /// checkUpdate | setAutoUpdate (Enabled) | setClipboard (Enabled) | filesOffer (Seq, attesa lunga)
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
    /// <summary>filesOffer: ultimo numero di offerta già visto dall'icona (null = dimmi solo a che punto siamo).</summary>
    public long? Seq { get; set; }
}

public sealed class ControlResponse
{
    public bool Ok { get; set; } = true;
    public string? Error { get; set; }
    public string? Key { get; set; }
    public StatusDto? Status { get; set; }
    public FilesOfferDto? FilesOffer { get; set; }
}

public sealed class StatusDto
{
    public Guid MachineId { get; set; }
    public string MachineName { get; set; } = "";
    public bool HasKey { get; set; }
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
    /// <summary>Versione dell'agent in esecuzione (x.y.z).</summary>
    public string Version { get; set; } = "";
    public bool AutoUpdate { get; set; } = true;
    public bool ShareClipboard { get; set; } = true;
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
    /// <summary>Connected | Connecting | Disconnected</summary>
    public string State { get; set; } = "Disconnected";
    public string? Address { get; set; }
    public double RttMs { get; set; }
    public string? LastError { get; set; }
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
