using System.Diagnostics;
using System.Runtime.InteropServices;
using System.ServiceProcess;
using Varco.Core;
using Varco.Core.Config;

namespace Varco.Service;

/// <summary>
/// Servizio Windows "Varco" (LocalSystem). Non fa rete né input: tiene in vita l'agent (Varco.exe --agent)
/// come SYSTEM nella sessione della console attiva. Così l'agent lavora anche su schermata di blocco, UAC e login,
/// e se cade (o cambia la sessione, es. cambio utente) viene rilanciato subito.
/// </summary>
public static class ServiceMain
{
    public const string ServiceName = "Varco";

    public static int Run(string[] args)
    {
        Log.Init(Path.Combine(ConfigStore.DefaultDirectory, "logs"), "service.log");
        if (args.Contains("--console"))
        {
            // Debug: va eseguito come SYSTEM (es. psexec -s) perché CreateProcessAsUser richiede SE_TCB.
            Log.ToConsole = true;
            using var launcher = new AgentLauncher();
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            launcher.RunAsync(cts.Token).GetAwaiter().GetResult();
            return 0;
        }
        if (Path.GetDirectoryName(Environment.ProcessPath) is { } dir) DeleteOldCopies(dir);
        ServiceBase.Run(new VarcoService());
        return 0;
    }

    /// <summary>
    /// Avvia un programma come l'utente collegato alla sessione indicata (token non elevato, il suo ambiente, il suo desktop).
    /// Serve SYSTEM: lo usa l'aggiornamento automatico per riaprire le icone nella tray.
    /// </summary>
    public static bool LaunchAsSessionUser(uint session, string exe, string args)
    {
        IntPtr user = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            if (!Win32.WTSQueryUserToken(session, out user)) throw new System.ComponentModel.Win32Exception();
            if (!Win32.CreateEnvironmentBlock(out env, user, false)) env = IntPtr.Zero;
            var si = new Win32.STARTUPINFO { cb = Marshal.SizeOf<Win32.STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            if (!Win32.CreateProcessAsUser(user, null, $"\"{exe}\" {args}", IntPtr.Zero, IntPtr.Zero, false,
                    Win32.CREATE_UNICODE_ENVIRONMENT, env, Path.GetDirectoryName(exe), ref si, out var pi))
                throw new System.ComponentModel.Win32Exception();
            Win32.CloseHandle(pi.hThread);
            Win32.CloseHandle(pi.hProcess);
            Log.Info($"Avviato {Path.GetFileName(exe)} {args} nella sessione {session} (pid {pi.dwProcessId})");
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Impossibile avviare {Path.GetFileName(exe)} nella sessione {session}: {ex.Message}");
            return false;
        }
        finally
        {
            if (env != IntPtr.Zero) Win32.DestroyEnvironmentBlock(env);
            if (user != IntPtr.Zero) Win32.CloseHandle(user);
        }
    }

    /// <summary>
    /// Cancella le copie Varco.old-*.exe lasciate da un aggiornamento (al momento erano in uso da un'icona aperta).
    /// Quelle ancora in uso restano lì fino alla prossima volta.
    /// </summary>
    public static void DeleteOldCopies(string installDir)
    {
        try
        {
            if (!Directory.Exists(installDir)) return;
            foreach (var f in Directory.EnumerateFiles(installDir, "Varco.old-*.exe"))
                try { File.Delete(f); } catch { }
        }
        catch { }
    }

    /// <summary>Chiede a qualsiasi agent in esecuzione di chiudersi (usato anche dal setup).</summary>
    public static void SignalAgentStop()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(@"Global\Varco.Agent.Stop", out var ev))
                using (ev) ev.Set();
        }
        catch (Exception ex)
        {
            Log.Warn($"Impossibile segnalare la chiusura all'agent: {ex.Message}");
        }
    }
}

internal sealed class VarcoService : ServiceBase
{
    private readonly AgentLauncher _launcher = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public VarcoService()
    {
        ServiceName = ServiceMain.ServiceName;
        CanHandleSessionChangeEvent = true;
        CanShutdown = true;
    }

    protected override void OnStart(string[] args)
    {
        Log.Info("Servizio Varco avviato");
        _cts = new CancellationTokenSource();
        _loop = _launcher.RunAsync(_cts.Token);
    }

    protected override void OnStop()
    {
        Log.Info("Servizio Varco in arresto");
        _cts?.Cancel();
        try { _loop?.Wait(3000); } catch { }
        _launcher.Dispose();
    }

    protected override void OnShutdown() => OnStop();

    protected override void OnSessionChange(SessionChangeDescription change)
    {
        Log.Info($"Cambio sessione: {change.Reason} (sessione {change.SessionId})");
        _launcher.CheckNow();
    }
}

