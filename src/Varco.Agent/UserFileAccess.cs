using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Varco.Core;
using Varco.Core.Files;

namespace Varco.Agent;

/// <summary>
/// L'agent gira come SYSTEM e potrebbe leggere qualsiasi file. I file copiati si aprono invece con i permessi
/// dell'utente collegato a questa sessione: un altro PC ottiene solo ciò che quell'utente poteva leggere,
/// e funzionano anche le sue unità di rete mappate. Avviato a mano (sviluppo) l'agent è già l'utente.
/// </summary>
internal sealed class UserFileAccess : IFileAccess
{
    public T RunAsUser<T>(Func<T> action)
    {
        var session = (uint)Process.GetCurrentProcess().SessionId;
        if (!WTSQueryUserToken(session, out var token))
        {
            var err = Marshal.GetLastWin32Error();
            // 1314 = privilegio mancante: non siamo SYSTEM (agent avviato a mano), quindi siamo già l'utente.
            if (err == 1314) return action();
            throw new UnauthorizedAccessException($"nessun utente collegato a questo PC ({err})");
        }
        using var handle = new SafeAccessTokenHandle(token);
        return WindowsIdentity.RunImpersonated(handle, action);
    }

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
}

/// <summary>Pipe dei dati per l'icona: i file incollati arrivano da qui (vedi <see cref="Varco.Core.Control.ControlProtocol.FilesPipeName"/>).</summary>
internal sealed class FilePipeServer(VarcoHost host, string pipeName) : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    public void Start() => _ = Task.Run(Loop);

    private async Task Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            System.IO.Pipes.NamedPipeServerStream pipe;
            try
            {
                pipe = System.IO.Pipes.NamedPipeServerStreamAcl.Create(pipeName, System.IO.Pipes.PipeDirection.InOut,
                    System.IO.Pipes.NamedPipeServerStream.MaxAllowedServerInstances, System.IO.Pipes.PipeTransmissionMode.Byte,
                    System.IO.Pipes.PipeOptions.Asynchronous, 256 * 1024, 4096, ControlServer.Security());
                await pipe.WaitForConnectionAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                Log.Error("Pipe dei file", ex);
                await Task.Delay(1000).ConfigureAwait(false);
                continue;
            }
            _ = Task.Run(() => Serve(pipe));
        }
    }

    private async Task Serve(System.IO.Pipes.NamedPipeServerStream pipe)
    {
        await using (pipe)
        {
            Varco.Core.Control.FileReadRequest? req = null;
            try
            {
                var line = await ReadLineAsync(pipe, _cts.Token).ConfigureAwait(false);
                req = System.Text.Json.JsonSerializer.Deserialize<Varco.Core.Control.FileReadRequest>(line, Varco.Core.Control.ControlProtocol.Json);
                if (req is null) return;

                FileTransfer.RemoteFile file;
                try
                {
                    file = await host.OpenRemoteFileAsync(req.PeerId, req.OfferId, req.Index, req.Offset, _cts.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or OperationCanceledException
                                               or Varco.Core.Crypto.HandshakeException or System.Security.Cryptography.CryptographicException)
                {
                    Log.Info($"File {req.Index} dell'offerta non disponibile: {ex.Message}");
                    await pipe.WriteAsync(BitConverter.GetBytes(-1L), _cts.Token).ConfigureAwait(false);
                    await pipe.WriteAsync(System.Text.Encoding.UTF8.GetBytes(ex.Message + "\n"), _cts.Token).ConfigureAwait(false);
                    return;
                }
                await using (file)
                {
                    await pipe.WriteAsync(BitConverter.GetBytes(file.Length), _cts.Token).ConfigureAwait(false);
                    await file.CopyToAsync(pipe, 256 * 1024, _cts.Token).ConfigureAwait(false);
                    await pipe.FlushAsync(_cts.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or System.Text.Json.JsonException
                                           or System.Security.Cryptography.CryptographicException or InvalidDataException)
            {
                // L'icona ha chiuso (copia annullata) o la rete è caduta: chiudendo la pipe Esplora risorse vede l'errore.
                Log.Info($"Copia del file {req?.Index} interrotta: {ex.Message}");
            }
        }
    }

    /// <summary>Una riga, byte per byte: dopo la richiesta sulla pipe passano solo dati binari.</summary>
    private static async Task<string> ReadLineAsync(Stream s, CancellationToken ct)
    {
        var bytes = new List<byte>();
        var one = new byte[1];
        while (bytes.Count < 4096)
        {
            if (await s.ReadAsync(one, ct).ConfigureAwait(false) == 0) throw new IOException("richiesta incompleta");
            if (one[0] == '\n') break;
            bytes.Add(one[0]);
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    public void Dispose() => _cts.Cancel();
}
