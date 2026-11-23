using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using Varco.Core.Input;

namespace Varco.Core.Config;

public sealed class KnownPeer
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Ultimi indirizzi "ip:porta" con cui la connessione è riuscita (il più recente per primo).</summary>
    public List<string> LastAddresses { get; set; } = [];
    public DateTime LastSeenUtc { get; set; }
}

/// <summary>
/// Chiave di gruppo sostituita (dopo aver rimosso un PC). La si accetta ancora, ma solo dai PC del gruppo che erano
/// spenti al momento del cambio (<see cref="Pending"/>): appena si collegano ricevono la chiave nuova.
/// Il PC rimosso non è in elenco, quindi con la chiave vecchia non entra più.
/// </summary>
public sealed class RetiredGroupKey
{
    public string Key { get; set; } = "";
    public List<Guid> Pending { get; set; } = [];
    public DateTime RetiredUtc { get; set; }
}

public sealed class VarcoConfig
{
    public Guid MachineId { get; set; } = Guid.NewGuid();
    public string MachineName { get; set; } = Environment.MachineName;
    public string? GroupKey { get; set; }
    /// <summary>Quando è stata creata la chiave attuale con un cambio (serve a scegliere fra due cambi fatti in parallelo).</summary>
    public DateTime GroupKeyChangedUtc { get; set; }
    /// <summary>Chiavi sostituite, ancora valide per i PC che non hanno ricevuto quella nuova (la più recente per prima).</summary>
    public List<RetiredGroupKey> RetiredKeys { get; set; } = [];
    /// <summary>PC rimossi dal gruppo: chi riceve la chiave nuova li toglie anche dai propri PC noti.</summary>
    public List<Guid> RemovedPeers { get; set; } = [];

    public int TcpPort { get; set; } = 47800;
    public int DiscoveryPort { get; set; } = 47801;

    /// <summary>Indirizzi aggiunti a mano ("192.168.1.20" o "host:porta"), utili se il broadcast UDP è bloccato.</summary>
    public List<string> ManualAddresses { get; set; } = [];

    public int HeartbeatIntervalMs { get; set; } = 500;
    /// <summary>Tollerante ai picchi del Wi-Fi: 3 s senza traffico = connessione morta.</summary>
    public int HeartbeatTimeoutMs { get; set; } = 3000;

    /// <summary>Appunti condivisi fra i PC del gruppo (testo, testo formattato, immagini).</summary>
    public bool ShareClipboard { get; set; } = true;

    /// <summary>
    /// Monitor di questo PC in standby (DDC/CI) mentre si comanda un altro PC, dopo il tempo di spegnimento dello schermo
    /// impostato in Windows. Vedi <see cref="Input.MonitorStandbyPolicy"/>.
    /// </summary>
    public bool MonitorStandby { get; set; } = true;

    /// <summary>
    /// Schermo acceso mentre suona musica o un video, anche senza toccare il PC: sul PC comandato (Windows lo spegnerebbe)
    /// e sul PC che comanda (niente standby dei monitor). Le casse collegate al monitor si spegnerebbero con lui.
    /// Vedi <see cref="Input.MediaKeepAwakePolicy"/>.
    /// </summary>
    public bool KeepScreenOnWithAudio { get; set; } = true;

    /// <summary>Bordi: negli angoli di ogni monitor non si passa all'altro PC. Vedi <see cref="Input.EdgeSettings"/>.</summary>
    public bool EdgeCorners { get; set; } = true;
    /// <summary>Bordi: si passa solo dopo una breve sosta contro il bordo.</summary>
    public bool EdgeDelay { get; set; }
    /// <summary>Bordi: non si passa mentre un'app a schermo intero è in primo piano.</summary>
    public bool EdgeBlockFullscreen { get; set; } = true;
    /// <summary>Bordi: con Bloc Scorr acceso il cursore resta sul PC dove si trova.</summary>
    public bool EdgeScrollLock { get; set; } = true;

    /// <summary>
    /// Blocco di gruppo: bloccando a mano questo PC si bloccano anche gli altri collegati, e questo PC si blocca quando
    /// lo chiede un altro. Vedi <see cref="Input.GroupLockPolicy"/>.
    /// </summary>
    public bool GroupLock { get; set; } = true;
    /// <summary>Tasti multimediali premuti su questo PC al PC dove suona la musica. Vedi <see cref="Input.MediaKeyRouter"/>.</summary>
    public bool MediaKeysFollowAudio { get; set; } = true;
    /// <summary>
    /// Quando questo PC comanda gli altri, gli spostamenti del cursore seguono la scala dei monitor (100%, 150%...)
    /// invece dei pixel. Spento: al pixel, come sempre.
    /// </summary>
    public bool PointerScaleCompensation { get; set; }

    /// <summary>Connessione disattivata dall'utente: rete chiusa (niente collegamenti né ricerca) finché non la riattiva.</summary>
    public bool Paused { get; set; }

    /// <summary>Aggiornamenti automatici da GitHub (firmati), installati senza chiedere nulla.</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>Solo per i test: indirizzo alternativo della release (formato API di GitHub). Null = GitHub.</summary>
    public string? UpdateFeedUrl { get; set; }

    /// <summary>Audio degli altri PC sul PC centrale: impostazione di tutto il gruppo. Vedi <see cref="Audio.AudioRouting"/>.</summary>
    public Audio.AudioRouting AudioRouting { get; set; } = new();

    public Layout Layout { get; set; } = new();
    public List<KnownPeer> KnownPeers { get; set; } = [];
}

/// <summary>Carica/salva la configurazione in JSON. Il file contiene la chiave di gruppo: la cartella è protetta da ACL.</summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();
    private Timer? _debounce;

    public string Directory { get; }
    public string FilePath => Path.Combine(Directory, "config.json");
    public VarcoConfig Current { get; private set; }

    public ConfigStore(string directory)
    {
        Directory = directory;
        Current = Load();
    }

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Varco");

    private VarcoConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var cfg = JsonSerializer.Deserialize<VarcoConfig>(File.ReadAllText(FilePath), Json);
                if (cfg is not null) return cfg;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Config illeggibile, ne creo una nuova", ex);
        }
        var fresh = new VarcoConfig();
        Current = fresh;
        SaveNow();
        return fresh;
    }

    public void Update(Action<VarcoConfig> change, bool immediate = false)
    {
        lock (_gate) change(Current);
        if (immediate) SaveNow(); else SaveSoon();
    }

    public void SaveSoon()
    {
        lock (_gate)
        {
            _debounce ??= new Timer(_ => SaveNow());
            _debounce.Change(1000, Timeout.Infinite);
        }
    }

    public void SaveNow()
    {
        lock (_gate)
        {
            try
            {
                EnsureDirectory();
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Json));
                Protect(tmp);
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                Log.Error("Salvataggio config fallito", ex);
            }
        }
    }

    private void EnsureDirectory() => System.IO.Directory.CreateDirectory(Directory);

    /// <summary>Il file contiene la chiave di gruppo: accesso solo a SYSTEM, Administrators e all'utente corrente.</summary>
    private static void Protect(string file)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var sec = new FileSecurity();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var sids = new List<SecurityIdentifier>
            {
                new(WellKnownSidType.LocalSystemSid, null),
                new(WellKnownSidType.BuiltinAdministratorsSid, null),
            };
            if (WindowsIdentity.GetCurrent().User is { } me) sids.Add(me);
            foreach (var sid in sids)
                sec.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(file).SetAccessControl(sec);
        }
        catch (Exception ex)
        {
            Log.Warn($"Impossibile impostare ACL su {file}: {ex.Message}");
        }
    }
}
