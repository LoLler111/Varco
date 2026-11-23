using System.Runtime.InteropServices;

namespace Varco.Agent;

/// <summary>
/// Interop WASAPI minimo per l'audio fra i PC: cattura in loopback di ciò che esce dalle casse, riproduzione con un nome
/// proprio nel Mixer volume, muto dell'uscita. Formato fisso 48 kHz stereo float: con AUTOCONVERTPCM ci pensa Windows a
/// convertire da e verso il formato del dispositivo (44,1 kHz, 5.1...).
/// </summary>
internal static class Wasapi
{
    public const int SampleRate = 48000, Channels = 2;

    public const int AUDCLNT_E_DEVICE_INVALIDATED = unchecked((int)0x88890004);
    public const uint AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
    public const uint AUDCLNT_STREAMFLAGS_EVENTCALLBACK = 0x00040000;
    public const uint AUDCLNT_SESSIONFLAGS_DISPLAY_HIDE = 0x20000000;
    public const uint AUDCLNT_STREAMFLAGS_NOPERSIST = 0x00080000;
    public const uint AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
    public const uint AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = 0x80000000;
    public const uint AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
    public const int CLSCTX_ALL = 0x17;

    public static readonly Guid IID_IAudioClient = new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
    public static readonly Guid IID_IAudioCaptureClient = new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
    public static readonly Guid IID_IAudioRenderClient = new("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
    public static readonly Guid IID_IAudioSessionControl = new("F4B1A599-7266-4319-A8CA-E70ACB11E8CD");
    public static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    public static readonly Guid IID_IAudioSessionManager2 = new("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
    public static readonly Guid IID_IAudioMeterInformation = new("C02216F6-8C67-4B5B-9D00-D008E73E0064");

    public enum EDataFlow { eRender, eCapture, eAll }
    public enum ERole { eConsole, eMultimedia, eCommunications }

    /// <summary>WAVEFORMATEXTENSIBLE: 48 kHz, stereo, float 32 bit.</summary>
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    public struct WaveFormatExtensible
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
        public ushort wValidBitsPerSample;
        public uint dwChannelMask;
        public Guid SubFormat;

        public static WaveFormatExtensible StereoFloat48k() => new()
        {
            wFormatTag = 0xFFFE,
            nChannels = Channels,
            nSamplesPerSec = SampleRate,
            nAvgBytesPerSec = SampleRate * Channels * 4,
            nBlockAlign = Channels * 4,
            wBitsPerSample = 32,
            cbSize = 22,
            wValidBitsPerSample = 32,
            dwChannelMask = 0x3, // anteriore sinistro + destro
            SubFormat = new Guid("00000003-0000-0010-8000-00aa00389b71"), // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT
        };
    }

    public static IMMDeviceEnumerator CreateEnumerator() => (IMMDeviceEnumerator)new MMDeviceEnumerator();

    /// <summary>Il dispositivo predefinito per musica e video, o null se non ce n'è (nessuna uscita/ingresso attivo).</summary>
    public static IMMDevice? DefaultDevice(IMMDeviceEnumerator enumerator, EDataFlow flow)
    {
        var hr = enumerator.GetDefaultAudioEndpoint(flow, ERole.eMultimedia, out var device);
        return hr == 0 ? device : null;
    }

    /// <summary>Id del dispositivo predefinito ("" se non ce n'è): cambia quando l'utente passa dalle casse alle cuffie.</summary>
    public static string DefaultDeviceId(IMMDeviceEnumerator enumerator, EDataFlow flow)
    {
        var device = DefaultDevice(enumerator, flow);
        if (device is null) return "";
        try { return device.GetId(out var id) == 0 ? id : ""; }
        finally { Marshal.ReleaseComObject(device); }
    }

    public static T Activate<T>(IMMDevice device, Guid iid) where T : class
    {
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj));
        return (T)obj;
    }

    public static T GetService<T>(IAudioClient client, Guid iid) where T : class
    {
        Marshal.ThrowExceptionForHR(client.GetService(ref iid, out var obj));
        return (T)obj;
    }

    /// <summary>Inizializza in modalità condivisa a 48 kHz stereo float (Windows converte da e verso il dispositivo).</summary>
    public static unsafe void Initialize(IAudioClient client, uint extraFlags, long bufferHns, Guid session)
    {
        var fmt = WaveFormatExtensible.StereoFloat48k();
        var flags = AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM | AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY | extraFlags;
        Marshal.ThrowExceptionForHR(client.Initialize(0 /* AUDCLNT_SHAREMODE_SHARED */, flags, bufferHns, 0, (IntPtr)(&fmt), ref session));
    }

    public static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    // ───────────── interfacce COM (solo i metodi usati, nell'ordine della vtable) ─────────────

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioClient
    {
        [PreserveSig] int Initialize(int shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, ref Guid audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint frames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint frames);
        [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
        [PreserveSig] int GetMixFormat(out IntPtr format);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr handle);
        [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioCaptureClient
    {
        [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition);
        [PreserveSig] int ReleaseBuffer(uint frames);
        [PreserveSig] int GetNextPacketSize(out uint frames);
    }

    [ComImport, Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioRenderClient
    {
        [PreserveSig] int GetBuffer(uint frames, out IntPtr data);
        [PreserveSig] int ReleaseBuffer(uint frames, uint flags);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionControl2
    {
        // IAudioSessionControl
        [PreserveSig] int GetState(out int state);
        [PreserveSig] int GetDisplayName(out IntPtr name);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string name, ref Guid eventContext);
        [PreserveSig] int GetIconPath(out IntPtr path);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string path, ref Guid eventContext);
        [PreserveSig] int GetGroupingParam(out Guid grouping);
        [PreserveSig] int SetGroupingParam(ref Guid grouping, ref Guid eventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr client);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr client);
        // IAudioSessionControl2
        [PreserveSig] int GetSessionIdentifier(out IntPtr id);
        [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr id);
        [PreserveSig] int GetProcessId(out uint pid);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(IntPtr sessionGuid, uint flags, out IntPtr control);
        [PreserveSig] int GetSimpleAudioVolume(IntPtr sessionGuid, uint flags, out IntPtr volume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator sessions);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int count);
        [PreserveSig] int GetSession(int index, out IAudioSessionControl2 session);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float peak);
    }
}
