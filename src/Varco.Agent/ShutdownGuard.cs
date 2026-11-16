using System.Runtime.InteropServices;
using Varco.Core;

namespace Varco.Agent;

/// <summary>
/// Tiene in vita l'agent fino alla fine dell'arresto di Windows.
///
/// Quando si spegne o si riavvia, Windows chiude prima le app dell'utente. Se una non si chiude, compare la schermata
/// "Chiusura app in corso" con "Arresta comunque" / "Annulla", sul desktop sicuro (come la schermata di blocco). Per
/// premere quei pulsanti dagli altri PC l'agent deve essere ancora vivo e collegato: si chiude solo quando il servizio
/// riceve l'arresto dei servizi, cioè dopo che le app dell'utente sono state chiuse.
///
/// - Livello di arresto 0x100: il più basso permesso alle app, quindi Windows chiude l'agent per ultimo.
/// - Gestore degli eventi di console: un processo SYSTEM riceve CTRL_LOGOFF_EVENT (uscita dell'utente, anche durante
///   lo spegnimento) e CTRL_SHUTDOWN_EVENT. Senza un gestore che risponde "gestito", il gestore predefinito di Windows
///   chiuderebbe il processo. L'agent li ignora: a chiuderlo ci pensa il servizio.
/// </summary>
internal static class ShutdownGuard
{
    private const uint LastApplicationLevel = 0x100;
    private const int CTRL_LOGOFF_EVENT = 5, CTRL_SHUTDOWN_EVENT = 6;

    private delegate bool HandlerRoutine(int ctrlType);
    private static readonly HandlerRoutine Handler = OnConsoleEvent; // riferimento tenuto vivo: Windows lo chiama per tutta la vita del processo

    public static void Install()
    {
        if (!SetProcessShutdownParameters(LastApplicationLevel, 0))
            Log.Warn($"Livello di arresto non impostato ({Marshal.GetLastWin32Error()})");
        if (!SetConsoleCtrlHandler(Handler, true))
            Log.Warn($"Gestore degli eventi di arresto non registrato ({Marshal.GetLastWin32Error()})");
    }

    private static bool OnConsoleEvent(int ctrlType)
    {
        switch (ctrlType)
        {
            case CTRL_LOGOFF_EVENT:
                Log.Info("Windows segnala l'uscita di un utente: l'agent resta attivo");
                return true;
            case CTRL_SHUTDOWN_EVENT:
                Log.Info("Windows segnala l'arresto del sistema: l'agent resta attivo finché lo chiude il servizio");
                return true;
            default:
                return false; // Ctrl+C e chiusura della console (solo con --console): come prima
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessShutdownParameters(uint level, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetConsoleCtrlHandler(HandlerRoutine handler, bool add);
}
