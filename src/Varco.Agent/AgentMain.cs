using Varco.Core;
using Varco.Core.Config;
using Varco.Core.Control;
using Varco.Core.Input;

namespace Varco.Agent;

/// <summary>
/// Agent: rete + input. Normalmente lo avvia il servizio come SYSTEM nella sessione attiva
/// (così funziona anche su schermata di blocco e UAC). Per sviluppo si può lanciare a mano:
///
///   Varco.exe --agent --console [--config cartella] [--port 47800] [--discovery-port 47801] [--no-input] [--pipe nome] [--verbose]
/// </summary>
public static class AgentMain
{
    /// <summary>Evento globale con cui servizio e setup chiedono all'agent di chiudersi.</summary>
    public const string StopEventName = @"Global\Varco.Agent.Stop";

    public static async Task<int> RunAsync(string[] args)
    {
        var console = args.Contains("--console");
        var noInput = args.Contains("--no-input");
        var configDir = Arg(args, "--config") is { } c ? Path.GetFullPath(c) : ConfigStore.DefaultDirectory;
        int? port = Arg(args, "--port") is { } p ? int.Parse(p) : null;
        int? discoveryPort = Arg(args, "--discovery-port") is { } d ? int.Parse(d) : null;

        if (console && !Native.AttachConsole(-1)) Native.AllocConsole();
        Log.ToConsole = console;
        Log.Verbose = args.Contains("--verbose");
        Log.Init(Path.Combine(configDir, "logs"), "agent.log");

        // Un solo agent per macchina (il servizio lo riavvia se cade).
        using var single = new Mutex(true, noInput ? $"Global\\Varco.Agent.{port}" : "Global\\Varco.Agent", out var first);
        if (!first)
        {
            Log.Warn("Un altro agent Varco è già in esecuzione: esco");
            return 1;
        }

        Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        Log.Info($"Agent Varco avviato (utente {Environment.UserName}, sessione {System.Diagnostics.Process.GetCurrentProcess().SessionId}, config {configDir})");

        var store = new ConfigStore(configDir);
        IInputPlatform platform = noInput ? new NullInput() : new WindowsInput();
        await using var host = new VarcoHost(store, platform, port, discoveryPort);
        if (platform is WindowsInput wi) wi.Coordinator = host.Coordinator;

        using var power = new PowerNotifier();
        power.Suspending += host.OnSuspend;
        power.Resumed += host.OnResume;

        using var control = new ControlServer(host, Arg(args, "--pipe") ?? ControlProtocol.PipeName);
        control.Start();
        await host.StartAsync();

        var exit = new TaskCompletionSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.TrySetResult(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => exit.TrySetResult();

        EventWaitHandle? stop = null;
        if (!noInput)
        {
            // SYSTEM e amministratori (il setup) devono poterlo segnalare.
            var sec = new System.Security.AccessControl.EventWaitHandleSecurity();
            foreach (var sid in new[] { System.Security.Principal.WellKnownSidType.LocalSystemSid, System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid })
                sec.AddAccessRule(new System.Security.AccessControl.EventWaitHandleAccessRule(
                    new System.Security.Principal.SecurityIdentifier(sid, null),
                    System.Security.AccessControl.EventWaitHandleRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
            if (System.Security.Principal.WindowsIdentity.GetCurrent().User is { } me)
                sec.AddAccessRule(new System.Security.AccessControl.EventWaitHandleAccessRule(me,
                    System.Security.AccessControl.EventWaitHandleRights.FullControl,
                    System.Security.AccessControl.AccessControlType.Allow));
            stop = System.Threading.EventWaitHandleAcl.Create(false, EventResetMode.ManualReset, StopEventName, out _, sec);
            stop.Reset();
            ThreadPool.RegisterWaitForSingleObject(stop, (_, _) =>
            {
                Log.Info("Richiesta di chiusura ricevuta");
                exit.TrySetResult();
            }, null, Timeout.Infinite, executeOnlyOnce: true);
        }

        await exit.Task;
        Log.Info("Agent Varco in chiusura");
        (platform as IDisposable)?.Dispose();
        stop?.Dispose();
        return 0;
    }

    private static string? Arg(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }
}
