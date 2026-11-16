using System.Runtime.InteropServices;
using Varco.Core;
using Varco.Core.Control;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace Varco.App;

/// <summary>
/// Trascinamento di file da uno schermo all'altro: la parte che deve girare come utente, perché Esplora risorse non può
/// trascinare file su una finestra di SYSTEM (l'agent) né riceverli da lei.
///
///  • Sul PC da cui si trascina (evento Probe): appena il pulsante premuto spinge contro il bordo, una finestrella
///    invisibile compare sotto il cursore. Se Esplora risorse sta trascinando dei file, ce li "passa sopra" e da lì si
///    leggono i percorsi, che vanno all'agent. La finestrella non accetta mai il rilascio: qui non cade niente.
///  • Sul PC dove arrivano (evento Source): un'altra finestrella invisibile compare sotto il cursore; l'agent ci preme
///    sopra il pulsante e da lì parte un trascinamento vero, con gli stessi "file virtuali" del copia e incolla.
///    L'utente lo guida dall'altro PC e rilascia sopra una cartella: Esplora risorse copia, chiedendo i dati all'altro PC.
///
/// Le finestre vivono sul thread STA dei file virtuali (OLE inizializzato, ciclo di messaggi proprio).
/// </summary>
internal sealed class FileDragBridge : IDisposable
{
    private readonly VirtualFileClipboard _sta;
    private readonly CancellationTokenSource _cts = new();
    private DragCatcher? _catcher;
    private DragSource? _source;
    /// <summary>L'agent ha annullato il trascinamento in arrivo (letto da QueryContinueDrag durante il trascinamento).</summary>
    private volatile bool _cancelled;

    public FileDragBridge(VirtualFileClipboard sta)
    {
        _sta = sta;
        _ = Task.Run(PollLoop);
    }

