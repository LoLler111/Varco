using System.Runtime.InteropServices;
using Varco.Core;
using Varco.Core.Input;
using static Varco.Agent.Native;

namespace Varco.Agent;

/// <summary>
/// Mette in standby i monitor di questo PC quando da un po' si sta comandando un altro PC (il "quando" lo decide
/// <see cref="MonitorStandbyPolicy"/>), e li riaccende appena il controllo torna qui.
///
/// Il comando di spegnimento di Windows (SC_MONITORPOWER) non basta: il primo tasto bloccato e mandato all'altro PC
/// riaccenderebbe il monitor. Si usa invece DDC/CI, il canale con cui il PC parla al monitor sul cavo video: codice VCP
/// D6 (modalità di alimentazione), 4 = spento in attesa di un comando, 1 = acceso. Mai 5, che si riaccende solo col tasto.
/// Windows non sa niente di questo standby, quindi l'input non lo interrompe. Funziona con i monitor esterni che hanno
/// DDC/CI attivo; gli altri (lo schermo dei portatili) restano accesi.
///
/// Riaccensione: alcuni monitor in standby rispondono a singhiozzo e ignorano i primi comandi (misurato: uno subito,
/// un altro dopo 4-8 tentativi, cioè 4-9 secondi). Quindi il comando si ripete finché il monitor non dice "acceso",
/// che è affidabile. Finché ci sono monitor in standby esiste un file segnaposto: se l'agent si chiude male, quello
/// nuovo li riaccende all'avvio (Windows non sa che sono spenti: muovere il mouse non basterebbe, servirebbe il tasto).
///
/// Le chiamate DDC/CI sono lente (decine di ms per monitor): girano su un thread a parte, mai su quello degli hook.
/// </summary>
internal sealed class MonitorStandby : IDisposable
{
    private const int CheckMs = 5000;
    private const byte VCP_POWER_MODE = 0xD6;
    private const uint PowerOn = 1, PowerOff = 4;
    /// <summary>Tentativi di riaccensione, uno al secondo circa (gli stessi tempi della prova che ha funzionato).</summary>
    private const int WakeAttempts = 30;
    private const int WakeReadDelayMs = 300, WakeRetryDelayMs = 700;

    private readonly MonitorStandbyPolicy _policy = new();
    private readonly Func<bool> _enabled;
    private readonly Func<bool> _audioKeepsOn;
    private readonly string _markerPath;
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private volatile bool _stop;
    /// <summary>Monitor mandati in standby da noi (solo il thread di lavoro li tocca).</summary>
    private List<PhysicalMonitor> _asleep = [];
    /// <summary>Standby rimandato perché suona qualcosa (solo per scriverlo una volta nel log).</summary>
    private bool _heldByAudio;

    private readonly record struct PhysicalMonitor(IntPtr Handle, string Name);

    /// <param name="markerPath">File che esiste finché ci sono monitor in standby (vedi sopra).</param>
    /// <param name="audioKeepsOn">Impostazione "schermo acceso mentre suona musica o un video".</param>
    public MonitorStandby(Func<bool> enabled, Func<bool> audioKeepsOn, string markerPath)
    {
        _enabled = enabled;
        _audioKeepsOn = audioKeepsOn;
        _markerPath = markerPath;
        _thread = new Thread(Run) { IsBackground = true, Name = "Varco.MonitorStandby" };
        _thread.Start();
    }

    /// <summary>Dal coordinatore, con il suo lock: non blocca.</summary>
    public void OnModeChanged(InputMode mode, Guid? _)
    {
        _policy.OnModeChanged(mode, Environment.TickCount64);
        if (mode != InputMode.Controlling) _signal.Set(); // riaccensione subito, non al prossimo giro
    }

    private void Run()
    {
        try { WakeLeftovers(); } catch (Exception ex) { Log.Error("Standby dei monitor", ex); }
        while (!_stop)
        {
            _signal.WaitOne(CheckMs);
            if (_stop) break;
            try { Tick(); } catch (Exception ex) { Log.Error("Standby dei monitor", ex); }
        }
        try { if (_asleep.Count > 0) Wake("chiusura dell'agent"); } catch (Exception ex) { Log.Error("Standby dei monitor", ex); }
    }

