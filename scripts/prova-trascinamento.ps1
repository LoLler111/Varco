# Misura quanto una finestra trascinata resta indietro rispetto al cursore, con il mouse mosso da SendInput come fa Varco
# sul PC comandato. Serve a capire perché WhatsApp, su un PC comandato, segue il cursore in ritardo.
#
#   powershell -ExecutionPolicy Bypass -File prova-trascinamento.ps1
#
# Entro 5 secondi metti il cursore sulla barra del titolo della finestra da provare (un punto vuoto, non sui pulsanti)
# e poi non toccare il mouse: lo script tiene premuto il pulsante sinistro e muove la finestra avanti e indietro
# in diversi modi, uno dopo l'altro, con 4 secondi di pausa fra uno e l'altro (-Pause per cambiarla).
# Ogni risultato compare appena la sua prova è finita.
param([int]$Seconds = 3, [int]$Amplitude = 250, [int]$Pause = 4)

Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

public static class VarcoDrag
{
    [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public MOUSEINPUT mi; }
    [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int max);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] static extern int GetSystemMetrics(int i);
    [StructLayout(LayoutKind.Sequential)] struct GUITHREADINFO
    {
        public int cbSize; public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public RECT rcCaret;
    }
    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    const uint GUI_INMOVESIZE = 0x2;

    /// <summary>Il thread in primo piano sta spostando o ridimensionando una finestra (ciclo di Windows)?</summary>
    static bool InMoveSize()
    {
        var gi = new GUITHREADINFO { cbSize = Marshal.SizeOf(typeof(GUITHREADINFO)) };
        return GetGUIThreadInfo(0, ref gi) && (gi.flags & GUI_INMOVESIZE) != 0;
    }
    [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint p);
    [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint p);

    const uint MOVE = 0x1, LDOWN = 0x2, LUP = 0x4, ABSOLUTE = 0x8000, VIRTUALDESK = 0x4000;
    static readonly IntPtr Marker = new IntPtr(0x56415243); // lo stesso di Varco: il suo hook ignora questi eventi
    /// <summary>Senza marcatore l'evento è come quello di un mouse vero, a parte il flag "iniettato" di Windows.</summary>
    public static bool UseMarker = true;

    static void Send(int dx, int dy, uint flags)
    {
        var i = new INPUT { type = 0, mi = new MOUSEINPUT { dx = dx, dy = dy, dwFlags = flags, dwExtraInfo = UseMarker ? Marker : IntPtr.Zero } };
        SendInput(1, new[] { i }, Marshal.SizeOf(typeof(INPUT)));
    }

    // Identica a WindowsInput.InjectMoveTo di Varco.
    static void MoveAbs(int x, int y)
    {
        int vx = GetSystemMetrics(76), vy = GetSystemMetrics(77);
        int vw = Math.Max(1, GetSystemMetrics(78)), vh = Math.Max(1, GetSystemMetrics(79));
        int nx = (int)(((long)(x - vx) * 65536 + vw - 1) / vw);
        int ny = (int)(((long)(y - vy) * 65536 + vh - 1) / vh);
        Send(nx, ny, MOVE | ABSOLUTE | VIRTUALDESK);
    }

    public static IntPtr WindowUnderCursor()
    {
        POINT p; GetCursorPos(out p);
        return GetAncestor(WindowFromPoint(p), 2 /* GA_ROOT */);
    }

    public static string Describe(IntPtr h)
    {
        var sb = new StringBuilder(128); GetClassName(h, sb, sb.Capacity);
        uint pid; GetWindowThreadProcessId(h, out pid);
        string name = "?";
        string version = "";
        try
        {
            var proc = Process.GetProcessById((int)pid);
            name = proc.ProcessName;
            version = ", versione " + proc.MainModule.FileVersionInfo.ProductVersion + Environment.NewLine + "  " + proc.MainModule.FileName;
        }
        catch { }
        return name + " (classe " + sb + ")" + version;
    }

    static void Wait(Stopwatch sw, double untilMs)
    {
        while (sw.Elapsed.TotalMilliseconds < untilMs - 1.5) Thread.Sleep(1);
        while (sw.Elapsed.TotalMilliseconds < untilMs) Thread.SpinWait(50);
    }

    // mode: "abs" assoluto come Varco, "rel" relativo, "burst" assoluto ma a raffiche (come pacchetti di rete raggruppati).
    public static string Run(IntPtr hwnd, string mode, double intervalMs, int burst, int seconds, int amp, bool marker, string name, System.IO.TextWriter csv)
    {
        UseMarker = marker;
        POINT start; GetCursorPos(out start);
        RECT r0; GetWindowRect(hwnd, out r0);
        int off = start.X - r0.Left;
        var t = new List<double>(); var cx = new List<int>(); var wx = new List<int>();
        int moveSize = 0;
        timeBeginPeriod(1);
        try
        {
            Send(0, 0, LDOWN);
            Thread.Sleep(300); // il ciclo di spostamento della finestra parte al primo movimento
            var sw = Stopwatch.StartNew();
            double period = 1000.0, total = seconds * 1000.0, next = 0;
            int step = 0, lastX = start.X;
            while (next < total)
            {
                // Una raffica: 'burst' posizioni consecutive iniettate tutte insieme.
                for (int k = 0; k < burst; k++)
                {
                    double at = next + k * intervalMs / burst;
                    int x = start.X + (int)Math.Round(amp * Math.Sin(2 * Math.PI * at / period));
                    if (mode == "rel")
                    {
                        POINT now; GetCursorPos(out now);
                        if (x != now.X) Send(x - now.X, 0, MOVE);
                    }
                    else if (x != lastX) MoveAbs(x, start.Y);
                    lastX = x;
                }
                next += intervalMs;
                Wait(sw, next);
                POINT c; GetCursorPos(out c);
                RECT r; GetWindowRect(hwnd, out r);
                t.Add(sw.Elapsed.TotalMilliseconds); cx.Add(c.X); wx.Add(r.Left + off);
                if (InMoveSize()) moveSize++;
                step++;
            }
            MoveAbs(start.X, start.Y);
            Thread.Sleep(100);
        }
        finally
        {
            Send(0, 0, LUP);
            timeEndPeriod(1);
            UseMarker = true;
        }
        Thread.Sleep(500);
        if (csv != null)
            for (int i = 0; i < t.Count; i++)
                csv.WriteLine(string.Format(System.Globalization.CultureInfo.InvariantCulture, "{0};{1:0.0};{2};{3}", name, t[i], cx[i], wx[i]));

        // Distanza massima finestra-cursore e ritardo che allinea meglio la finestra al cursore.
        int n = t.Count, maxGap = 0;
        for (int i = 0; i < n; i++) maxGap = Math.Max(maxGap, Math.Abs(wx[i] - cx[i]));
        double dt = (t[n - 1] - t[0]) / Math.Max(1, n - 1);
        int bestShift = 0; double bestErr = double.MaxValue;
        for (int s = 0; s * dt <= 400 && s < n / 2; s++)
        {
            double e = 0; int m = 0;
            for (int i = s; i < n; i++) { e += Math.Abs(wx[i] - cx[i - s]); m++; }
            e /= m;
            if (e < bestErr) { bestErr = e; bestShift = s; }
        }
        bool moved = false;
        for (int i = 0; i < n; i++) if (Math.Abs(wx[i] - wx[0]) > 20) { moved = true; break; }
        if (!moved) return "la finestra non si è mossa (cursore non sulla barra del titolo?)";
        return string.Format("ritardo ~{0,4:0} ms   distanza max {1,4} px   scarto {2,5:0.0} px   spostamento finestra {3,3}%",
            bestShift * dt, maxGap, bestErr, 100 * moveSize / Math.Max(1, n));
    }
}
'@

