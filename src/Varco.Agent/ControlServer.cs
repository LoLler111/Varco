using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Varco.Core;
using Varco.Core.Control;

namespace Varco.Agent;

/// <summary>Named pipe per la tray. Accessibile a SYSTEM, amministratori e utenti interattivi della macchina.</summary>
internal sealed class ControlServer(VarcoHost host, string pipeName) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public void Start() => _ = Task.Run(Loop);

    private static PipeSecurity Security()
    {
        var sec = new PipeSecurity();
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        sec.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.InteractiveSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        var me = WindowsIdentity.GetCurrent().User;
        if (me is not null) sec.AddAccessRule(new PipeAccessRule(me, PipeAccessRights.FullControl, AccessControlType.Allow));
        return sec;
    }

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                    4096, 4096, Security());
                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Error("Pipe di controllo", ex);
                await Task.Delay(1000).ConfigureAwait(false);
                continue;
            }
            _ = Task.Run(() => Serve(pipe));
        }
    }

    private async Task Serve(NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
                var line = await reader.ReadLineAsync(_cts.Token).ConfigureAwait(false);
                if (line is null) return;
                var req = JsonSerializer.Deserialize<ControlRequest>(line, ControlProtocol.Json) ?? new ControlRequest();
                var resp = await host.HandleAsync(req).ConfigureAwait(false);
                var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(resp, ControlProtocol.Json) + "\n");
                await pipe.WriteAsync(bytes, _cts.Token).ConfigureAwait(false);
                await pipe.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or JsonException)
            {
                Log.Debug($"Client pipe: {ex.Message}");
            }
        }
    }

    public void Dispose() => _cts.Cancel();
}
