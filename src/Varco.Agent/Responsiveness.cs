using System.Diagnostics;
using System.Runtime.InteropServices;
using Varco.Core;
using Varco.Core.Input;

namespace Varco.Agent;

/// <summary>
/// Tempi di risposta da periferica USB per l'agent:
///  • priorità di processo alta: con il PC sotto carico (giochi, compilazioni) gli hook e l'iniezione non aspettano il proprio turno;
///  • niente EcoQoS / "risparmio energia" sul processo: Windows 11 rallenta i processi senza finestre visibili, soprattutto a batteria;
///  • timer di sistema a 1 ms solo mentre si comanda o si è comandati: di base Windows conta il tempo a scatti di 15,6 ms,
///    e i movimenti raggruppati e i ping ravvicinati arriverebbero in ritardo e a gruppi. Fuori dal controllo si torna al
///    valore normale per non consumare batteria.
/// </summary>
internal sealed class Responsiveness : IDisposable
{
    private readonly object _gate = new();
    private bool _fastTimer;

    public static void ApplyProcessPriority()
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            me.PriorityClass = ProcessPriorityClass.High;
        }
        catch (Exception ex) { Log.Debug($"Priorità di processo non impostata: {ex.Message}"); }

        try
        {
            // ControlMask = cosa gestisco io, StateMask = 0 → "mai rallentare" per entrambe le voci.
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED | PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION,
                StateMask = 0,
            };
            var size = Marshal.SizeOf<PROCESS_POWER_THROTTLING_STATE>();
            if (!SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, size))
            {
                // Windows 10 prima del 21H2 non conosce IGNORE_TIMER_RESOLUTION: almeno la velocità di esecuzione.
                state.ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED;
                if (!SetProcessInformation(GetCurrentProcess(), ProcessPowerThrottling, ref state, size))
                    Log.Debug($"SetProcessInformation(PowerThrottling) fallito ({Marshal.GetLastWin32Error()})");
            }
        }
        catch (Exception ex) { Log.Debug($"Power throttling non disattivato: {ex.Message}"); }
    }

    /// <summary>Segue la modalità di input: timer a 1 ms fuori da <see cref="InputMode.Local"/>.</summary>
    public void OnModeChanged(InputMode mode, Guid? _) => SetFastTimer(mode != InputMode.Local);

    private void SetFastTimer(bool on)
    {
        lock (_gate)
        {
            if (on == _fastTimer) return;
            _fastTimer = on;
            try
            {
                if (on) timeBeginPeriod(1); else timeEndPeriod(1);
            }
            catch (Exception ex) { Log.Debug($"timeBeginPeriod: {ex.Message}"); }
        }
    }

    public void Dispose() => SetFastTimer(false);

    // ───────────── P/Invoke ─────────────

    private const int ProcessPowerThrottling = 4;
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;
    private const uint PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION = 0x4;

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr hProcess, int processInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE processInformation, int processInformationSize);

    [DllImport("winmm.dll")]
    private static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    private static extern uint timeEndPeriod(uint uPeriod);
}
