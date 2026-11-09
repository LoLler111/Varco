using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;
using Microsoft.Win32;
using Varco.Core;
using Varco.Core.Config;
using Varco.Service;

namespace Varco.App;

/// <summary>
/// Installazione e disinstallazione fatte dallo stesso Varco.exe: niente installer esterni.
/// Installa in Programmi\Varco, registra il servizio (con riavvio automatico), apre il firewall
/// per tutti i profili di rete, avvia l'icona a ogni accesso e compare in "App installate".
/// </summary>
internal static class Installer
{
    private const string RunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";
    private const string UninstallKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Varco";
    private const string FirewallRule = "Varco";

    public static string InstallDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Varco");
    public static string InstalledExe => Path.Combine(InstallDir, "Varco.exe");
    private static string Self => Environment.ProcessPath ?? Application.ExecutablePath;
    public static string ErrorFile => Path.Combine(Path.GetTempPath(), "varco-setup-error.txt");

    public static Version CurrentVersion => typeof(Installer).Assembly.GetName().Version ?? new Version(0, 0);

    public static bool IsRunningFromInstallDir() =>
        string.Equals(Path.GetFullPath(Self), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);

    public static Version? InstalledVersion()
    {
        try
        {
            if (!File.Exists(InstalledExe)) return null;
            var v = FileVersionInfo.GetVersionInfo(InstalledExe);
            return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
        }
        catch { return null; }
    }

