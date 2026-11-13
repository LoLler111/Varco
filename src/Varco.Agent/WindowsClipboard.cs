using System.Runtime.InteropServices;
using Varco.Core;
using Varco.Core.Clipboard;

namespace Varco.Agent;

/// <summary>
/// Appunti di Windows. Un thread dedicato con una finestra invisibile (message-only) riceve WM_CLIPBOARDUPDATE
/// e fa tutte le letture e scritture. Gli appunti appartengono alla window station (WinSta0), non al desktop:
/// funzionano anche mentre il thread di input passa alla schermata di blocco.
///
/// Si condividono solo formati che hanno senso su un altro PC: testo, HTML, RTF e immagini (DIB e PNG).
/// I file copiati in Esplora risorse (CF_HDROP) diventano un elenco di percorsi: i dati passano solo quando si incolla.
/// </summary>
internal sealed class WindowsClipboard : IClipboardPlatform, IDisposable
{
    private const uint CF_UNICODETEXT = 13, CF_DIB = 8, CF_HDROP = 15;
    private const uint WM_CLOSE = 0x0010, WM_DESTROY = 0x0002, WM_TIMER = 0x0113, WM_CLIPBOARDUPDATE = 0x031D;
    private const uint WM_SET_CONTENT = 0x8000 + 1;
    private const uint GMEM_MOVEABLE = 0x0002;
    private static readonly IntPtr HWND_MESSAGE = new(-3);
    private static readonly IntPtr TimerId = new(1);

    /// <summary>Molti programmi scrivono gli appunti in più passaggi: si legge solo quando smettono.</summary>
    private const uint DebounceMs = 150;

    /// <summary>Formati registrati condivisi (stesso nome su tutti i PC, id diverso).</summary>
    private static readonly string[] RegisteredFormats = ["HTML Format", "Rich Text Format", "PNG"];

    /// <summary>Formati con cui gestori di password e simili chiedono di non copiare il contenuto altrove.</summary>
    private static readonly string[] PrivateMarkers = ["ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore"];

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly WndProc _wndProc;
    private IntPtr _hwnd;
    private uint _ownSeq;
    private ClipContent? _pending;

    public event Action<ClipContent>? LocalChanged;

    public WindowsClipboard()
    {
        _wndProc = WindowProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "Varco appunti" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait(2000);
    }

