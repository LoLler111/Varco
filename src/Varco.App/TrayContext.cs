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
    private static readonly Color Green = Color.FromArgb(46, 160, 67);
    private static readonly Color Orange = Color.FromArgb(219, 140, 20);
    private static readonly Color Blue = Color.FromArgb(40, 110, 220);

    private readonly NotifyIcon _icon;
    private readonly ContextMenuStrip _menu = new();
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };
    private readonly Dictionary<Color, Icon> _icons = new();
    private readonly HashSet<Guid> _announced = new();
    private MainForm? _main;
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

    private void UpdateIcon()
    {
        string text;
        Color color;
        if (_last is null)
        {
            color = Gray;
            text = "Varco: servizio non attivo";
        }
        else
        {
            var connected = _last.Peers.Count(p => p.State == "Connected");
            var total = _last.Peers.Count;
            color = _last.Mode != "Local" ? Blue
                : total > 0 && connected == total ? Green
                : connected > 0 ? Orange
                : Gray;
            text = _last.Mode switch
            {
                "Controlling" => $"Varco: stai usando {_last.ActivePeerName}",
                "Controlled" => $"Varco: {_last.ActivePeerName} sta usando questo PC",
                _ when total == 0 => "Varco: nessun PC collegato",
                _ => $"Varco: {connected} di {total} PC collegati",
            };
        }
        _icon.Icon = IconFor(color);
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var open = new ToolStripMenuItem("Apri Varco", null, (_, _) => ShowMain());
        open.Font = new Font(open.Font, FontStyle.Bold);
        _menu.Items.Add(open);

        if (_last is { Peers.Count: > 0 })
        {
            _menu.Items.Add(new ToolStripSeparator());
            foreach (var p in _last.Peers.OrderBy(p => p.Name))
                _menu.Items.Add(new ToolStripMenuItem($"{(p.State == "Connected" ? "●" : "○")} {p.Name}") { Enabled = false });
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Riconnetti ora", null, async (_, _) =>
        {
            try { await ControlClient.SendAsync(new ControlRequest { Cmd = "reconnect" }); } catch { }
        });
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

    private Icon IconFor(Color color)
    {
        if (_icons.TryGetValue(color, out var icon)) return icon;
        // Dimensione reale dell'icona nella tray (16 px al 100%, 24 al 150%, 32 al 200%).
        var size = SystemInformation.SmallIconSize.Width;
        using var bmp = VarcoIcon.RenderTray(size, color);
        icon = Icon.FromHandle(bmp.GetHicon());
        _icons[color] = icon;
        return icon;
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
