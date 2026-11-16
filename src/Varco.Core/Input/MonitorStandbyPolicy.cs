namespace Varco.Core.Input;

public enum StandbyAction { None, Standby, Wake }

/// <summary>
/// Quando mettere in standby i monitor di questo PC mentre si comanda un altro PC, e quando riaccenderli.
///
/// Windows non li spegne da solo: l'input fisico che Varco blocca e manda all'altro PC conta lo stesso come "utente
/// presente" (azzera il timer di inattività e riaccende anche un monitor spento con il comando di Windows; misurato).
/// Quindi Varco usa lo stesso tempo di spegnimento dello schermo impostato in Windows, contato da quando il controllo è
/// passato a un altro PC, e manda i monitor in standby lui (vedi MonitorStandby nell'agent, via DDC/CI).
/// Si riaccendono appena il controllo torna qui. Thread-safe.
/// </summary>
public sealed class MonitorStandbyPolicy
{
    private readonly object _gate = new();
    private long? _controllingSince;
    /// <summary>Standby già tentato in questo turno di controllo (riuscito o no): non si riprova a ogni giro.</summary>
    private bool _tried;
    private bool _asleep;
    private bool _wakePending;

    public bool Asleep { get { lock (_gate) return _asleep; } }

    /// <summary>Chiamato a ogni cambio di modalità (anche con il lock del coordinatore: non blocca).</summary>
    public void OnModeChanged(InputMode mode, long nowMs)
    {
        lock (_gate)
        {
            if (mode == InputMode.Controlling)
            {
                // Passare da un PC comandato all'altro non azzera il conto: lo schermo di qui resta inutilizzato.
                _controllingSince ??= nowMs;
                return;
            }
            _controllingSince = null;
            _tried = false;
            if (_asleep) _wakePending = true;
        }
    }

    /// <param name="timeoutSec">Tempo di spegnimento dello schermo impostato in Windows; 0 = mai.</param>
    /// <param name="displayRequired">Un programma chiede di tenere acceso lo schermo (video, presentazione).</param>
    public StandbyAction Decide(long nowMs, int timeoutSec, bool displayRequired, bool enabled)
    {
        lock (_gate)
        {
            if (_wakePending || (_asleep && _controllingSince is null)) return StandbyAction.Wake;
            if (_controllingSince is not { } since || _asleep || _tried || !enabled || timeoutSec <= 0 || displayRequired)
                return StandbyAction.None;
            return nowMs - since >= timeoutSec * 1000L ? StandbyAction.Standby : StandbyAction.None;
        }
    }

    /// <summary>Esito di <see cref="Decide"/>: per Standby, se almeno un monitor è andato in standby.</summary>
    public void Applied(StandbyAction action, bool anyAsleep = false)
    {
        lock (_gate)
        {
            switch (action)
            {
                case StandbyAction.Standby:
                    _tried = true;
                    _asleep = anyAsleep;
                    break;
                case StandbyAction.Wake:
                    _asleep = false;
                    _wakePending = false;
                    break;
            }
        }
    }
}
