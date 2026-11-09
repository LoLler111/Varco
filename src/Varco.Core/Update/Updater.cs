using System.Diagnostics;
using System.Net.Http.Headers;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Varco.Core.Control;

namespace Varco.Core.Update;

public sealed class UpdaterOptions
{
    /// <summary>Indirizzo dell'ultima release (formato API di GitHub). Si può cambiare per i test con un server locale.</summary>
    public string FeedUrl { get; init; } = Updater.GitHubFeed;
    public required Version CurrentVersion { get; init; }
    /// <summary>Cartella dei download: accessibile solo a SYSTEM e Administrators.</summary>
    public required string DownloadDirectory { get; init; }
    public string PublicKey { get; init; } = UpdateSignature.PublicKey;
    public TimeSpan FirstCheckDelay { get; init; } = TimeSpan.FromMinutes(2);
    public TimeSpan CheckInterval { get; init; } = TimeSpan.FromHours(6);
    public TimeSpan RetryWhileBusy { get; init; } = TimeSpan.FromMinutes(1);
    /// <summary>Vero se adesso si può installare (nessun PC sta usando mouse e tastiera).</summary>
    public Func<bool> CanInstallNow { get; init; } = () => true;
    /// <summary>Aggiornamenti automatici attivi (impostazione dell'utente).</summary>
    public Func<bool> Enabled { get; init; } = () => true;
    /// <summary>Avvia l'installazione dell'exe scaricato e verificato. Di default: "exe --install --update".</summary>
    public Action<string> Launch { get; init; } = Updater.LaunchInstaller;
    public HttpMessageHandler? Handler { get; init; }
    /// <summary>Limita la cartella dei download a SYSTEM e Administrators (spento solo nei test).</summary>
    public bool ProtectDirectory { get; init; } = true;
}

/// <summary>
/// Controlla le release su GitHub, scarica la nuova versione, ne verifica firma e hash e la fa installare.
/// Gira nell'agent (SYSTEM): l'installazione non chiede nulla all'utente.
/// </summary>
public sealed class Updater : IDisposable
{
    public const string GitHubFeed = "https://api.github.com/repos/LoLler111/Varco/releases/latest";

    private readonly UpdaterOptions _o;
    private readonly HttpClient _http;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusLock = new();
    private readonly UpdateStatusDto _status = new();
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _loop;
    private string? _ready; // exe scaricato e verificato in attesa del momento giusto

    public Updater(UpdaterOptions options)
    {
        _o = options;
        _http = new HttpClient(options.Handler ?? new HttpClientHandler(), disposeHandler: options.Handler is null)
        {
            Timeout = TimeSpan.FromMinutes(10),
        };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Varco-Updater", options.CurrentVersion.ToString(3)));
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public UpdateStatusDto Status
    {
        get
        {
            lock (_statusLock)
                return new UpdateStatusDto
                {
                    State = _status.State,
                    Latest = _status.Latest,
                    Message = _status.Message,
                    LastCheckUtc = _status.LastCheckUtc,
                };
        }
    }

    public void Start()
    {
        if (_loop is not null) return;
        // Exe di aggiornamenti già installati: non servono più (quello appena usato potrebbe essere ancora in chiusura).
        try
        {
            if (Directory.Exists(_o.DownloadDirectory))
                foreach (var f in Directory.EnumerateFiles(_o.DownloadDirectory, "Varco-*"))
                    try { File.Delete(f); } catch { }
        }
        catch { }
        _loop = Task.Run(LoopAsync);
    }

    /// <summary>Controllo immediato (pulsante "Controlla aggiornamenti").</summary>
    public void CheckSoon() => _wake.TrySetResult();

    private async Task LoopAsync()
    {
        var ct = _cts.Token;
        var delay = _o.FirstCheckDelay;
        while (!ct.IsCancellationRequested)
        {
            var manual = await WaitAsync(delay, ct).ConfigureAwait(false);
            if (ct.IsCancellationRequested) break;
            try
            {
                if (manual || _o.Enabled()) await CheckAndInstallAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log.Warn($"Aggiornamento non riuscito: {ex.Message}");
                SetStatus("Error", message: ex.Message);
            }
            // In attesa che nessuno stia usando il mouse: riprovo presto. Altrimenti al prossimo giro, con un po' di
            // casualità perché i PC del gruppo non interroghino GitHub tutti nello stesso istante.
            delay = _ready is not null
                ? _o.RetryWhileBusy
                : _o.CheckInterval + TimeSpan.FromMinutes(Random.Shared.Next(0, 30));
        }
    }

    /// <summary>Attende il tempo indicato o una richiesta manuale (true).</summary>
    private async Task<bool> WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        var wake = _wake.Task;
        var timer = Task.Delay(delay, ct);
        var done = await Task.WhenAny(wake, timer).ConfigureAwait(false);
        if (done == wake)
        {
            _wake = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            return true;
        }
        return false;
    }