    private void Tick()
    {
        var enabled = _enabled();
        var action = _policy.Decide(Environment.TickCount64, enabled ? DisplayTimeoutSeconds() : 0, DisplayRequired(), enabled);
        // Musica in corso: niente standby, spesso le casse sono collegate al monitor e si spegnerebbero con lui.
        // Si legge solo quando sarebbe ora (la lettura dura fino a un secondo); senza Applied si ricontrolla al prossimo giro.
        var playing = action == StandbyAction.Standby && _audioKeepsOn() && AudioOutput.IsPlaying(() => _stop);
        if (playing && !_heldByAudio)
            Log.Info("Si sta comandando un altro PC, ma su questo suona musica o un video: i monitor restano accesi");
        _heldByAudio = playing;
        if (playing) return;
        switch (action)
        {
            case StandbyAction.Standby: Standby(); break;
            case StandbyAction.Wake: Wake("il controllo è tornato su questo PC"); break;
        }
    }

    private void Standby()
    {
        var names = new List<string>();
        foreach (var m in PhysicalMonitors())
        {
            if (GetPowerMode(m.Handle) >= 1 && SetVCPFeature(m.Handle, VCP_POWER_MODE, PowerOff))
            {
                _asleep.Add(m);
                names.Add(m.Name);
            }
            else DestroyPhysicalMonitor(m.Handle);
        }
        if (names.Count > 0)
        {
            WriteMarker(names);
            Log.Info($"Si sta comandando un altro PC: monitor in standby ({string.Join(", ", names)})");
        }
        else Log.Info("Si sta comandando un altro PC, ma nessun monitor accetta lo standby via DDC/CI: restano accesi");
        _policy.Applied(StandbyAction.Standby, names.Count > 0);
    }

    private void Wake(string why)
    {
        var asleep = _asleep;
        _asleep = [];
        var left = WakeWithRetries(asleep, WakeAttempts);
        foreach (var m in asleep) DestroyPhysicalMonitor(m.Handle);
        if (left.Count > 0)
        {
            // Handle forse non più validi (schermi cambiati nel frattempo): riprovo con i monitor appena letti.
            // "Acceso" a un monitor già acceso non fa nulla, e lui lo conferma subito.
            var fresh = PhysicalMonitors();
            left = WakeWithRetries(fresh, WakeAttempts / 3);
            foreach (var m in fresh) DestroyPhysicalMonitor(m.Handle);
        }
        if (left.Count == 0) Log.Info($"Monitor riaccesi ({why})");
        else Log.Warn($"Monitor che non confermano la riaccensione ({why}): {string.Join(", ", left.Select(m => m.Name))}");
        DeleteMarker();
        _policy.Applied(StandbyAction.Wake);
    }

    /// <summary>Manda "acceso" finché ogni monitor non lo conferma. Restituisce quelli che non l'hanno confermato.</summary>
    private static List<PhysicalMonitor> WakeWithRetries(List<PhysicalMonitor> monitors, int attempts)
    {
        var pending = new List<PhysicalMonitor>(monitors);
        for (var attempt = 1; attempt <= attempts && pending.Count > 0; attempt++)
        {
            foreach (var m in pending.ToArray())
            {
                SetVCPFeature(m.Handle, VCP_POWER_MODE, PowerOn);
                Thread.Sleep(WakeReadDelayMs);
                if (GetPowerMode(m.Handle) != PowerOn) continue;
                pending.Remove(m);
                if (attempt > 1) Log.Info($"Monitor {m.Name} riacceso al tentativo {attempt}");
            }
            if (pending.Count > 0) Thread.Sleep(WakeRetryDelayMs);
        }
        return pending;
    }

    /// <summary>All'avvio: monitor rimasti in standby da un agent chiuso male (c'è ancora il file segnaposto).</summary>
    private void WakeLeftovers()
    {
        if (!File.Exists(_markerPath)) return;
        Log.Info("Monitor lasciati in standby da un agent chiuso male: li riaccendo");
        var monitors = PhysicalMonitors();
        var left = WakeWithRetries(monitors, WakeAttempts);
        foreach (var m in monitors) DestroyPhysicalMonitor(m.Handle);
        if (left.Count > 0) Log.Warn($"Monitor che non confermano la riaccensione: {string.Join(", ", left.Select(m => m.Name))}");
        DeleteMarker();
    }