    private async Task PollLoop()
    {
        long? seq = null;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await ControlClient.SendAsync(new ControlRequest { Cmd = "dragEvents", Seq = seq }, 35_000).ConfigureAwait(false);
                if (r.DragEvent is { } e)
                {
                    // Al primo giro si prende solo il numero: un evento vecchio non deve far comparire finestre all'avvio.
                    if (seq is not null && e.Seq > seq) Handle(e);
                    seq = e.Seq;
                }
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            {
                Log.Debug($"Eventi di trascinamento: {ex.Message}");
                try { await Task.Delay(3000, _cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            catch (OperationCanceledException) { }
        }
    }

    private void Handle(DragEventDto e)
    {
        switch (e.Kind)
        {
            case "Probe":
                _sta.Post(() => ShowCatcher(e.Seq));
                break;
            case "Source" when e.Offer is not null || e.Data is { Count: > 0 }:
                _cancelled = false;
                _sta.Post(() => ShowSource(e));
                break;
            case "End":
                _cancelled = true;
                _sta.Post(() =>
                {
                    _catcher?.Hide();
                    if (_source is { Dragging: false }) _source.Hide();
                });
                break;
        }
    }

    // ───────────── PC da cui si trascina ─────────────

    private void ShowCatcher(long seq)
    {
        _catcher ??= new DragCatcher();
        var answered = false;
        void Answer(List<string> paths, List<ClipFormatDto>? formats)
        {
            if (answered) return;
            answered = true;
            var req = paths.Count == 0 && formats is { Count: > 0 }
                ? new ControlRequest { Cmd = "dragData", Seq = seq, Formats = formats }
                : new ControlRequest { Cmd = "dragFiles", Seq = seq, Paths = paths };
            _ = ControlClient.SendAsync(req, 10_000)
                .ContinueWith(t => { if (t.IsFaulted) Log.Debug($"{req.Cmd}: {t.Exception?.InnerException?.Message}"); }, TaskScheduler.Default);
        }
        _catcher.Seen = Answer;
        _catcher.ShowUnderCursor();
        // Il cursore è fermo contro il bordo: lo sposto di un pixel e lo rimetto, così il trascinamento in corso si
        // accorge subito della finestrella (lo controlla a ogni movimento).
        var p = Cursor.Position;
        var center = SystemInformation.VirtualScreen;
        SetCursorPos(p.X + Math.Sign(center.Left + center.Width / 2 - p.X), p.Y + Math.Sign(center.Top + center.Height / 2 - p.Y));
        SetCursorPos(p.X, p.Y);
        // Nessun file entro poco: non si sta trascinando niente che si possa passare (testo, una finestra, ...).
        var timer = new System.Windows.Forms.Timer { Interval = 600 };
        timer.Tick += (_, _) =>
        {
            timer.Dispose();
            Answer([], null);
        };
        timer.Start();
        // Per sicurezza: se l'agent non dice più niente, la finestrella sparisce da sola.
        _catcher.HideAfter(8000);
    }

    // ───────────── PC dove arrivano i file ─────────────

    private void ShowSource(DragEventDto e)
    {
        var seq = e.Seq;
        _source ??= new DragSource(() => _cancelled);
        _source.Offer = e.Offer;
        _source.Data = e.Offer is null ? e.Data : null;
        _source.DataFrom = e.PeerName ?? "";
        _source.ShowUnderCursor();
        _source.HideAfter(5000); // se il pulsante non arriva, via
        _ = ControlClient.SendAsync(new ControlRequest { Cmd = "dragReady", Seq = seq }, 5000)
            .ContinueWith(t => { if (t.IsFaulted) Log.Debug($"dragReady: {t.Exception?.InnerException?.Message}"); }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _sta.Post(() =>
        {
            _catcher?.Dispose();
            _source?.Dispose();
        });
    }

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);
}

/// <summary>Finestrella invisibile (alfa 2/255: il mouse la "vede"), sempre in primo piano, che non prende mai il focus.</summary>
internal class InvisibleWindow : Form
{
    private const int WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    private const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
    private readonly System.Windows.Forms.Timer _hideTimer = new();

    protected InvisibleWindow(int size)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.Black;
        Opacity = 0.01;
        AutoScaleMode = AutoScaleMode.None;
        Size = new Size(size, size);
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            OnHideTimeout();
        };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_MOUSEACTIVATE)
        {
            m.Result = MA_NOACTIVATE;
            return;
        }
        base.WndProc(ref m);
    }

    public void ShowUnderCursor()
    {
        var p = Cursor.Position;
        Location = new Point(p.X - Width / 2, p.Y - Height / 2);
        if (!Visible) Show();
        BringToFront();
    }

    public void HideAfter(int ms)
    {
        _hideTimer.Stop();
        _hideTimer.Interval = ms;
        _hideTimer.Start();
    }

    protected virtual void OnHideTimeout() => Hide();

    protected override void OnVisibleChanged(EventArgs e)
    {
        if (!Visible) _hideTimer.Stop();
        base.OnVisibleChanged(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _hideTimer.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Bersaglio finto contro il bordo: legge i file (o testo, immagini) trascinati e non accetta mai il rilascio.</summary>
internal sealed class DragCatcher : InvisibleWindow
{
    /// <summary>Percorsi dei file trascinati; se non ce ne sono, gli altri formati letti (null = niente).</summary>
    public Action<List<string>, List<ClipFormatDto>?>? Seen; // campo, non proprietà: niente serializzazione del designer

    public DragCatcher() : base(24)
    {
        AllowDrop = true;
    }

    protected override void OnDragEnter(DragEventArgs e)
    {
        e.Effect = DragDropEffects.None;
        try
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
            {
                Log.Info($"Trascinamento contro il bordo: {paths.Length} elementi");
                Seen?.Invoke(paths.ToList(), null);
            }
            else if (e.Data is ComTypes.IDataObject com && DragDataReader.Read(com) is { Count: > 0 } formats)
            {
                // Testo, HTML, immagini (da un browser, da Word…), anche file senza percorso.
                Log.Info($"Trascinamento contro il bordo: {string.Join(", ", formats.Select(f => f.Name))}");
                Seen?.Invoke([], formats);
            }
            else
            {
                Seen?.Invoke([], null); // si trascina qualcos'altro (una finestra, un formato che non passa)
            }
        }
        catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
        {
            Log.Debug($"Trascinamento non letto: {ex.Message}");
            Seen?.Invoke([], null);
        }
    }

    protected override void OnDragOver(DragEventArgs e) => e.Effect = DragDropEffects.None;
}

/// <summary>Da qui parte il trascinamento dei file arrivati da un altro PC, quando l'agent ci preme sopra il pulsante.</summary>
internal sealed class DragSource : InvisibleWindow
{
    private const int DROPEFFECT_COPY = 1;
    private readonly DropSource _dropSource;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public FilesOfferDto? Offer { get; set; }
    /// <summary>Testo o immagini arrivati da un altro PC (al posto di <see cref="Offer"/>).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public List<ClipFormatDto>? Data { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string DataFrom { get; set; } = "";
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Dragging { get; private set; }

    public DragSource(Func<bool> cancelled) : base(48)
    {
        _dropSource = new DropSource(cancelled);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || Dragging || (Offer is null && Data is not { Count: > 0 })) return;
        Dragging = true;
        try
        {
            ComTypes.IDataObject data;
            if (Offer is { } offer)
            {
                var files = new VirtualFilesDataObject(offer);
                Log.Info($"Trascinamento di {files.Count} elementi arrivati da {offer.PeerName}");
                data = files;
            }
            else
            {
                var memory = new MemoryDataObject(Data!);
                if (memory.IsEmpty) return;
                Log.Info($"Trascinamento di testo o immagini arrivati da {DataFrom}");
                data = memory;
            }
            // Bloccante finché l'utente non rilascia (o annulla). Se rilascia su una cartella, Esplora risorse copia
            // da qui dentro, chiedendo i dati all'oggetto come per l'incolla.
            var hr = DoDragDrop(data, _dropSource, DROPEFFECT_COPY, out var effect);
            Log.Info(hr == DropSource.DRAGDROP_S_DROP && effect != 0 ? "Lasciato cadere: copia avviata" : "Trascinamento finito senza copiare nulla");
        }
        catch (Exception ex)
        {
            Log.Error("Trascinamento dei file", ex);
        }
        finally
        {
            Dragging = false;
            Hide();
        }
    }

    protected override void OnHideTimeout()
    {
        if (!Dragging) Hide();
    }

    [DllImport("ole32.dll")]
    private static extern int DoDragDrop(ComTypes.IDataObject dataObject, IDropSource dropSource, int okEffects, out int effect);
}

