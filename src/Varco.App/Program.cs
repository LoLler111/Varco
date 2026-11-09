using Varco.Agent;
using Varco.Service;

namespace Varco.App;

/// <summary>
/// Varco.exe è un solo file con più ruoli:
///   (nessuna opzione)  setup se non è installato qui, altrimenti icona nella tray
///   --install          installazione (richiede amministratore, lanciata dal setup)
///   --uninstall        disinstallazione (da "App installate" di Windows); con --quiet senza finestre
///   --service          servizio Windows
///   --agent            rete + input (avviato dal servizio come SYSTEM)
///   --tray             icona nella tray anche fuori dalla cartella di installazione (sviluppo)
/// </summary>
internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--service")) return ServiceMain.Run(args);
        if (args.Contains("--agent")) return AgentMain.RunAsync(args).GetAwaiter().GetResult();

        ApplicationConfiguration.Initialize();

        if (args.Contains("--install")) return Installer.RunInstall(update: args.Contains("--update"));
        if (args.Contains("--uninstall")) return Installer.RunUninstallInteractive(quiet: args.Contains("--quiet"));

        // --tray forza l'icona anche fuori dalla cartella di installazione (sviluppo).
        if (!args.Contains("--tray") && !Installer.IsRunningFromInstallDir())
        {
            Application.Run(new SetupForm());
            return 0;
        }

        // L'icona gira come utente: log nella sua cartella. Un errore non deve mai bloccarla con una finestra di eccezione.
        Varco.Core.Log.Init(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Varco", "logs"), "tray.log");
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Varco.Core.Log.Error("Errore nell'icona", e.Exception);

        // --after-update: avviata dalla vecchia icona dopo un aggiornamento; aspetto che quella si chiuda.
        var afterUpdate = args.Contains("--after-update");
        using var single = new Mutex(false, @"Local\Varco.Tray");
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, TrayContext.ShowEventName);
        if (!Acquire(single, afterUpdate ? 15000 : 0))
        {
            // Già aperto (es. lanciato di nuovo dal menu Start): chiedo all'istanza esistente di mostrare la finestra.
            if (!afterUpdate) show.Set();
            return 0;
        }
        try
        {
            Application.Run(new TrayContext(show, afterUpdate));
        }
        finally
        {
            single.ReleaseMutex();
        }
        return 0;
    }

    private static bool Acquire(Mutex m, int timeoutMs)
    {
        try { return m.WaitOne(timeoutMs); }
        catch (AbandonedMutexException) { return true; } // l'istanza precedente è uscita senza rilasciarlo
    }
}
