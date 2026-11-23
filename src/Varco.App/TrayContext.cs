using Varco.Core.Control;

namespace Varco.App;

/// <summary>
/// Icona nell'area di notifica. Oltre a mostrare lo stato:
///  • apre la finestra principale al primo avvio (finché non c'è nessun PC collegato);
///  • avvisa con una notifica quando compare un nuovo PC con Varco nella rete;
///  • mostra in primo piano le richieste di collegamento in arrivo.
/// </summary>
internal sealed class TrayContext : ApplicationContext
{
    private static readonly Color Gray = Color.FromArgb(140, 140, 140);
    private static readonly Color Purple = Color.FromArgb(120, 70, 210);   // questo PC comanda un altro PC o se stesso
    private static readonly Color Red = Color.FromArgb(210, 55, 55);       // questo PC è comandato da un altro

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Dictionary<(Color, TrayGlyph), Icon> _icons = new();
    private readonly Dictionary<(Color, TrayGlyph), Image> _menuImages = new();
    private readonly HashSet<Guid> _announced = new();
    private MainForm? _main;
    private readonly VirtualFileClipboard _files;
    private readonly CopiedFilesWatcher _copied;
    private readonly FileDragBridge _drag;
    private PairRequestForm? _request;
    private StatusDto? _last;
    private bool _polling;
    private bool _welcomeShown;
    private bool _firstPoll = true;
    private bool _afterUpdate;

    public const string ShowEventName = @"Local\Varco.Tray.Show";

    public TrayContext(EventWaitHandle showRequests, bool afterUpdate = false)
    {
        _afterUpdate = afterUpdate;
        _welcomeShown = afterUpdate; // dopo un aggiornamento automatico niente finestra di benvenuto: basta la notifica
        var ui = SynchronizationContext.Current;
        ThreadPool.RegisterWaitForSingleObject(showRequests, (_, _) => ui?.Post(_ => ShowMain(), null), null, Timeout.Infinite, executeOnlyOnce: false);
        _icon = new NotifyIcon { Icon = IconFor(Gray), Text = "Varco", Visible = true, ContextMenuStrip = _menu };
        _icon.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
        _icon.BalloonTipClicked += (_, _) => ShowMain();
        _menu.Renderer = new DarkMenuRenderer();
        _menu.ForeColor = Ui.Text;
        _menu.Opening += (_, _) => BuildMenu();
        _timer.Tick += async (_, _) => await Poll();
        _timer.Start();
        _files = new VirtualFileClipboard();
        _copied = new CopiedFilesWatcher();
        _drag = new FileDragBridge(_files);
        _ = Poll();
    }