/// <summary>Regole del trascinamento in arrivo: si lascia cadere al rilascio del pulsante, si annulla con Esc o se l'agent lo chiede.</summary>
[ComVisible(true)]
public sealed class DropSource : IDropSource
{
    public const int DRAGDROP_S_DROP = 0x00040100, DRAGDROP_S_CANCEL = 0x00040101, DRAGDROP_S_USEDEFAULTCURSORS = 0x00040102;
    private const int MK_LBUTTON = 1, VK_ESCAPE = 0x1B;
    private readonly Func<bool> _cancelled;

    internal DropSource(Func<bool> cancelled) => _cancelled = cancelled;

    public int QueryContinueDrag(int fEscapePressed, int grfKeyState)
    {
        // Esc premuto sull'altro PC arriva come tasto iniettato al programma in primo piano, non per forza a noi.
        if (fEscapePressed != 0 || _cancelled() || (GetAsyncKeyState(VK_ESCAPE) & 0x8000) != 0) return DRAGDROP_S_CANCEL;
        return (grfKeyState & MK_LBUTTON) == 0 ? DRAGDROP_S_DROP : 0;
    }

    public int GiveFeedback(int dwEffect) => DRAGDROP_S_USEDEFAULTCURSORS;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vk);
}

[ComImport, Guid("00000121-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDropSource
{
    [PreserveSig] int QueryContinueDrag(int fEscapePressed, int grfKeyState);
    [PreserveSig] int GiveFeedback(int dwEffect);
}
