using System.Runtime.InteropServices;
using Varco.Core;

namespace Varco.Agent;

/// <summary>
/// Nasconde il cursore del PC che comanda: mentre mouse e tastiera lavorano su un altro PC, qui il cursore resta
/// parcheggiato al centro dello schermo e non serve vederlo.
///
/// Non si toccano i cursori di sistema (SetSystemCursor): se Varco si chiudesse male resterebbero invisibili fino al
/// prossimo accesso. Si mette invece sotto il cursore una piccola finestra trasparente, sempre in primo piano, che dice
/// "nessun cursore" (WM_SETCURSOR → SetCursor(NULL)). Il cursore è fermo perché l'hook blocca il mouse fisico, quindi
/// non esce mai dalla finestra. Se la finestra sparisce (Varco chiuso, crash) il cursore torna visibile da solo.
///
/// La finestra vive sul thread degli hook, agganciato al desktop di input: quando il desktop cambia (schermata di blocco,
/// UAC) il thread viene ricreato e la finestra con lui.
/// </summary>
internal sealed class CursorHider : IDisposable
{
    private const int Size = 64;
    private const string ClassName = "Varco.CursorHider";
    private const int WM_SETCURSOR = 0x0020, WM_MOUSEACTIVATE = 0x0021, MA_NOACTIVATE = 3;
    private const int WM_QUERYENDSESSION = 0x0011, WM_ENDSESSION = 0x0016;
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_LAYERED = 0x80000, WS_EX_NOACTIVATE = 0x08000000;
    private const uint SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    private const uint LWA_ALPHA = 0x2;
    private const int SW_HIDE = 0;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private static readonly WndProcDelegate Proc = WindowProc; // riferimento tenuto vivo: Windows lo chiama per tutta la vita del processo
    private static readonly object ClassGate = new();
    private static bool _registered;

    private IntPtr _hwnd;

    public bool Visible { get; private set; }

    public CursorHider()
    {
        var module = Native.GetModuleHandle(null);
        lock (ClassGate)
        {
            if (!_registered)
            {
                var wc = new WNDCLASSEX
                {
                    cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                    hInstance = module,
                    hbrBackground = GetStockObject(4), // BLACK_BRUSH: con alfa 1/255 resta invisibile
                    lpszClassName = ClassName,
                };
                if (RegisterClassEx(ref wc) == 0) throw new System.ComponentModel.Win32Exception();
                _registered = true;
            }
        }
        // Le classi sono del processo, le finestre del desktop del thread che le crea: qui, quello di input.
        _hwnd = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE, ClassName, "Varco",
            WS_POPUP, 0, 0, Size, Size, IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        // Alfa 1 su 255: invisibile, ma il punto sotto il cursore è suo (con alfa 0 il mouse "passerebbe attraverso").
        SetLayeredWindowAttributes(_hwnd, 0, 1, LWA_ALPHA);
    }

    /// <summary>Mostra la finestra centrata sul cursore (anche per rimetterla in primo piano, se qualcuno le è passato sopra).</summary>
    public void Show()
    {
        if (Native.GetCursorPos(out var p)) ShowAt(p.X, p.Y);
    }

    private void ShowAt(int x, int y) => ShowRect(x - Size / 2, y - Size / 2, Size, Size);

    /// <summary>
    /// Mostra la finestra abbastanza grande da coprire tutti e due i punti: il cursore, dopo essere sparito nel primo,
    /// può andare nel secondo senza uscire dalla finestra (vedi WindowsInput.ParkCursor).
    /// </summary>
    public void Cover(int x1, int y1, int x2, int y2)
    {
        int left = Math.Min(x1, x2) - Size / 2, top = Math.Min(y1, y2) - Size / 2;
        ShowRect(left, top, Math.Max(x1, x2) + Size / 2 - left, Math.Max(y1, y2) + Size / 2 - top);
    }

    /// <summary>
    /// Il cursore è davvero sparito? Windows applica "nessun cursore" solo quando la nostra finestra gli risponde,
    /// qualche millisecondo dopo che il cursore le è arrivato sopra. Da lì in poi resta invisibile anche se si sposta,
    /// purché non esca dalla finestra.
    /// </summary>
    public static bool CursorGone()
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        return GetCursorInfo(ref ci) && (ci.flags == 0 || ci.hCursor == IntPtr.Zero);
    }

    private void ShowRect(int x, int y, int w, int h)
    {
        if (_hwnd == IntPtr.Zero) return;
        SetWindowPos(_hwnd, HWND_TOPMOST, x, y, w, h, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        if (!Visible) Log.Debug("Cursore nascosto");
        Visible = true;
        // Il cursore è fermo, quindi Windows non chiederebbe subito la forma: la imposto io (vale perché è sopra una nostra finestra).
        SetCursor(IntPtr.Zero);
    }

    public void Hide()
    {
        if (_hwnd == IntPtr.Zero || !Visible) return;
        ShowWindow(_hwnd, SW_HIDE);
        Visible = false;
        Log.Debug("Cursore di nuovo visibile");
    }

    public void Dispose()
    {
        if (_hwnd == IntPtr.Zero) return;
        DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        Visible = false;
    }

    private static IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_SETCURSOR:
                SetCursor(IntPtr.Zero);
                return 1;
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE; // un clic iniettato qui sopra non deve portare in primo piano nulla
            case WM_QUERYENDSESSION:
                // Non si blocca mai l'arresto, e l'agent non si chiude qui: lo chiude il servizio (vedi ShutdownGuard).
                Log.Info("Windows chiede se si può chiudere la sessione: sì, l'agent resta attivo");
                return 1;
            case WM_ENDSESSION:
                if (wParam != IntPtr.Zero) Log.Info("Windows chiude la sessione: l'agent resta attivo finché lo chiude il servizio");
                return IntPtr.Zero;
            default:
                return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);
    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public int x, y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO info);
    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int index);
}
