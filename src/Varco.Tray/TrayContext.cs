using System.Drawing.Drawing2D;
using Varco.Core.Control;

namespace Varco.Tray;

/// <summary>Icona nell'area di notifica: stato dei PC a colpo d'occhio, menu e impostazioni.</summary>
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
    private SettingsForm? _settings;
    private StatusDto? _last;
    private bool _polling;

    public TrayContext()
    {
        _icon = new NotifyIcon { Icon = IconFor(Gray), Text = "Varco", Visible = true, ContextMenuStrip = _menu };
        _icon.DoubleClick += (_, _) => ShowSettings();
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
            var resp = await ControlClient.SendAsync(new ControlRequest { Cmd = "status" });
            _last = resp.Status;
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
        _settings?.UpdateStatus(_last);
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
        else if (!_last.HasKey)
        {
            color = Gray;
            text = "Varco: nessun gruppo configurato";
        }
        else
        {
            var connected = _last.Peers.Count(p => p.State == "Connected");
            var total = _last.Peers.Count(p => p.Id is not null);
            color = _last.Mode != "Local" ? Blue
                : total > 0 && connected == total ? Green
                : connected > 0 ? Orange
                : Gray;
            text = _last.Mode switch
            {
                "Controlling" => $"Varco: stai comandando {_last.ActivePeerName}",
                "Controlled" => $"Varco: comandato da {_last.ActivePeerName}",
                _ => $"Varco: {connected}/{total} PC connessi",
            };
        }
        _icon.Icon = IconFor(color);
        _icon.Text = text.Length > 63 ? text[..63] : text;
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        var title = new ToolStripMenuItem(_last is null ? "Varco (servizio non attivo)" : $"Varco — {_last.MachineName}") { Enabled = false };
        title.Font = new Font(title.Font, FontStyle.Bold);
        _menu.Items.Add(title);

        if (_last is { HasKey: true })
        {
            if (_last.Peers.Count == 0)
                _menu.Items.Add(new ToolStripMenuItem("Nessun altro PC trovato") { Enabled = false });
            foreach (var p in _last.Peers.OrderBy(p => p.Name))
            {
                var label = p.State switch
                {
                    "Connected" => $"● {p.Name} — connesso{(p.RttMs >= 0 ? $" ({p.RttMs:0.#} ms)" : "")}",
                    "Connecting" => $"◌ {p.Name} — connessione in corso…",
                    _ => $"○ {p.Name} — non raggiungibile{(p.LastError is null ? "" : $" ({p.LastError})")}",
                };
                _menu.Items.Add(new ToolStripMenuItem(label) { Enabled = false });
            }
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Impostazioni…", null, (_, _) => ShowSettings());
        _menu.Items.Add("Riconnetti ora", null, async (_, _) =>
        {
            try { await ControlClient.SendAsync(new ControlRequest { Cmd = "reconnect" }); } catch { }
        });
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Chiudi icona", null, (_, _) => ExitThread());
    }

    private void ShowSettings()
    {
        if (_settings is { IsDisposed: false })
        {
            _settings.Activate();
            return;
        }
        _settings = new SettingsForm();
        _settings.FormClosed += (_, _) => _settings = null;
        _settings.UpdateStatus(_last);
        _settings.Show();
    }

    private Icon IconFor(Color color)
    {
        if (_icons.TryGetValue(color, out var icon)) return icon;
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(color);
            g.FillEllipse(bg, 1, 1, 30, 30);
            using var pen = new Pen(Color.White, 4.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, [new PointF(9, 9), new PointF(16, 23), new PointF(23, 9)]);
        }
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