Write-Host "Metti il cursore sulla barra del titolo della finestra da provare e non toccare più il mouse."
for ($i = 5; $i -gt 0; $i--) { Write-Host "  $i..."; Start-Sleep 1 }
$hwnd = [VarcoDrag]::WindowUnderCursor()
if ($hwnd -eq [IntPtr]::Zero) { Write-Host "Nessuna finestra sotto il cursore."; exit 1 }
Write-Host ("Finestra: " + [VarcoDrag]::Describe($hwnd))
Write-Host ""

# Stessi eventi di Varco (assoluti, con il suo marcatore), un solo spostamento ogni N ms: quanto spesso si può iniettare
# perché la finestra segua il cursore. "Spostamento finestra" = per quanto tempo Windows segnala lo spostamento di una
# finestra (GUI_INMOVESIZE): se è vicino a 100% Varco può riconoscere da solo quando raggruppare i movimenti.
$prove = @(
    @{ Nome = 'Ogni 4 ms (come Varco)'; Mode = 'abs'; Ms = 4;  Burst = 1; Marker = $true },
    @{ Nome = 'Ogni 8 ms';              Mode = 'abs'; Ms = 8;  Burst = 1; Marker = $true },
    @{ Nome = 'Ogni 12 ms';             Mode = 'abs'; Ms = 12; Burst = 1; Marker = $true },
    @{ Nome = 'Ogni 16 ms';             Mode = 'abs'; Ms = 16; Burst = 1; Marker = $true },
    @{ Nome = 'Ogni 20 ms';             Mode = 'abs'; Ms = 20; Burst = 1; Marker = $true }
)
$csvPath = Join-Path (Get-Location) ("prova-trascinamento-" + (Get-Date -Format 'HHmmss') + ".csv")
$csv = New-Object System.IO.StreamWriter $csvPath
$csv.WriteLine("prova;ms;cursore_x;finestra_x")
try {
    $first = $true
    foreach ($p in $prove) {
        # Pausa fra una prova e l'altra: la finestra rimasta indietro deve finire di muoversi prima della prova successiva.
        if (-not $first) { Start-Sleep -Seconds $Pause }
        $first = $false
        $r = [VarcoDrag]::Run($hwnd, $p.Mode, $p.Ms, $p.Burst, $Seconds, $Amplitude, $p.Marker, $p.Nome, $csv)
        Write-Host ("{0,-36} {1}" -f $p.Nome, $r)
    }
} finally { $csv.Close() }
Write-Host ""
Write-Host "Traccia salvata in $csvPath"
Write-Host "Ritardo vicino a 0-20 ms = la finestra segue il cursore. Rifai la prova su un'altra finestra (es. Blocco note) per confronto."
