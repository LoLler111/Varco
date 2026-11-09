using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Varco.Core;
using Varco.Core.Input;
using Varco.Core.Protocol;
using static Varco.Agent.Native;

namespace Varco.Agent;

/// <summary>
/// Cattura (hook low-level) e iniezione (SendInput) su Windows.
///
/// Gli hook low-level e SendInput funzionano solo sul desktop a cui è agganciato il thread.
/// Per controllare anche schermata di blocco, UAC e Ctrl+Alt+Canc (desktop "Winlogon") l'agent gira come SYSTEM
/// nella sessione dell'utente e un watcher, ogni 250 ms, controlla quale desktop riceve l'input: se cambia,
/// il thread di input viene ricreato e agganciato al nuovo desktop.
/// </summary>
internal sealed class WindowsInput : IInputPlatform, IDisposable
{
    /// <summary>Marca gli eventi iniettati da noi, così gli hook non li scambiano per input fisico.</summary>
    internal static readonly IntPtr Marker = new(0x56415243); // "VARC"

    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Timer _watcher;
    private readonly object _threadGate = new();
    private InputThread? _thread;
    private volatile IReadOnlyList<Rect> _monitors = [];
    private int _ticks;
    private bool _disposed;

    public InputCoordinator? Coordinator { get; set; }

    public WindowsInput()
    {
        RefreshMonitors();
        EnsureThread();
        _watcher = new Timer(_ => Watch(), null, 250, 250);
    }

    // ───────────── IInputPlatform ─────────────

    public void Post(Action action)
    {
        _work.Enqueue(action);
        _thread?.Wake();
    }

    public IReadOnlyList<Rect> GetMonitors() => _monitors;

    public (int X, int Y) GetCursorPos() => Native.GetCursorPos(out var p) ? (p.X, p.Y) : (0, 0);

    public void SetCursorPos(int x, int y) => Native.SetCursorPos(x, y);

    public void InjectMoveTo(int x, int y)
    {
        int vx = GetSystemMetrics(SM_XVIRTUALSCREEN), vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int vw = Math.Max(1, GetSystemMetrics(SM_CXVIRTUALSCREEN)), vh = Math.Max(1, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        // Arrotondamento per eccesso: Windows riconverte con troncamento, così si torna al pixel esatto.
        var nx = (int)(((long)(x - vx) * 65536 + vw - 1) / vw);
        var ny = (int)(((long)(y - vy) * 65536 + vh - 1) / vh);
        SendMouse(nx, ny, 0, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK);
    }

    public void InjectButton(MouseButton button, bool down)
    {
        var (flags, data) = button switch
        {
            MouseButton.Left => (down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP, 0u),
            MouseButton.Right => (down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP, 0u),
            MouseButton.Middle => (down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP, 0u),
            MouseButton.X1 => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, 1u),
            _ => (down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, 2u),
        };
        SendMouse(0, 0, data, flags);
    }

    public void InjectWheel(int delta, bool horizontal) =>
        SendMouse(0, 0, unchecked((uint)delta), horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL);