internal sealed class AgentLauncher : IDisposable
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    private Process? _agent;
    private uint _agentSession = uint.MaxValue;
    private readonly Queue<DateTime> _recentStarts = new();

    private static string AgentPath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Varco.exe");

    public void CheckNow()
    {
        try { if (_wake.CurrentCount == 0) _wake.Release(); } catch (SemaphoreFullException) { }
    }

    public async Task RunAsync(CancellationToken ct)
    {
        StopStrayAgents();
        while (!ct.IsCancellationRequested)
        {
            try { Check(); }
            catch (Exception ex) { Log.Error("Controllo agent", ex); }
            try { await _wake.WaitAsync(1000, ct); }
            catch (OperationCanceledException) { break; }
        }
        StopAgent();
    }

    private void Check()
    {
        var session = Win32.TargetSession();
        if (session == 0xFFFFFFFF) return; // transizione in corso, nessuna sessione utilizzabile

        var alive = _agent is { HasExited: false };
        if (alive && _agentSession == session) return;

        if (alive)
        {
            Log.Info($"La sessione attiva è ora la {session}: sposto l'agent");
            StopAgent();
        }
        else if (_agent is not null)
        {
            Log.Warn($"Agent terminato (codice {SafeExitCode(_agent)}): lo riavvio");
            _agent.Dispose();
            _agent = null;
        }

        // Protezione da crash a ripetizione: max 5 avvii al minuto.
        while (_recentStarts.Count > 0 && DateTime.UtcNow - _recentStarts.Peek() > TimeSpan.FromMinutes(1)) _recentStarts.Dequeue();
        if (_recentStarts.Count >= 5) return;
        _recentStarts.Enqueue(DateTime.UtcNow);

        _agent = Launch(session);
        _agentSession = session;
    }

    private static Process? Launch(uint session)
    {
        if (!File.Exists(AgentPath))
        {
            Log.Error($"Agent non trovato: {AgentPath}");
            return null;
        }

        IntPtr token = IntPtr.Zero, dup = IntPtr.Zero;
        try
        {
            if (!Win32.OpenProcessToken(Process.GetCurrentProcess().Handle, Win32.TOKEN_ALL_ACCESS, out token))
                throw new System.ComponentModel.Win32Exception();
            if (!Win32.DuplicateTokenEx(token, Win32.MAXIMUM_ALLOWED, IntPtr.Zero, 2 /*SecurityImpersonation*/, 1 /*TokenPrimary*/, out dup))
                throw new System.ComponentModel.Win32Exception();
            // Stesso token SYSTEM, ma nella sessione dell'utente: può agganciarsi a Default e Winlogon.
            if (!Win32.SetTokenInformation(dup, 12 /*TokenSessionId*/, ref session, sizeof(uint)))
                throw new System.ComponentModel.Win32Exception();

            var si = new Win32.STARTUPINFO { cb = Marshal.SizeOf<Win32.STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            var cmd = $"\"{AgentPath}\" --agent";
            if (!Win32.CreateProcessAsUser(dup, null, cmd, IntPtr.Zero, IntPtr.Zero, false,
                    Win32.CREATE_NO_WINDOW | Win32.CREATE_UNICODE_ENVIRONMENT, IntPtr.Zero,
                    AppContext.BaseDirectory, ref si, out var pi))
                throw new System.ComponentModel.Win32Exception();

            Win32.CloseHandle(pi.hThread);
            Win32.CloseHandle(pi.hProcess);
            Log.Info($"Agent avviato nella sessione {session} (pid {pi.dwProcessId})");
            return Process.GetProcessById(pi.dwProcessId);
        }
        catch (Exception ex)
        {
            Log.Error($"Avvio agent nella sessione {session} fallito", ex);
            return null;
        }
        finally
        {
            if (dup != IntPtr.Zero) Win32.CloseHandle(dup);
            if (token != IntPtr.Zero) Win32.CloseHandle(token);
        }
    }

private void StopAgent()
    {
        if (_agent is null) return;
        // Prima con le buone (l'agent rilascia i tasti e chiude le connessioni), poi con le cattive.
        ServiceMain.SignalAgentStop();
        try { if (!_agent.WaitForExit(2000)) _agent.Kill(); } catch { }
        _agent.Dispose();
        _agent = null;
        _agentSession = uint.MaxValue;
    }

    /// <summary>Un agent rimasto da un'esecuzione precedente terrebbe il mutex: gli chiedo di chiudersi.</summary>
    private static void StopStrayAgents()
    {
        ServiceMain.SignalAgentStop();
        Thread.Sleep(500);
    }

    private static string SafeExitCode(Process p)
    {
        try { return p.ExitCode.ToString(); } catch { return "?"; }
    }

    public void Dispose() => StopAgent();
}

internal static class Win32
{
    public const uint TOKEN_ALL_ACCESS = 0xF01FF;
    public const uint MAXIMUM_ALLOWED = 0x02000000;
    public const uint CREATE_NO_WINDOW = 0x08000000;
    public const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [DllImport("kernel32.dll")]
    public static extern uint WTSGetActiveConsoleSessionId();

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        public IntPtr WinStationName;
        public int State;
    }

    private const int WTSActive = 0;

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr server, int reserved, int version, out IntPtr sessions, out int count);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    /// <summary>
    /// Sessione in cui far girare l'agent: la console fisica se c'è qualcuno che la usa;
    /// altrimenti la sessione attiva dell'utente (es. Desktop remoto o sessione avanzata di Hyper-V);
    /// altrimenti di nuovo la console (schermata di accesso, nessuno collegato).
    /// </summary>
    public static uint TargetSession()
    {
        var console = WTSGetActiveConsoleSessionId();
        var active = new List<uint>();
        if (WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count))
        {
            try
            {
                var size = Marshal.SizeOf<WTS_SESSION_INFO>();
                for (var i = 0; i < count; i++)
                {
                    var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(buffer + i * size);
                    if (info.State == WTSActive && info.SessionId != 0) active.Add(info.SessionId);
                }
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }
        if (console != 0xFFFFFFFF && active.Contains(console)) return console;
        if (active.Count > 0) return active.Min();
        return console;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attrs, int impersonationLevel, int tokenType, out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    public static extern bool SetTokenInformation(IntPtr token, int infoClass, ref uint info, int length);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern bool CreateProcessAsUser(IntPtr token, string? app, string cmdLine, IntPtr procAttrs, IntPtr threadAttrs,
        bool inherit, uint flags, IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool CloseHandle(IntPtr h);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    public static extern bool WTSQueryUserToken(uint session, out IntPtr token);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    public static extern bool DestroyEnvironmentBlock(IntPtr env);
}
