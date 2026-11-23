using System.Runtime.InteropServices;
using Varco.Core;

namespace Varco.Agent;

/// <summary>
/// Avvisa quando la sessione dell'utente viene bloccata (Win+L, Start → Blocca, Ctrl+Alt+Canc → Blocca, blocco per
/// inattività). Usa le notifiche di sessione di Windows su una finestra invisibile (solo messaggi) con un thread suo.
/// </summary>
internal sealed class SessionLockWatcher : IDisposable
{
    private const uint WM_WTSSESSION_CHANGE = 0x02B1, WM_CLOSE = 0x0010, WM_DESTROY = 0x0002;
    private const int WTS_SESSION_LOCK = 0x7;
    private const int NOTIFY_FOR_THIS_SESSION = 0;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private readonly WndProc _wndProc; // tenuto vivo finché la finestra esiste
    private readonly Thread _thread;
    private IntPtr _hwnd;

    /// <summary>La sessione è stata bloccata. Chiamato dal thread della finestra: non deve bloccare a lungo.</summary>
    public event Action? Locked;

    public SessionLockWatcher()
    {
        _wndProc = WindowProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "Varco.SessionLock" };
        _thread.Start();
    }

    private void Run()
    {
        try
        {
            var hInst = Native.GetModuleHandle(null);
            var cls = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                hInstance = hInst,
                lpszClassName = "Varco.SessionLock." + Environment.ProcessId,
            };
            if (RegisterClassEx(ref cls) == 0)
            {
                Log.Error($"Blocco di gruppo: RegisterClassEx fallito ({Marshal.GetLastWin32Error()})");
                return;
            }
            _hwnd = CreateWindowEx(0, cls.lpszClassName, "Varco blocco", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                Log.Error($"Blocco di gruppo: CreateWindowEx fallito ({Marshal.GetLastWin32Error()})");
                return;
            }
            if (!WTSRegisterSessionNotification(_hwnd, NOTIFY_FOR_THIS_SESSION))
            {
                Log.Error($"Blocco di gruppo: notifiche di sessione non disponibili ({Marshal.GetLastWin32Error()})");
                return;
            }
            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Thread delle notifiche di sessione", ex);
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_WTSSESSION_CHANGE when (int)wParam == WTS_SESSION_LOCK:
                    Log.Info("Sessione bloccata");
                    Locked?.Invoke();
                    return IntPtr.Zero;
                case WM_CLOSE:
                    WTSUnRegisterSessionNotification(hwnd);
                    DestroyWindow(hwnd);
                    return IntPtr.Zero;
                case WM_DESTROY:
                    _hwnd = IntPtr.Zero;
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Notifica di sessione (messaggio 0x{msg:X})", ex);
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    // ───────────── interop ─────────────

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

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
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Native.MSG msg);
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSRegisterSessionNotification(IntPtr hwnd, int flags);
    [DllImport("wtsapi32.dll")]
    private static extern bool WTSUnRegisterSessionNotification(IntPtr hwnd);
}

/// <summary>
/// Controlla ogni pochi secondi se esce del suono da questo PC (vedi <see cref="AudioOutput"/>) e lo dice all'host, che
/// lo fa sapere agli altri PC: i tasti multimediali premuti altrove arrivano qui mentre suona la musica.
/// Controlla solo quando c'è almeno un altro PC collegato.
/// </summary>
internal sealed class AudioActivity : IDisposable
{
    private const int CheckMs = 2000;

    private readonly Func<bool> _wanted;
    private readonly Action<bool> _report;
    private readonly Func<bool> _ignoreOwnAudio;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _stopEvent = new();
    private volatile bool _stop;
    private bool _playing;

    /// <param name="ignoreOwnAudio">Il suono di Varco stesso (l'audio degli altri PC sul centrale) non conta: la musica suona altrove.</param>
    public AudioActivity(Func<bool> wanted, Action<bool> report, Func<bool> ignoreOwnAudio)
    {
        _wanted = wanted;
        _report = report;
        _ignoreOwnAudio = ignoreOwnAudio;
        _thread = new Thread(Run) { IsBackground = true, Name = "Varco.AudioActivity" };
        _thread.Start();
    }

    private void Run()
    {
        while (!_stop)
        {
            _stopEvent.Wait(CheckMs);
            if (_stop) break;
            try
            {
                var playing = _wanted() && AudioOutput.IsPlaying(() => _stop, _ignoreOwnAudio());
                if (playing == _playing) continue;
                _playing = playing;
                _report(playing);
            }
            catch (Exception ex) { Log.Error("Controllo dell'audio", ex); }
        }
    }

    public void Dispose()
    {
        _stop = true;
        _stopEvent.Set();
        _thread.Join(2000);
    }
}
