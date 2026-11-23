using System.Runtime.InteropServices;
using Varco.Core;

namespace Varco.Agent;

/// <summary>
/// Esce del suono da questo PC? Si legge il livello in uscita su ogni dispositivo audio attivo (altoparlanti, cuffie,
/// audio del monitor via HDMI/DisplayPort...) con IAudioMeterInformation: un lettore in pausa tiene aperto il flusso
/// audio, ma il livello è zero. Serve a non spegnere lo schermo mentre suona musica: spesso le casse sono collegate al
/// monitor (jack e alimentazione USB) e con lui si spengono anche loro.
/// </summary>
internal static class AudioOutput
{
    /// <summary>Letture del livello: le pause brevi della musica non devono sembrare silenzio (circa un secondo).</summary>
    private const int Samples = 20, SampleGapMs = 50;
    private const float AudibleLevel = 0.0005f;
    private static int _broken; // per non riempire il log se il livello non si legge

    /// <summary>Lento (fino a un secondo): solo da thread di lavoro, mai da quello degli hook.</summary>
    /// <param name="ignoreOwnProcess">
    /// Non conta il suono riprodotto da Varco stesso (l'audio degli altri PC, sul PC centrale): si leggono i livelli delle
    /// singole sessioni audio, saltando quelle di questo processo.
    /// </param>
    public static bool IsPlaying(Func<bool>? stop = null, bool ignoreOwnProcess = false)
    {
        var meters = new List<IAudioMeterInformation>();
        try
        {
            // Una sola classe COM per CLSID nell'assembly (vedi Wasapi): due dichiarazioni dello stesso enumeratore fanno fallire il cast.
            var enumerator = (IMMDeviceEnumerator)Wasapi.CreateEnumerator();
            try
            {
                Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATE_ACTIVE, out var devices));
                try
                {
                    Marshal.ThrowExceptionForHR(devices.GetCount(out var count));
                    for (uint i = 0; i < count; i++)
                    {
                        if (devices.Item(i, out var device) != 0) continue;
                        try
                        {
                            if (ignoreOwnProcess) AddOtherSessionMeters(device, meters);
                            else
                            {
                                var iid = typeof(IAudioMeterInformation).GUID;
                                if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var meter) == 0 && meter is IAudioMeterInformation m)
                                    meters.Add(m);
                            }
                        }
                        finally { Marshal.ReleaseComObject(device); }
                    }
                }
                finally { Marshal.ReleaseComObject(devices); }
            }
            finally { Marshal.ReleaseComObject(enumerator); }

            Interlocked.Exchange(ref _broken, 0);
            for (var s = 0; s < Samples; s++)
            {
                foreach (var m in meters)
                    if (m.GetPeakValue(out var peak) == 0 && peak > AudibleLevel) return true;
                if (stop?.Invoke() == true) return false;
                Thread.Sleep(SampleGapMs);
            }
            return false;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            if (Interlocked.Exchange(ref _broken, 1) == 0) Log.Warn($"Livello dell'audio non leggibile: {ex.Message}");
            return false;
        }
        finally
        {
            foreach (var m in meters) Marshal.ReleaseComObject(m);
        }
    }

    /// <summary>I livelli delle sessioni audio del dispositivo, tranne quelle di questo processo.</summary>
    private static void AddOtherSessionMeters(IMMDevice device, List<IAudioMeterInformation> meters)
    {
        var iid = Wasapi.IID_IAudioSessionManager2;
        if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj) != 0 || obj is not Wasapi.IAudioSessionManager2 manager) return;
        try
        {
            if (manager.GetSessionEnumerator(out var sessions) != 0) return;
            try
            {
                if (sessions.GetCount(out var count) != 0) return;
                for (var i = 0; i < count; i++)
                {
                    if (sessions.GetSession(i, out var session) != 0) continue;
                    if (session.GetProcessId(out var pid) == 0 && pid != Environment.ProcessId && session is IAudioMeterInformation m)
                        meters.Add(m); // stesso oggetto COM: rilasciato con i livelli
                    else Marshal.ReleaseComObject(session);
                }
            }
            finally { Marshal.ReleaseComObject(sessions); }
        }
        finally { Marshal.ReleaseComObject(manager); }
    }

    // ───────────── interop ─────────────

    private const uint DEVICE_STATE_ACTIVE = 0x1;
    private const int CLSCTX_ALL = 0x17;

    private enum EDataFlow { eRender, eCapture, eAll }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        // Gli altri metodi non servono e non vanno chiamati (la tabella dei metodi finisce qui).
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
