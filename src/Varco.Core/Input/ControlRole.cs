namespace Varco.Core.Input;

/// <summary>
/// Ruolo di questo PC per l'icona nella tray: master (lo si usa con il suo mouse e la sua tastiera, e magari comanda gli
/// altri) o slave (lo si usa da un altro PC). Slave resta tale anche quando il mouse torna sul PC che comanda o passa a
/// un terzo PC: finché nessuno tocca il mouse o la tastiera di questo PC, chi lo comandava l'ultima volta è ancora il
/// suo master. Torna master appena lo si usa da qui, o quando è lui a comandare un altro PC. Thread-safe.
/// </summary>
public sealed class ControlRole
{
    private readonly object _gate = new();
    private InputMode _mode = InputMode.Local;
    /// <summary>Quando questo PC ha cominciato a essere comandato l'ultima volta; null = usato da qui.</summary>
    private long? _controlledSince;
    private Guid? _controller;

    /// <summary>Chiamato a ogni cambio di modalità (anche con il lock del coordinatore: non blocca).</summary>
    public void OnModeChanged(InputMode mode, Guid? peer, long nowMs)
    {
        lock (_gate)
        {
            if (mode == InputMode.Controlled && (_mode != InputMode.Controlled || _controller != peer))
            {
                _controlledSince = nowMs;
                _controller = peer;
            }
            else if (mode == InputMode.Controlling)
            {
                _controlledSince = null;
                _controller = null;
            }
            _mode = mode;
        }
    }

    /// <summary>Il PC che comanda questo (adesso o l'ultima volta, se da allora nessuno l'ha toccato); null = master.</summary>
    /// <param name="lastPhysicalInputMs">Ultimo input fisico (non iniettato) visto su questo PC; 0 = mai.</param>
    public Guid? Controller(long lastPhysicalInputMs)
    {
        lock (_gate)
        {
            if (_mode == InputMode.Controlled) return _controller;
            return _controlledSince is { } since && lastPhysicalInputMs < since ? _controller : null;
        }
    }
}
