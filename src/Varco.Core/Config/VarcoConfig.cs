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

public sealed class VarcoConfig
{
    public Guid MachineId { get; set; } = Guid.NewGuid();
    public string MachineName { get; set; } = Environment.MachineName;
    public string? GroupKey { get; set; }

    public int TcpPort { get; set; } = 47800;
    public int DiscoveryPort { get; set; } = 47801;

    /// <summary>Indirizzi aggiunti a mano ("192.168.1.20" o "host:porta"), utili se il broadcast UDP è bloccato.</summary>
    public List<string> ManualAddresses { get; set; } = [];

    public int HeartbeatIntervalMs { get; set; } = 500;
    /// <summary>Tollerante ai picchi del Wi-Fi: 3 s senza traffico = connessione morta.</summary>
    public int HeartbeatTimeoutMs { get; set; } = 3000;

    /// <summary>Aggiornamenti automatici da GitHub (firmati), installati senza chiedere nulla.</summary>
    public bool AutoUpdate { get; set; } = true;
    /// <summary>Solo per i test: indirizzo alternativo della release (formato API di GitHub). Null = GitHub.</summary>
    public string? UpdateFeedUrl { get; set; }

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
