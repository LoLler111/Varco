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
    public static bool IsPlaying(Func<bool>? stop = null)
    {
        var meters = new List<IAudioMeterInformation>();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
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
                            var iid = typeof(IAudioMeterInformation).GUID;
                            if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var meter) == 0 && meter is IAudioMeterInformation m)
                                meters.Add(m);
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

    // ───────────── interop ─────────────

    private const uint DEVICE_STATE_ACTIVE = 0x1;
    private const int CLSCTX_ALL = 0x17;

    private enum EDataFlow { eRender, eCapture, eAll }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

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
