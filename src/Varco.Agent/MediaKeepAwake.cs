using System.Runtime.InteropServices;
using Varco.Core;
using Varco.Core.Input;

namespace Varco.Agent;

/// <summary>
/// Sul PC remoto (vedi <see cref="MediaKeepAwakePolicy"/>) che sta suonando musica o un video, lo schermo non si spegne
/// e non si blocca per inattività. Windows lo spegnerebbe lo stesso con la sola musica (tiene sveglio il PC, non lo
/// schermo), e se le casse sono collegate al monitor si spengono anche loro. Fa come un lettore video: azzera il timer
/// di inattività dello schermo e del sistema (SetThreadExecutionState senza ES_CONTINUOUS, cioè una volta sola, come un
/// tocco del mouse). Finita la musica Windows riparte a contare da lì, con i suoi tempi.
///
/// Un video senza audio non si sente (vedi <see cref="AudioOutput"/>): per quello basta Windows, perché i lettori video
/// chiedono già da soli di tenere acceso lo schermo.
/// </summary>
internal sealed class MediaKeepAwake : IDisposable
{
    /// <summary>Ogni quanto si controlla: molto meno del minimo spegnimento dello schermo di Windows (1 minuto).</summary>
    private const int CheckMs = 10_000;

    private readonly MediaKeepAwakePolicy _policy = new();
    private readonly Func<bool> _enabled;
    private readonly Func<long> _lastPhysicalInput;
    private readonly AutoResetEvent _signal = new(false);
    private readonly Thread _thread;
    private volatile bool _stop;
    private bool _active; // solo il thread di lavoro

    /// <param name="enabled">Impostazione "schermo acceso mentre suona musica o un video".</param>
    /// <param name="lastPhysicalInput">Ultimo input fisico (non iniettato) visto dagli hook, in ms di TickCount64.</param>
    public MediaKeepAwake(Func<bool> enabled, Func<long> lastPhysicalInput)
    {
        _enabled = enabled;
        _lastPhysicalInput = lastPhysicalInput;
        _thread = new Thread(Run) { IsBackground = true, Name = "Varco.MediaKeepAwake" };
        _thread.Start();
    }

    /// <summary>Dal coordinatore, con il suo lock: non blocca.</summary>
    public void OnModeChanged(InputMode mode, Guid? _)
    {
        _policy.OnModeChanged(mode, Environment.TickCount64);
        if (mode != InputMode.Controlled) _signal.Set(); // controllo subito: il controllo è appena passato altrove
    }

    private void Run()
    {
        while (!_stop)
        {
            _signal.WaitOne(CheckMs);
            if (_stop) break;
            try { Tick(); } catch (Exception ex) { Log.Error("Schermo acceso durante la riproduzione", ex); }
        }
    }

    private void Tick()
    {
        var keep = _enabled() && _policy.IsRemote(_lastPhysicalInput()) && AudioOutput.IsPlaying(() => _stop);
        if (keep) SetThreadExecutionState(ES_SYSTEM_REQUIRED | ES_DISPLAY_REQUIRED);
        if (keep == _active) return;
        _active = keep;
        Log.Info(keep
            ? "Musica o video in riproduzione su questo PC usato da un altro PC: lo schermo resta acceso"
            : "Riproduzione finita o PC di nuovo usato da qui: spegnimento dello schermo con i tempi di Windows");
    }

    public void Dispose()
    {
        _stop = true;
        _signal.Set();
        _thread.Join(2_000);
    }

    private const uint ES_SYSTEM_REQUIRED = 0x1, ES_DISPLAY_REQUIRED = 0x2;

    [DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint flags);
}