    public void SetContent(ClipContent content)
    {
        Volatile.Write(ref _pending, content);
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_SET_CONTENT, IntPtr.Zero, IntPtr.Zero);
    }

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    // ───────────── thread e finestra ─────────────

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
                lpszClassName = "Varco.Clipboard." + Environment.ProcessId,
            };
            if (RegisterClassEx(ref cls) == 0)
            {
                Log.Error($"Appunti: RegisterClassEx fallito ({Marshal.GetLastWin32Error()})");
                return;
            }
            _hwnd = CreateWindowEx(0, cls.lpszClassName, "Varco appunti", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, hInst, IntPtr.Zero);
            if (_hwnd == IntPtr.Zero)
            {
                Log.Error($"Appunti: CreateWindowEx fallito ({Marshal.GetLastWin32Error()})");
                return;
            }
            if (!AddClipboardFormatListener(_hwnd))
                Log.Error($"Appunti: AddClipboardFormatListener fallito ({Marshal.GetLastWin32Error()})");
            else
                Log.Info("Appunti condivisi pronti");
            _ready.Set();

            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Thread degli appunti", ex);
        }
        finally
        {
            _ready.Set();
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            switch (msg)
            {
                case WM_CLIPBOARDUPDATE:
                    // Scritture nostre (appunti arrivati da un altro PC): non vanno rimandate indietro.
                    if (GetClipboardOwner() == _hwnd || GetClipboardSequenceNumber() == _ownSeq) return IntPtr.Zero;
                    SetTimer(hwnd, TimerId, DebounceMs, IntPtr.Zero);
                    return IntPtr.Zero;

                case WM_TIMER when wParam == TimerId:
                    KillTimer(hwnd, TimerId);
                    if (GetClipboardOwner() == _hwnd) return IntPtr.Zero;
                    if (Read() is { } content) LocalChanged?.Invoke(content);
                    return IntPtr.Zero;

                case WM_SET_CONTENT:
                    if (Interlocked.Exchange(ref _pending, null) is { } pending) Write(pending);
                    return IntPtr.Zero;

                case WM_CLOSE:
                    RemoveClipboardFormatListener(hwnd);
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
            Log.Error($"Appunti (messaggio 0x{msg:X})", ex);
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    // ───────────── lettura ─────────────

    private ClipContent? Read()
    {
        if (!OpenWithRetry()) return null;
        try
        {
            foreach (var marker in PrivateMarkers)
                if (IsClipboardFormatAvailable(RegisterClipboardFormat(marker)))
                {
                    Log.Debug($"Appunti marcati come privati ({marker}): non li condivido");
                    return null;
                }
            // "Non sincronizzare con altri dispositivi" (lo usano anche Windows e i gestori di password).
            var cloud = RegisterClipboardFormat("CanUploadToCloudClipboard");
            if (IsClipboardFormatAvailable(cloud) && ReadBytes(cloud, 16) is { Length: >= 4 } flag && BitConverter.ToInt32(flag) == 0)
            {
                Log.Debug("Appunti esclusi dalla sincronizzazione dal programma che li ha copiati");
                return null;
            }

            var formats = new List<ClipFormat>();
            long budget = ClipboardSync.MaxRawBytes;
            void Take(uint id, string name)
            {
                if (!IsClipboardFormatAvailable(id)) return;
                var data = ReadBytes(id, budget);
                if (data is null || data.Length == 0) return;
                if (id == CF_UNICODETEXT) data = TrimText(data);
                budget -= data.Length;
                formats.Add(new ClipFormat(name, data));
            }

            // File copiati in Esplora risorse: solo i percorsi, l'host li trasforma in un'offerta (i dati viaggiano quando si incolla).
            if (IsClipboardFormatAvailable(CF_HDROP) && ReadDropPaths() is { Count: > 0 } paths)
                formats.Add(new ClipFormat(ClipboardSync.LocalFilesFormat, System.Text.Encoding.UTF8.GetBytes(string.Join('\0', paths))));

            Take(CF_UNICODETEXT, "CF_UNICODETEXT");
            foreach (var name in RegisteredFormats) Take(RegisterClipboardFormat(name), name);
            // CF_DIB è il formato immagine più compatibile: Windows lo ricava anche da CF_BITMAP e CF_DIBV5.
            Take(CF_DIB, "CF_DIB");

            if (formats.Count == 0)
            {
                Log.Debug("Appunti senza formati condivisibili: restano su questo PC");
                return null;
            }
            return new ClipContent(formats);
        }
        finally
        {
            CloseClipboard();
        }
    }

    private static byte[]? ReadBytes(uint format, long max)
    {
        var h = GetClipboardData(format);
        if (h == IntPtr.Zero) return null;
        var size = (long)GlobalSize(h);
        if (size <= 0) return null;
        if (size > max)
        {
            Log.Info($"Formato degli appunti troppo grande da condividere ({size / (1024 * 1024)} MB)");
            return null;
        }
        var p = GlobalLock(h);
        if (p == IntPtr.Zero) return null;
        try
        {
            var data = new byte[size];
            Marshal.Copy(p, data, 0, (int)size);
            return data;
        }
        finally
        {
            GlobalUnlock(h);
        }
    }

    private static List<string> ReadDropPaths()
    {
        var list = new List<string>();
        var h = GetClipboardData(CF_HDROP);
        if (h == IntPtr.Zero) return list;
        var count = DragQueryFile(h, 0xFFFFFFFF, null, 0);
        for (uint i = 0; i < count; i++)
        {
            var len = DragQueryFile(h, i, null, 0);
            if (len == 0) continue;
            var sb = new System.Text.StringBuilder((int)len + 1);
            if (DragQueryFile(h, i, sb, (uint)sb.Capacity) > 0) list.Add(sb.ToString());
        }
        return list;
    }

    /// <summary>GlobalSize arrotonda: si tiene il testo fino al terminatore (incluso), così la stessa copia dà sempre gli stessi byte.</summary>
    private static byte[] TrimText(byte[] data)
    {
        for (var i = 0; i + 1 < data.Length; i += 2)
            if (data[i] == 0 && data[i + 1] == 0) return data[..(i + 2)];
        return data;
    }

    // ───────────── scrittura ─────────────

    private void Write(ClipContent content)
    {
        if (!OpenWithRetry())
        {
            Log.Warn("Appunti occupati da un altro programma: contenuto ricevuto non scritto");
            return;
        }
        try
        {
            EmptyClipboard();
            foreach (var f in content.Formats)
            {
                var id = f.Name switch
                {
                    "CF_UNICODETEXT" => CF_UNICODETEXT,
                    "CF_DIB" => CF_DIB,
                    _ when RegisteredFormats.Contains(f.Name) => RegisterClipboardFormat(f.Name),
                    _ => 0u, // formato sconosciuto (versione futura): si salta
                };
                if (id == 0) continue;
                var data = f.Data;
                if (id == CF_UNICODETEXT && (data.Length < 2 || data[^1] != 0 || data[^2] != 0)) data = [.. data, 0, 0];
                SetBytes(id, data);
            }
        }
        finally
        {
            CloseClipboard();
            _ownSeq = GetClipboardSequenceNumber();
        }
    }

    private static void SetBytes(uint format, byte[] data)
    {
        var h = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)Math.Max(1, data.Length));
        if (h == IntPtr.Zero) return;
        var p = GlobalLock(h);
        if (p == IntPtr.Zero) { GlobalFree(h); return; }
        Marshal.Copy(data, 0, p, data.Length);
        GlobalUnlock(h);
        // Se riesce, la memoria passa al sistema; altrimenti va liberata da noi.
        if (SetClipboardData(format, h) == IntPtr.Zero)
        {
            Log.Debug($"SetClipboardData {format} fallito ({Marshal.GetLastWin32Error()})");
            GlobalFree(h);
        }
    }

    /// <summary>Un altro programma può tenere aperti gli appunti per qualche istante.</summary>
    private bool OpenWithRetry()
    {
        for (var i = 0; i < 10; i++)
        {
            if (OpenClipboard(_hwnd)) return true;
            Thread.Sleep(30);
        }
        Log.Debug($"OpenClipboard fallito ({Marshal.GetLastWin32Error()})");
        return false;
    }

    // ───────────── Win32 ─────────────

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
    private static extern ushort RegisterClassEx(ref WNDCLASSEX cls);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h,
        IntPtr parent, IntPtr menu, IntPtr hInst, IntPtr param);
    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Native.MSG msg);
    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Native.MSG msg);
    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern UIntPtr SetTimer(IntPtr hwnd, IntPtr id, uint ms, IntPtr proc);
    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hwnd, IntPtr id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();
    [DllImport("user32.dll")]
    private static extern bool EmptyClipboard();
    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);
    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr hMem);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr h);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr h);
    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr h);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr hDrop, uint index, System.Text.StringBuilder? file, uint size);
}
