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

        if (args.Contains("--install")) return Installer.RunInstall();
        if (args.Contains("--uninstall")) return Installer.RunUninstallInteractive(quiet: args.Contains("--quiet"));

        // --tray forza l'icona anche fuori dalla cartella di installazione (sviluppo).
        if (!args.Contains("--tray") && !Installer.IsRunningFromInstallDir())
        {
            Application.Run(new SetupForm());
            return 0;
        }

        using var single = new Mutex(true, @"Local\Varco.Tray", out var first);
        using var show = new EventWaitHandle(false, EventResetMode.AutoReset, TrayContext.ShowEventName);
        if (!first)
        {
            // Già aperto (es. lanciato di nuovo dal menu Start): chiedo all'istanza esistente di mostrare la finestra.
            show.Set();
            return 0;
        }
        Application.Run(new TrayContext(show));
        return 0;
    }
}
