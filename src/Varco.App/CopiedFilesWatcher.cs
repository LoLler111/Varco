using System.Runtime.InteropServices;
using Varco.Core;
using Varco.Core.Control;

namespace Varco.App;

/// <summary>
/// File copiati in Esplora risorse su questo PC → all'agent, che li offre agli altri PC.
///
/// L'agent legge gli appunti da sé, ma gira come SYSTEM: con i file copiati da Esplora risorse (appunti OLE) a volte
/// vede solo il segnaposto "DataObject", senza l'elenco. L'icona gira con l'utente e vede gli appunti per intero.
/// Se l'elenco lo trova anche l'agent, l'offerta è la stessa e lui scarta il doppione.
///
/// Vive sul thread dell'interfaccia (STA, con il suo ciclo di messaggi), che è quello che serve agli appunti OLE.
/// </summary>
internal sealed class CopiedFilesWatcher : NativeWindow, IDisposable
{
    private const int WM_CLIPBOARDUPDATE = 0x031D;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    /// <summary>Esplora risorse scrive gli appunti in più passaggi: si legge quando smette.</summary>
    private readonly System.Windows.Forms.Timer _debounce = new() { Interval = 200 };
    private uint _lastSeq;
    private bool _listening;

    public CopiedFilesWatcher()
    {
        CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
        _listening = AddClipboardFormatListener(Handle);
        if (!_listening) Log.Warn($"Appunti: AddClipboardFormatListener fallito ({Marshal.GetLastWin32Error()})");
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Check();
        };
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_CLIPBOARDUPDATE)
        {
            _debounce.Stop();
            _debounce.Start();
        }
        base.WndProc(ref m);
    }

    private void Check()
    {
        var seq = GetClipboardSequenceNumber();
        if (seq == _lastSeq) return;
        _lastSeq = seq;

        List<string> paths;
        try
        {
            // I file arrivati da un altro PC (file virtuali) non hanno l'elenco dei percorsi: qui non rientrano.
            if (!Clipboard.ContainsFileDropList()) return;
            paths = Clipboard.GetFileDropList().Cast<string>().Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        }
        catch (ExternalException ex)
        {
            Log.Debug($"Appunti occupati, file copiati non letti: {ex.Message}");
            return;
        }
        if (paths.Count == 0) return;

        Log.Info($"File copiati in Esplora risorse: {paths.Count} elementi, li passo all'agent");
        _ = Task.Run(async () =>
        {
            try
            {
                var r = await ControlClient.SendAsync(new ControlRequest { Cmd = "localFiles", Paths = paths }, 5000).ConfigureAwait(false);
                if (!r.Ok) Log.Info($"L'agent non ha preso i file copiati: {r.Error}");
            }
            catch (Exception ex)
            {
                Log.Info($"File copiati non passati all'agent: {ex.Message}");
            }
        });
    }

    public void Dispose()
    {
        _debounce.Dispose();
        if (Handle == IntPtr.Zero) return;
        if (_listening) RemoveClipboardFormatListener(Handle);
        DestroyHandle();
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