    public void InjectKey(ushort vk, ushort scan, KeyFlags flags)
    {
        uint f = 0;
        if (flags.HasFlag(KeyFlags.Up)) f |= KEYEVENTF_KEYUP;
        if (flags.HasFlag(KeyFlags.Extended)) f |= KEYEVENTF_EXTENDEDKEY;
        // Scancode: indipendente dal layout di tastiera dei due PC. Senza scancode (tasti multimediali) uso il VK.
        if (scan != 0) f |= KEYEVENTF_SCANCODE;
        var input = new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion { ki = new KEYBDINPUT { wVk = scan != 0 ? (ushort)0 : vk, wScan = scan, dwFlags = f, dwExtraInfo = Marker } },
        };
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) == 0)
            Log.Debug($"SendInput tastiera fallito: {Marshal.GetLastWin32Error()}");
    }

    private static void SendMouse(int dx, int dy, uint data, uint flags)
    {
        var input = new INPUT
        {
            type = INPUT_MOUSE,
            U = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags, dwExtraInfo = Marker } },
        };
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) == 0)
            Log.Debug($"SendInput mouse fallito: {Marshal.GetLastWin32Error()}");
    }

    // ───────────── gestione desktop / thread ─────────────

    private void Watch()
    {
        if (_disposed) return;
        try
        {
            if (++_ticks % 4 == 0) RefreshMonitors();
            var current = InputDesktopName();
            var t = _thread;
            if (t is null || !t.IsAlive || (current is not null && t.DesktopName is not null && current != t.DesktopName))
            {
                Log.Info($"Desktop di input: {current ?? "?"} → ricreo il thread di input");
                EnsureThread(restart: true);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Watcher desktop", ex);
        }
    }

    private void EnsureThread(bool restart = false)
    {
        lock (_threadGate)
        {
            if (_disposed) return;
            if (_thread is { IsAlive: true } && !restart) return;
            _thread?.Stop();
            _thread = new InputThread(this);
            _thread.Start();
        }
    }

    private void RefreshMonitors()
    {
        var list = new List<(Rect r, bool primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(h, ref mi))
                list.Add((new Rect(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right, mi.rcMonitor.Bottom), (mi.dwFlags & 1) != 0));
            return true;
        }, IntPtr.Zero);
        if (list.Count == 0) return;
        var ordered = list.OrderByDescending(m => m.primary).Select(m => m.r).ToList();
        if (!ordered.SequenceEqual(_monitors))
        {
            _monitors = ordered;
            Log.Info("Monitor: " + string.Join(", ", ordered.Select(r => $"[{r.Left},{r.Top} {r.Width}x{r.Height}]")));
        }
    }

    internal void DrainWork()
    {
        while (_work.TryDequeue(out var a))
        {
            try { a(); }
            catch (Exception ex) { Log.Error("Azione di input", ex); }
        }
    }

    public void Dispose()
    {
        lock (_threadGate)
        {
            _disposed = true;
            _watcher.Dispose();
            _thread?.Stop();
        }
    }

    // ───────────── thread agganciato al desktop di input ─────────────

    private sealed class InputThread
    {
        private readonly WindowsInput _owner;
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private readonly HookProc _mouseProc;
        private readonly HookProc _keyProc;
        private uint _threadId;
        private volatile bool _alive = true;

        public string? DesktopName { get; private set; }
        public bool IsAlive => _alive && _thread.IsAlive;

        public InputThread(WindowsInput owner)
        {
            _owner = owner;
            _mouseProc = MouseHook;
            _keyProc = KeyHook;
            _thread = new Thread(Run) { IsBackground = true, Name = "Varco input", Priority = ThreadPriority.Highest };
        }

        public void Start()
        {
            _thread.Start();
            _ready.Wait(2000);
        }

        public void Wake()
        {
            if (_threadId != 0) PostThreadMessage(_threadId, WM_APP, IntPtr.Zero, IntPtr.Zero);
        }

        public void Stop()
        {
            _alive = false;
            if (_threadId != 0) PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }

        private void Run()
        {
            IntPtr desk = IntPtr.Zero, mouseHook = IntPtr.Zero, keyHook = IntPtr.Zero;
            try
            {
                desk = OpenInputDesktop(0, false, DESKTOP_ALL | GENERIC_ALL);
                if (desk != IntPtr.Zero)
                {
                    if (!SetThreadDesktop(desk))
                        Log.Warn($"SetThreadDesktop fallito ({Marshal.GetLastWin32Error()})");
                    DesktopName = Native.DesktopName(desk);
                }
                else
                {
                    // Utente normale (modalità sviluppo): niente accesso al desktop sicuro, resto su quello corrente.
                    Log.Debug($"OpenInputDesktop fallito ({Marshal.GetLastWin32Error()})");
                }

                var hMod = GetModuleHandle(null);
                mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, hMod, 0);
                keyHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyProc, hMod, 0);
                if (mouseHook == IntPtr.Zero || keyHook == IntPtr.Zero)
                    Log.Error($"Installazione hook fallita ({Marshal.GetLastWin32Error()})");

                PeekMessage(out _, IntPtr.Zero, 0, 0, 0); // crea la coda messaggi
                _threadId = GetCurrentThreadId();
                _ready.Set();
                Log.Info($"Thread di input attivo sul desktop \"{DesktopName ?? "corrente"}\"");

                _owner.DrainWork();
                while (_alive && GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message == WM_APP) _owner.DrainWork();
                }
            }
            catch (Exception ex)
            {
                Log.Error("Thread di input", ex);
            }
            finally
            {
                _alive = false;
                _ready.Set();
                if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
                if (keyHook != IntPtr.Zero) UnhookWindowsHookEx(keyHook);
                if (desk != IntPtr.Zero) CloseDesktop(desk);
            }
        }

        private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _owner.Coordinator is { } c)
            {
                try
                {
                    var s = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    if (s.dwExtraInfo != Marker)
                    {
                        var swallow = (int)wParam switch
                        {
                            WM_MOUSEMOVE => c.OnLocalMouseMove(s.pt.X, s.pt.Y),
                            WM_LBUTTONDOWN => c.OnLocalButton(MouseButton.Left, true),
                            WM_LBUTTONUP => c.OnLocalButton(MouseButton.Left, false),
                            WM_RBUTTONDOWN => c.OnLocalButton(MouseButton.Right, true),
                            WM_RBUTTONUP => c.OnLocalButton(MouseButton.Right, false),
                            WM_MBUTTONDOWN => c.OnLocalButton(MouseButton.Middle, true),
                            WM_MBUTTONUP => c.OnLocalButton(MouseButton.Middle, false),
                            WM_XBUTTONDOWN => c.OnLocalButton((s.mouseData >> 16) == 1 ? MouseButton.X1 : MouseButton.X2, true),
                            WM_XBUTTONUP => c.OnLocalButton((s.mouseData >> 16) == 1 ? MouseButton.X1 : MouseButton.X2, false),
                            WM_MOUSEWHEEL => c.OnLocalWheel((short)(s.mouseData >> 16), false),
                            WM_MOUSEHWHEEL => c.OnLocalWheel((short)(s.mouseData >> 16), true),
                            _ => false,
                        };
                        if (swallow) return 1;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Hook mouse", ex);
                }
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }

        private IntPtr KeyHook(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0 && _owner.Coordinator is { } c)
            {
                try
                {
                    var s = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    if (s.dwExtraInfo != Marker)
                    {
                        var flags = KeyFlags.None;
                        if ((s.flags & LLKHF_UP) != 0) flags |= KeyFlags.Up;
                        if ((s.flags & LLKHF_EXTENDED) != 0) flags |= KeyFlags.Extended;
                        if (c.OnLocalKey((ushort)s.vkCode, (ushort)s.scanCode, flags)) return 1;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("Hook tastiera", ex);
                }
            }
            return CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }
    }
}

/// <summary>Piattaforma finta per --no-input (test di rete con due agent sullo stesso PC).</summary>
internal sealed class NullInput : IInputPlatform
{
    private static readonly IReadOnlyList<Rect> Screen = [new Rect(0, 0, 1920, 1080)];
    public void Post(Action action) => action();
    public IReadOnlyList<Rect> GetMonitors() => Screen;
    public (int X, int Y) GetCursorPos() => (960, 540);
    public void SetCursorPos(int x, int y) { }
    public void InjectMoveTo(int x, int y) => Log.Debug($"[no-input] move {x},{y}");
    public void InjectButton(MouseButton button, bool down) => Log.Debug($"[no-input] {button} {(down ? "giù" : "su")}");
    public void InjectWheel(int delta, bool horizontal) => Log.Debug($"[no-input] wheel {delta}");
    public void InjectKey(ushort vk, ushort scan, KeyFlags flags) => Log.Debug($"[no-input] key {vk:X2} {flags}");
}