    private async Task Poll()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            _last = (await ControlClient.SendAsync(new ControlRequest { Cmd = "status" })).Status;
        }
        catch
        {
            _last = null;
        }
        finally
        {
            _polling = false;
        }

        UpdateIcon();
        _main?.UpdateStatus(_last);
        if (_last is null) return;

        if (_afterUpdate)
        {
            _afterUpdate = false;
            _icon.ShowBalloonTip(5000, "Varco aggiornato", $"Ora usi la versione {Installer.CurrentVersion.ToString(3)}.", ToolTipIcon.Info);
        }

        HandleIncomingRequest(_last.IncomingPair);
        NotifyNewPcs(_last);

        // Primo avvio (nessun PC collegato): apro subito la finestra, così si vede cosa fare.
        if (!_welcomeShown && _last.Peers.Count == 0)
        {
            _welcomeShown = true;
            ShowMain();
        }
        _welcomeShown = true;
        _firstPoll = false;
    }

    private void HandleIncomingRequest(PairRequestDto? req)
    {
        if (req is null)
        {
            // scaduta o gestita altrove
            if (_request is { IsDisposed: false }) _request.Close();
            _request = null;
            return;
        }
        if (_request is { IsDisposed: false } && _request.RequestId == req.RequestId) return;
        _request?.Close();
        _request = new PairRequestForm(req);
        _request.FormClosed += (_, _) => _request = null;
        _request.Show();
        _request.Activate();
    }

    private void NotifyNewPcs(StatusDto s)
    {
        foreach (var d in s.Discovered)
        {
            if (!_announced.Add(d.Id)) continue;
            if (_main is { Visible: true }) continue; // lo vede già nella finestra
            if (_firstPoll && s.Peers.Count == 0) continue; // si apre comunque la finestra di benvenuto
            _icon.ShowBalloonTip(6000, "Trovato un PC con Varco", $"{d.Name} è nella tua rete. Clicca qui per collegarlo.", ToolTipIcon.Info);
        }
    }

    /// <summary>Come appare lo stato nella tray: colore, simbolo e frase (tooltip e prima riga del menu).</summary>
    internal readonly record struct TrayState(Color Color, TrayGlyph Glyph, string Text);

    /// <summary>
    /// Conta chi comanda chi; quanti PC sono connessi si legge solo nella frase.
    ///  • viola con la freccia del mouse (master): mouse e tastiera di questo PC comandano un altro PC o questo stesso;
    ///  • rosso con uno schermo (slave): questo PC è comandato da un altro, anche se adesso il mouse è altrove,
    ///    finché nessuno tocca il suo mouse o la sua tastiera;
    ///  • grigio con la croce: non connesso a nessun PC (o servizio spento);
    ///  • grigio con gli schermi: connessione disattivata o nessun PC abbinato.
    /// </summary>
    internal static TrayState Describe(StatusDto? s)
    {
        if (s is null) return new(Gray, TrayGlyph.Off, "Servizio Varco non attivo");
        if (s.Paused) return new(Gray, TrayGlyph.Screens, "Connessione disattivata");
        if (s.Mode == "Controlling") return new(Purple, TrayGlyph.Cursor, $"Stai comandando {s.ActivePeerName ?? "un altro PC"}");
        if (s.Mode == "Controlled") return new(Red, TrayGlyph.Monitor, $"Comandato da {s.ActivePeerName ?? "un altro PC"}");
        // Il mouse è tornato sul master (o è passato a un altro PC) e nessuno ha toccato questo: resta slave.
        if (s.ControllerName is { } master) return new(Red, TrayGlyph.Monitor, $"Comandato da {master}");
        var connected = s.Peers.Count(p => p.State == "Connected");
        var total = s.Peers.Count;
        if (total == 0) return new(Gray, TrayGlyph.Screens, "Nessun PC abbinato");
        if (connected == 0) return new(Gray, TrayGlyph.Off, "Non connesso a nessun PC");
        return new(Purple, TrayGlyph.Cursor, $"Stai comandando questo PC ({connected} di {total} PC connessi)");
    }

    private void UpdateIcon()
    {
        var state = Describe(_last);
        var text = "Varco: " + state.Text;
        _icon.Icon = IconFor(state.Color, state.Glyph);
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    /// <summary>Ruolo di un PC abbinato, per l'elenco nel menu.</summary>
    private static string PeerLine(StatusDto s, PeerDto p)
    {
        var active = s.ActivePeerName == p.Name;
        if (active && s.Mode == "Controlling") return $"● {p.Name} — lo stai comandando";
        if ((active && s.Mode == "Controlled") || s.ControllerName == p.Name) return $"● {p.Name} — comanda questo PC";
        return p.State == "Connected" ? $"● {p.Name} — connesso" : $"○ {p.Name} — non connesso";
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var state = Describe(_last);
        _menu.Items.Add(new ToolStripMenuItem(state.Text, MenuImage(state)) { Enabled = false });
        _menu.Items.Add(new ToolStripSeparator());

        var open = new ToolStripMenuItem("Apri Varco", null, (_, _) => ShowMain());
        open.Font = new Font(open.Font, FontStyle.Bold);
        _menu.Items.Add(open);

        if (_last is { Peers.Count: > 0 } s)
        {
            _menu.Items.Add(new ToolStripSeparator());
            foreach (var p in s.Peers.OrderBy(p => p.Name))
                _menu.Items.Add(new ToolStripMenuItem(PeerLine(s, p)) { Enabled = false });
        }

        _menu.Items.Add(new ToolStripSeparator());
        if (_last is not null)
        {
            var paused = _last.Paused;
            _menu.Items.Add(paused ? "Riattiva connessione" : "Disattiva connessione", null, async (_, _) =>
            {
                try { await ControlClient.SendAsync(new ControlRequest { Cmd = "setPaused", Enabled = !paused }, 5000); } catch { }
                await Poll();
            });
        }
        if (_last is not { Paused: true })
        {
            _menu.Items.Add("Riconnetti ora", null, async (_, _) =>
            {
                try { await ControlClient.SendAsync(new ControlRequest { Cmd = "reconnect" }); } catch { }
            });
        }
        _menu.Items.Add("Nascondi icona", null, (_, _) => ExitThread());
    }

    private void ShowMain()
    {
        if (_main is { IsDisposed: false })
        {
            if (_main.WindowState == FormWindowState.Minimized) _main.WindowState = FormWindowState.Normal;
            _main.Activate();
            return;
        }
        _main = new MainForm();
        _main.FormClosed += (_, _) => _main = null;
        _main.UpdateStatus(_last);
        _main.Show();
        _main.Activate();
    }

    private Image MenuImage(TrayState state)
    {
        if (_menuImages.TryGetValue((state.Color, state.Glyph), out var img)) return img;
        img = IconFor(state.Color, state.Glyph).ToBitmap();
        _menuImages[(state.Color, state.Glyph)] = img;
        return img;
    }

    private Icon IconFor(Color color, TrayGlyph glyph = TrayGlyph.Screens)
    {
        if (_icons.TryGetValue((color, glyph), out var icon)) return icon;
        // Dimensione reale dell'icona nella tray (16 px al 100%, 24 al 150%, 32 al 200%).
        var size = SystemInformation.SmallIconSize.Width;
        using var bmp = VarcoIcon.RenderTray(size, color, glyph);
        icon = Icon.FromHandle(bmp.GetHicon());
        _icons[(color, glyph)] = icon;
        return icon;
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _drag.Dispose();
        _files.Dispose();
        _copied.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
