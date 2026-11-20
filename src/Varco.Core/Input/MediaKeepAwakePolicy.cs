namespace Varco.Core.Input;

/// <summary>
/// Quando questo PC è "remoto", cioè lo si usa da un altro PC con Varco e non con il suo mouse e la sua tastiera.
/// Su un PC remoto che sta suonando musica o un video lo schermo non deve spegnersi (e quindi bloccarsi) per inattività:
/// chi lo usa da lontano lo sta ascoltando o guardando, anche se non lo tocca. Windows invece lo spegne lo stesso con la
/// sola musica, perché tiene acceso lo schermo solo per i programmi che lo chiedono (i lettori video, non quelli audio).
///
/// Remoto: comandato in questo momento, oppure comandato l'ultima volta e da allora mai toccato fisicamente (il controllo
/// è passato a un altro PC o è tornato su quello che comandava). Non più remoto appena si usa il suo mouse o la sua
/// tastiera, o quando è lui a comandare un altro PC (allora la persona è qui). Thread-safe.
/// </summary>
public sealed class MediaKeepAwakePolicy
{
    private readonly object _gate = new();
    private InputMode _mode = InputMode.Local;
    /// <summary>Quando questo PC ha cominciato a essere comandato l'ultima volta; null = usato da qui.</summary>
    private long? _controlledSince;

    /// <summary>Chiamato a ogni cambio di modalità (anche con il lock del coordinatore: non blocca).</summary>
    public void OnModeChanged(InputMode mode, long nowMs)
    {
        lock (_gate)
        {
            if (mode == InputMode.Controlled && _mode != InputMode.Controlled) _controlledSince = nowMs;
            else if (mode == InputMode.Controlling) _controlledSince = null;
            _mode = mode;
        }
    }

    /// <param name="lastPhysicalInputMs">Ultimo input fisico (non iniettato) visto su questo PC; 0 = mai.</param>
    public bool IsRemote(long lastPhysicalInputMs)
    {
        lock (_gate)
            return _mode == InputMode.Controlled || (_controlledSince is { } since && lastPhysicalInputMs < since);
    }
}