    private void WriteMarker(List<string> names)
    {
        try { File.WriteAllLines(_markerPath, names); }
        catch (Exception ex) { Log.Warn($"File segnaposto dello standby non scritto: {ex.Message}"); }
    }

    private void DeleteMarker()
    {
        try { File.Delete(_markerPath); }
        catch (Exception ex) { Log.Warn($"File segnaposto dello standby non cancellato: {ex.Message}"); }
    }

    private static List<PhysicalMonitor> PhysicalMonitors()
    {
        var list = new List<PhysicalMonitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr _, ref RECT _, IntPtr _) =>
        {
            if (GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, out var n) && n > 0)
            {
                var a = new PHYSICAL_MONITOR[n];
                if (GetPhysicalMonitorsFromHMONITOR(hMon, n, a))
                    foreach (var p in a) list.Add(new PhysicalMonitor(p.hPhysicalMonitor, p.szPhysicalMonitorDescription.Trim()));
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Modalità di alimentazione (1 acceso, 2-4 standby/spento), o -1 se il monitor non risponde al DDC/CI.</summary>
    private static long GetPowerMode(IntPtr monitor) =>
        GetVCPFeatureAndVCPFeatureReply(monitor, VCP_POWER_MODE, out _, out var current, out _) ? current : -1;

    // ───────────── impostazioni di alimentazione di Windows ─────────────

    private static readonly Guid VideoSubgroup = new("7516b95f-f776-4464-8c53-06167f40cc99");
    private static readonly Guid VideoPowerdownTimeout = new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e");

    /// <summary>"Spegni lo schermo dopo" del piano attivo, con l'alimentatore o a batteria; 0 = mai (o non leggibile).</summary>
    private static int DisplayTimeoutSeconds()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var schemePtr) != 0) return 0;
        try
        {
            var scheme = Marshal.PtrToStructure<Guid>(schemePtr);
            var sub = VideoSubgroup;
            var setting = VideoPowerdownTimeout;
            var onBattery = GetSystemPowerStatus(out var s) && s.ACLineStatus == 0;
            uint seconds;
            var rc = onBattery
                ? PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out seconds)
                : PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out seconds);
            return rc == 0 ? (int)Math.Min(seconds, int.MaxValue) : 0;
        }
        finally
        {
            LocalFree(schemePtr);
        }
    }

    /// <summary>
    /// Un programma chiede di tenere acceso lo schermo (video, presentazione): come Windows, niente standby.
    /// Lo stato di esecuzione del sistema comprende le richieste di tutti i processi (misurato).
    /// </summary>
    private static bool DisplayRequired() =>
        CallNtPowerInformation(SystemExecutionState, IntPtr.Zero, 0, out var state, sizeof(uint)) == 0
        && (state & ES_DISPLAY_REQUIRED) != 0;

    public void Dispose()
    {
        _stop = true;
        _signal.Set();
        // Riaccende i monitor prima di uscire (vedi Run). Se non fa in tempo, li riaccende il prossimo agent (file segnaposto).
        _thread.Join(15_000);
    }

    // ───────────── interop ─────────────

    private const int SystemExecutionState = 16;
    private const uint ES_DISPLAY_REQUIRED = 0x2;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint count, [Out] PHYSICAL_MONITOR[] monitors);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool DestroyPhysicalMonitor(IntPtr monitor);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr monitor, byte code, out uint type, out uint current, out uint maximum);
    [DllImport("dxva2.dll", SetLastError = true)]
    private static extern bool SetVCPFeature(IntPtr monitor, byte code, uint value);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);
    [DllImport("powrprof.dll")]
    private static extern uint PowerReadACValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")]
    private static extern uint PowerReadDCValueIndex(IntPtr rootPowerKey, ref Guid scheme, ref Guid subgroup, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(int level, IntPtr input, uint inputLength, out uint output, uint outputLength);
    [DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr mem);
}
