using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Varco.Core.Net;

/// <summary>
/// Marca il traffico di una connessione come "voce" con qWAVE (DSCP 46 / priorità 802.1p 6).
/// Sul Wi-Fi la scheda e il router lo mettono nella coda WMM più veloce (AC_VO), la stessa delle chiamate:
/// i movimenti del mouse non aspettano dietro download, streaming o aggiornamenti di Windows.
/// Sul cavo non cambia nulla di male. Se non è possibile (niente qWAVE, niente diritti) si va avanti senza.
/// </summary>
internal sealed class SocketQos : IDisposable
{
    private static readonly object Gate = new();
    private static IntPtr _handle;
    private static bool _unavailable;

    private readonly Socket _socket;
    private uint _flowId;

    private SocketQos(Socket socket, uint flowId)
    {
        _socket = socket;
        _flowId = flowId;
    }

    /// <summary>Da chiamare su un socket TCP già connesso. Restituisce null se la marcatura non è disponibile.</summary>
    public static SocketQos? TryApply(Socket socket)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            var h = Handle();
            if (h == IntPtr.Zero) return null;
            uint flowId = 0;
            if (!QOSAddSocketToFlow(h, socket.Handle, IntPtr.Zero, QOSTrafficTypeVoice, QOS_NON_ADAPTIVE_FLOW, ref flowId))
            {
                Log.Debug($"QoS non applicata alla connessione ({Marshal.GetLastWin32Error()})");
                return null;
            }
            return new SocketQos(socket, flowId);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or ObjectDisposedException)
        {
            return null;
        }
    }

    private static IntPtr Handle()
    {
        lock (Gate)
        {
            if (_handle != IntPtr.Zero || _unavailable) return _handle;
            var v = new QOS_VERSION { MajorVersion = 1, MinorVersion = 0 };
            if (!QOSCreateHandle(ref v, out _handle))
            {
                _unavailable = true;
                _handle = IntPtr.Zero;
                Log.Debug($"qWAVE non disponibile ({Marshal.GetLastWin32Error()}): traffico senza priorità");
            }
            return _handle;
        }
    }

    public void Dispose()
    {
        var id = Interlocked.Exchange(ref _flowId, 0);
        if (id == 0) return;
        try { QOSRemoveSocketFromFlow(_handle, _socket.Handle, id, 0); } catch { }
    }

    // ───────────── P/Invoke qwave.dll ─────────────

    private const int QOSTrafficTypeVoice = 4;
    private const uint QOS_NON_ADAPTIVE_FLOW = 0x2;

    [StructLayout(LayoutKind.Sequential)]
    private struct QOS_VERSION
    {
        public ushort MajorVersion;
        public ushort MinorVersion;
    }

    [DllImport("qwave.dll", SetLastError = true)]
    private static extern bool QOSCreateHandle(ref QOS_VERSION version, out IntPtr qosHandle);

    [DllImport("qwave.dll", SetLastError = true)]
    private static extern bool QOSAddSocketToFlow(IntPtr qosHandle, IntPtr socket, IntPtr destAddr, int trafficType, uint flags, ref uint flowId);

    [DllImport("qwave.dll", SetLastError = true)]
    private static extern bool QOSRemoveSocketFromFlow(IntPtr qosHandle, IntPtr socket, uint flowId, uint flags);
}