    /// <summary>Un giro completo: controlla, scarica, verifica, installa. Ritorna la versione installata (o null).</summary>
    public async Task<Version?> CheckAndInstallAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ready is null)
            {
                SetStatus("Checking");
                var release = await GetLatestAsync(ct).ConfigureAwait(false);
                var now = DateTime.UtcNow;
                if (release is null || release.Version <= _o.CurrentVersion)
                {
                    SetStatus("UpToDate", latest: release?.Version, checkedUtc: now);
                    return null;
                }
                Log.Info($"Nuova versione disponibile: {release.Version} (installata {_o.CurrentVersion.ToString(3)})");
                SetStatus("Downloading", latest: release.Version, checkedUtc: now);
                _ready = await DownloadAsync(release, ct).ConfigureAwait(false);
            }

            if (!_o.CanInstallNow())
            {
                SetStatus("Waiting", message: "Installo appena nessun PC sta usando mouse e tastiera.");
                return null;
            }

            // Ultimo controllo subito prima di avviare: il file è in una cartella che solo SYSTEM/Administrators possono toccare.
            var exe = _ready;
            _ready = null;
            var v = VerifyDownloaded(exe, null);
            SetStatus("Installing", latest: v);
            Log.Info($"Avvio l'installazione di Varco {v.ToString(3)}");
            _o.Launch(exe);
            return v;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal sealed record Release(Version Version, string ExeUrl, string SigUrl);

    internal async Task<Release?> GetLatestAsync(CancellationToken ct)
    {
        using var resp = await _http.GetAsync(_o.FeedUrl, ct).ConfigureAwait(false);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null; // ancora nessuna release
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        return ParseRelease(doc.RootElement);
    }

    internal static Release? ParseRelease(JsonElement root)
    {
        if (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True) return null;
        if (root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True) return null;
        if (!root.TryGetProperty("tag_name", out var tag) || !UpdateSignature.TryParseVersion(tag.GetString(), out var version)) return null;
        string? exe = null, sig = null;
        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            foreach (var a in assets.EnumerateArray())
            {
                var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
                var url = a.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (url is null) continue;
                if (string.Equals(name, UpdateSignature.ExeAssetName, StringComparison.OrdinalIgnoreCase)) exe = url;
                else if (string.Equals(name, UpdateSignature.SigAssetName, StringComparison.OrdinalIgnoreCase)) sig = url;
            }
        return exe is null || sig is null ? null : new Release(version, exe, sig);
    }

    private async Task<string> DownloadAsync(Release r, CancellationToken ct)
    {
        var sigJson = await _http.GetStringAsync(r.SigUrl, ct).ConfigureAwait(false);
        var m = UpdateSignature.Deserialize(sigJson) ?? throw new InvalidDataException("File di firma illeggibile");
        if (!UpdateSignature.Verify(m, _o.PublicKey)) throw new InvalidDataException("Firma dell'aggiornamento non valida: ignorato");
        if (!UpdateSignature.TryParseVersion(m.Version, out var signed) || signed != r.Version)
            throw new InvalidDataException($"La firma è per la versione {m.Version}, la release dice {r.Version.ToString(3)}");

        var dir = PrepareDirectory(_o.DownloadDirectory, _o.ProtectDirectory);
        foreach (var old in Directory.EnumerateFiles(dir, "Varco-*.exe"))
            try { File.Delete(old); } catch { /* ancora in uso: lo tolgo al prossimo giro */ }

        var exe = Path.Combine(dir, $"Varco-{r.Version.ToString(3)}.exe");
        var tmp = exe + ".part";
        try
        {
            using var resp = await _http.GetAsync(r.ExeUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            if (resp.Content.Headers.ContentLength is long len && len != m.Size)
                throw new InvalidDataException($"Dimensione inattesa ({len} invece di {m.Size} byte)");
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (var dst = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long total = 0;
                int n;
                while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += n;
                    if (total > m.Size) throw new InvalidDataException("Il download è più grande del previsto");
                    await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                }
            }
            File.Move(tmp, exe, overwrite: true);
        }
        catch
        {
            try { File.Delete(tmp); } catch { }
            throw;
        }
        File.WriteAllText(exe + ".sig", sigJson);
        VerifyDownloaded(exe, m);
        Log.Info($"Scaricato e verificato {exe}");
        return exe;
    }

    /// <summary>Ricontrolla firma, hash e dimensione dell'exe su disco. Lancia un'eccezione (e lo cancella) se non torna.</summary>
    private Version VerifyDownloaded(string exe, UpdateManifest? m)
    {
        try
        {
            m ??= UpdateSignature.Deserialize(File.ReadAllText(exe + ".sig")) ?? throw new InvalidDataException("Firma mancante");
            if (!UpdateSignature.Verify(m, _o.PublicKey)) throw new InvalidDataException("Firma non valida");
            var (sha, size) = UpdateSignature.HashFile(exe);
            if (size != m.Size || !string.Equals(sha, m.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Il file scaricato non corrisponde alla firma");
            UpdateSignature.TryParseVersion(m.Version, out var v);
            return v;
        }
        catch
        {
            try { File.Delete(exe); } catch { }
            throw;
        }
    }

    /// <summary>Crea la cartella dei download con accesso solo a SYSTEM e Administrators (niente ereditarietà).</summary>
    private static string PrepareDirectory(string dir, bool protect)
    {
        Directory.CreateDirectory(dir);
        if (protect && OperatingSystem.IsWindows())
        {
            var sec = new DirectorySecurity();
            sec.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            foreach (var sid in new[] { WellKnownSidType.LocalSystemSid, WellKnownSidType.BuiltinAdministratorsSid })
                sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(sid, null), FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(dir).SetAccessControl(sec);
        }
        return dir;
    }

    public static void LaunchInstaller(string exe) =>
        Process.Start(new ProcessStartInfo(exe, "--install --update")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        })?.Dispose();

    private void SetStatus(string state, Version? latest = null, string? message = null, DateTime? checkedUtc = null)
    {
        lock (_statusLock)
        {
            _status.State = state;
            if (latest is not null) _status.Latest = latest.ToString(3);
            _status.Message = message;
            if (checkedUtc is not null) _status.LastCheckUtc = checkedUtc;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _loop?.Wait(2000); } catch { }
        _http.Dispose();
        _cts.Dispose();
    }
}
