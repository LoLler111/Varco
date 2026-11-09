using Varco.Agent;
using Varco.Core;
using Varco.Core.Config;
using Varco.Core.Input;

// Varco.Agent: rete + input. Normalmente lo avvia il servizio come SYSTEM nella sessione attiva
// (così funziona anche su schermata di blocco e UAC). Per sviluppo si può lanciare a mano:
//
//   Varco.Agent.exe --console [--config <cartella>] [--port 47800] [--discovery-port 47801] [--no-input] [--pipe <nome>] [--verbose]

var console = args.Contains("--console");
var noInput = args.Contains("--no-input");
var configDir = Arg("--config") is { } c ? Path.GetFullPath(c) : ConfigStore.DefaultDirectory;
int? port = Arg("--port") is { } p ? int.Parse(p) : null;
int? discoveryPort = Arg("--discovery-port") is { } d ? int.Parse(d) : null;

if (console && !Native.AttachConsole(-1)) Native.AllocConsole();
Log.ToConsole = console;
Log.Verbose = args.Contains("--verbose");
Log.Init(Path.Combine(configDir, "logs"), "agent.log");

// Un solo agent per macchina (il servizio lo riavvia se cade).
using var single = new Mutex(true, noInput ? $"Global\\Varco.Agent.{port}" : "Global\\Varco.Agent", out var first);
if (!first)
{
    Log.Warn("Un altro Varco.Agent è già in esecuzione: esco");
    return 1;
}

Native.SetProcessDpiAwarenessContext(Native.DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
Log.Info($"Varco.Agent avviato (utente {Environment.UserName}, sessione {System.Diagnostics.Process.GetCurrentProcess().SessionId}, config {configDir})");

var store = new ConfigStore(configDir);
IInputPlatform platform = noInput ? new NullInput() : new WindowsInput();
await using var host = new VarcoHost(store, platform, port, discoveryPort);
if (platform is WindowsInput wi) wi.Coordinator = host.Coordinator;

using var power = new PowerNotifier();
power.Suspending += host.OnSuspend;
power.Resumed += host.OnResume;

using var control = new ControlServer(host, Arg("--pipe") ?? Varco.Core.Control.ControlProtocol.PipeName);
control.Start();
await host.StartAsync();

var exit = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; exit.TrySetResult(); };
AppDomain.CurrentDomain.ProcessExit += (_, _) => exit.TrySetResult();
await exit.Task;

Log.Info("Varco.Agent in chiusura");
(platform as IDisposable)?.Dispose();
return 0;

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}
