using Varco.Core;
using static Varco.Agent.Native;

namespace Varco.Agent;

/// <summary>Avvisa quando il PC va in standby o si risveglia (senza bisogno di una finestra).</summary>
internal sealed class PowerNotifier : IDisposable
{
    private readonly DeviceNotifyCallback _callback; // va tenuto vivo finché la registrazione è attiva
    private IntPtr _handle;

    public event Action? Suspending;
    public event Action? Resumed;

    public PowerNotifier()
    {
        _callback = OnPower;
        var p = new DEVICE_NOTIFY_SUBSCRIBE_PARAMETERS { Callback = _callback, Context = IntPtr.Zero };
        var rc = PowerRegisterSuspendResumeNotification(DEVICE_NOTIFY_CALLBACK, ref p, out _handle);
        if (rc != 0) Log.Warn($"Registrazione notifiche di alimentazione fallita ({rc})");
    }

    private uint OnPower(IntPtr context, uint type, IntPtr setting)
    {
        try
        {
            switch ((int)type)
            {
                case PBT_APMSUSPEND:
                    Log.Info("Il sistema sta andando in standby");
                    Suspending?.Invoke();
                    break;
                case PBT_APMRESUMEAUTOMATIC:
                case PBT_APMRESUMESUSPEND:
                    Log.Info("Ripresa dallo standby");
                    Resumed?.Invoke();
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Notifica alimentazione", ex);
        }
        return 0;
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero) PowerUnregisterSuspendResumeNotification(_handle);
        _handle = IntPtr.Zero;
    }
}