    public static bool IsAdmin()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Rilancia Varco.exe come amministratore (finestra UAC). -2 se l'utente annulla.</summary>
    public static int RunElevated(string arg)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Self, arg) { UseShellExecute = true, Verb = "runas" })!;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return -2;
        }
    }

    /// <summary>Avvia l'icona come utente normale anche se chi chiama è amministratore (tramite Esplora risorse).</summary>
    public static void LaunchTray() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{InstalledExe}\"") { UseShellExecute = true });

    // ═══════════════════════ installazione ═══════════════════════

    /// <param name="update">
    /// Aggiornamento automatico lanciato dall'agent (SYSTEM): nessuna finestra; le icone nella tray vengono chiuse
    /// e riaperte nelle sessioni degli utenti; il servizio riparte comunque, anche se qualcosa va storto.
    /// </param>
    public static int RunInstall(bool update = false)
    {
        Log.Init(Path.Combine(ConfigStore.DefaultDirectory, "logs"), "setup.log");
        try { File.Delete(ErrorFile); } catch { }
        var serviceStopped = false;
        var traySessions = new HashSet<uint>();
        try
        {
            if (!IsAdmin()) throw new InvalidOperationException("Servono i permessi di amministratore.");
            Log.Info($"{(update ? "Aggiornamento automatico" : "Installazione")} di Varco {CurrentVersion} da {Self}");

            DeleteOldCopies();
            traySessions = StopEverything();
            serviceStopped = true;
            CopyFiles();

            var exe = InstalledExe;
            var binPath = $"\"\\\"{exe}\\\" --service\"";
            if (ServiceExists())
                Run("sc.exe", $"config {ServiceMain.ServiceName} binPath= {binPath} start= auto DisplayName= Varco");
            else
                Run("sc.exe", $"create {ServiceMain.ServiceName} binPath= {binPath} start= auto DisplayName= Varco");
            Run("sc.exe", $"description {ServiceMain.ServiceName} \"Condivide mouse e tastiera tra i PC della rete locale.\"", check: false);
            Run("sc.exe", $"failure {ServiceMain.ServiceName} reset= 60 actions= restart/1000/restart/1000/restart/5000", check: false);

            // Firewall per tutti i profili: dopo una riconnessione Windows può classificare la rete come "Pubblica".
            foreach (var old in new[] { FirewallRule, "Varco (TCP)", "Varco (discovery UDP)" })
                Run("netsh.exe", $"advfirewall firewall delete rule name=\"{old}\"", check: false);
            Run("netsh.exe", $"advfirewall firewall add rule name=\"{FirewallRule}\" dir=in action=allow program=\"{exe}\" enable=yes profile=any");

            using (var run = Registry.LocalMachine.CreateSubKey(RunKey))
                run.SetValue("Varco", $"\"{exe}\"");

            using (var un = Registry.LocalMachine.CreateSubKey(UninstallKey))
            {
                un.SetValue("DisplayName", "Varco");
                un.SetValue("DisplayVersion", CurrentVersion.ToString(3));
                un.SetValue("Publisher", "Varco");
                un.SetValue("DisplayIcon", $"{exe},0");
                un.SetValue("InstallLocation", InstallDir);
                un.SetValue("UninstallString", $"\"{exe}\" --uninstall");
                un.SetValue("NoModify", 1, RegistryValueKind.DWord);
                un.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                un.SetValue("EstimatedSize", (int)(DirSize(InstallDir) / 1024), RegistryValueKind.DWord);
            }

            CreateStartMenuShortcut(exe);

            StartService();
            serviceStopped = false;
            Log.Info(update ? $"Aggiornamento a {CurrentVersion.ToString(3)} completato" : "Installazione completata");
            if (update) ReopenTrays(traySessions, "--after-update");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error(update ? "Aggiornamento fallito" : "Installazione fallita", ex);
            try { File.WriteAllText(ErrorFile, ex.Message); } catch { }
            // Mai lasciare il PC senza Varco: se il servizio era stato fermato, lo riavvio (con l'exe vecchio se la copia è fallita).
            if (serviceStopped && ServiceExists())
                try { StartService(); } catch (Exception ex2) { Log.Error("Riavvio del servizio fallito", ex2); }
            if (update) ReopenTrays(traySessions, "");
            return 1;
        }
    }

    /// <summary>
    /// Riapre l'icona in ogni sessione in cui era aperta, come l'utente di quella sessione. Durante un aggiornamento
    /// automatico non c'è nessuno a cui chiedere: il setup gira come SYSTEM. Nell'installazione normale ci pensa la finestra di setup.
    /// </summary>
    private static void ReopenTrays(IEnumerable<uint> sessions, string args)
    {
        if (!File.Exists(InstalledExe)) return;
        foreach (var s in sessions) ServiceMain.LaunchAsSessionUser(s, InstalledExe, args);
    }

    private static void StartService()
    {
        using var sc = new System.ServiceProcess.ServiceController(ServiceMain.ServiceName);
        if (sc.Status == System.ServiceProcess.ServiceControllerStatus.Running) return;
        sc.Start();
        sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
    }

    /// <summary>Ferma il servizio, che chiude l'agent.</summary>
    private static void StopService()
    {
        try
        {
            using var sc = new System.ServiceProcess.ServiceController(ServiceMain.ServiceName);
            if (sc.Status != System.ServiceProcess.ServiceControllerStatus.Stopped)
            {
                Log.Info("Arresto del servizio Varco");
                sc.Stop();
                sc.WaitForStatus(System.ServiceProcess.ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
            }
        }
        catch (InvalidOperationException) { /* servizio non installato */ }
        ServiceMain.SignalAgentStop();
    }

    /// <summary>Copie vecchie messe da parte durante un aggiornamento (erano in uso da un'icona aperta).</summary>
    private static void DeleteOldCopies() => ServiceMain.DeleteOldCopies(InstallDir);

    private static void CopyFiles()
    {
        if (IsRunningFromInstallDir()) return;
        Directory.CreateDirectory(InstallDir);
        var srcDir = Path.GetDirectoryName(Self)!;
        // Build di sviluppo (non single-file): accanto all'exe ci sono le DLL, copio tutta la cartella.
        if (File.Exists(Path.Combine(srcDir, "Varco.dll")))
        {
            foreach (var file in Directory.EnumerateFiles(srcDir, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(InstallDir, Path.GetRelativePath(srcDir, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                Retry(() => File.Copy(file, dest, overwrite: true));
            }
        }
        else
        {
            // Un exe in esecuzione (es. l'icona nella tray) non si può sovrascrivere ma si può rinominare:
            // lo sposto da parte, copio il nuovo e, se la copia fallisce, rimetto a posto il vecchio.
            string? aside = null;
            if (File.Exists(InstalledExe))
            {
                aside = Path.Combine(InstallDir, $"Varco.old-{DateTime.Now:yyyyMMddHHmmssfff}.exe");
                Retry(() => File.Move(InstalledExe, aside));
            }
            try
            {
                Retry(() => File.Copy(Self, InstalledExe, overwrite: true));
            }
            catch
            {
                if (aside is not null)
                {
                    try { File.Delete(InstalledExe); } catch { }
                    File.Move(aside, InstalledExe);
                }
                throw;
            }
            if (aside is not null) try { File.Delete(aside); } catch { /* ancora in uso: lo cancello alla prossima occasione */ }
        }
        Log.Info($"File copiati in {InstallDir}");
    }

    // ═══════════════════════ disinstallazione ═══════════════════════

    public static int RunUninstallInteractive(bool quiet = false)
    {
        if (!IsAdmin())
        {
            var rc = RunElevated(quiet ? "--uninstall --quiet" : "--uninstall");
            return rc == -2 ? 1 : rc;
        }
        if (quiet)
        {
            // Senza finestre (script, test): esito solo nel codice di uscita e nel log.
            Log.Init(Path.GetTempPath(), "varco-uninstall.log");
            try { Uninstall(); return 0; }
            catch (Exception ex) { Log.Error("Disinstallazione fallita", ex); return 1; }
        }
        if (MessageBox.Show("Vuoi rimuovere Varco da questo PC?\nGli altri PC del gruppo continueranno a funzionare tra loro.",
                "Varco", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return 1;
        try
        {
            Uninstall();
            MessageBox.Show("Varco è stato rimosso.", "Varco", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Rimozione non completata: {ex.Message}", "Varco", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    private static void Uninstall()
    {
        StopEverything();
        if (ServiceExists()) Run("sc.exe", $"delete {ServiceMain.ServiceName}", check: false);
        Run("netsh.exe", $"advfirewall firewall delete rule name=\"{FirewallRule}\"", check: false);
        using (var run = Registry.LocalMachine.OpenSubKey(RunKey, writable: true)) run?.DeleteValue("Varco", throwOnMissingValue: false);
        Registry.LocalMachine.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
        try { File.Delete(StartMenuShortcut); } catch { }
        try { Directory.Delete(ConfigStore.DefaultDirectory, recursive: true); } catch { }

        // L'exe in uso non può cancellarsi da solo: ci pensa un comando che parte appena usciamo.
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c timeout /t 2 /nobreak >nul & rmdir /s /q \"{InstallDir}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    // ═══════════════════════ utilità ═══════════════════════

    /// <summary>Ferma servizio e agent e chiude le icone aperte. Ritorna le sessioni in cui c'era un'icona.</summary>
    private static HashSet<uint> StopEverything()
    {
        StopService();
        var sessions = new HashSet<uint>();
        var me = Environment.ProcessId;
        // Anche le copie Varco.old-*.exe rimaste aperte da un aggiornamento precedente.
        foreach (var p in Process.GetProcesses().Where(p => p.ProcessName.StartsWith("Varco", StringComparison.OrdinalIgnoreCase)))
        {
            using (p)
            {
                if (p.Id == me) continue;
                // Solo le copie installate (icone, agent): non la finestra di setup che ci ha lanciato.
                string? path = null;
                try { path = p.MainModule?.FileName; } catch { /* processo SYSTEM: percorso non leggibile */ }
                if (path is not null && !string.Equals(Path.GetDirectoryName(path), InstallDir, StringComparison.OrdinalIgnoreCase)) continue;
                if (path is not null && p.SessionId != 0) sessions.Add((uint)p.SessionId);
                try
                {
                    if (!p.WaitForExit(1000)) p.Kill();
                    p.WaitForExit(3000);
                }
                catch (Exception ex) { Log.Warn($"Impossibile chiudere Varco (pid {p.Id}): {ex.Message}"); }
            }
        }
        return sessions;
    }

    private static bool ServiceExists() =>
        System.ServiceProcess.ServiceController.GetServices().Any(s => s.ServiceName.Equals(ServiceMain.ServiceName, StringComparison.OrdinalIgnoreCase));

    private static void Run(string exe, string args, bool check = true)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        var output = (p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd()).Trim();
        p.WaitForExit();
        Log.Info($"{exe} {args} → {p.ExitCode}");
        if (check && p.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileNameWithoutExtension(exe)} non riuscito ({p.ExitCode}): {output}");
    }

    private static void Retry(Action action)
    {
        for (var i = 0; ; i++)
        {
            try { action(); return; }
            catch (IOException) when (i < 10) { Thread.Sleep(500); }
            catch (UnauthorizedAccessException) when (i < 10) { Thread.Sleep(500); }
        }
    }

    private static long DirSize(string dir)
    {
        try { return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }

    private static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "Varco.lnk");

    private static void CreateStartMenuShortcut(string exe)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic link = shell.CreateShortcut(StartMenuShortcut);
            link.TargetPath = exe;
            link.IconLocation = $"{exe},0";
            link.Description = "Un mouse e una tastiera per tutti i tuoi PC";
            link.Save();
        }
        catch (Exception ex)
        {
            Log.Warn($"Collegamento nel menu Start non creato: {ex.Message}");
        }
    }
}
