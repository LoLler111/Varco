namespace Varco.App;

/// <summary>Finestra di setup: un pulsante e basta. Compare quando Varco.exe viene aperto fuori da Programmi\Varco.</summary>
internal sealed class SetupForm : Form
{
    private static readonly int W = Ui.S(400);

    private readonly Label _status = Ui.Label("", color: Ui.Muted, maxWidth: W, margin: Ui.P(0, 20, 0, 20));
    private readonly VButton _install;
    private readonly VButton _open = new("Apri Varco", minWidth: 110) { Margin = Ui.P(8, 0, 0, 0) };
    private readonly VButton _cancel = new("Chiudi", minWidth: 90) { Margin = Ui.P(8, 0, 0, 0) };

    public SetupForm()
    {
        Ui.StyleForm(this);
        Text = "Installa Varco";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = Ui.P(26, 24, 26, 22);

        var installed = Installer.InstalledVersion();
        var current = Installer.CurrentVersion;
        string action;
        if (installed is null)
        {
            action = "Installa";
            _status.Text = "Premi «Installa» e conferma la richiesta di Windows. Fai lo stesso sugli altri PC: si troveranno da soli nella rete.";
        }
        else if (installed < new Version(current.Major, current.Minor, current.Build))
        {
            action = "Aggiorna";
            _status.Text = $"Su questo PC c'è Varco {installed.ToString(3)}. Verrà aggiornato alla versione {current.ToString(3)}, mantenendo i PC già collegati.";
        }
        else
        {
            action = "Reinstalla";
            _status.Text = "Varco è già installato su questo PC.";
        }
        // Lo scudo avvisa che Windows chiederà la conferma dell'amministratore.
        _install = new VButton(action, primary: true, minWidth: 130, shield: true);

        var root = Ui.Column();
        root.Location = new Point(Padding.Left, Padding.Top);
        Controls.Add(root);

        var header = Ui.Flow();
        header.Controls.Add(new PictureBox { Image = VarcoIcon.RenderApp(Ui.S(64), gradient: true), Size = Ui.S(64, 64), Margin = Ui.P(0, 0, 16, 0) });
        var titles = Ui.Column();
        titles.Controls.Add(Ui.Label("Varco", Ui.Semibold(20f)));
        titles.Controls.Add(Ui.Label("Un mouse e una tastiera per tutti i tuoi PC, via cavo o Wi-Fi.", color: Ui.Muted, maxWidth: W - Ui.S(80), margin: Ui.P(2, 2, 0, 0)));
        header.Controls.Add(titles);
        root.Controls.Add(header);
        root.Controls.Add(_status);

        var buttons = Ui.Flow();
        buttons.Controls.AddRange([_install, _open, _cancel]);
        _open.Visible = installed is not null;
        buttons.Size = buttons.PreferredSize;
        root.Controls.Add(new LineRow(W, _install.Height, [], buttons));

        _install.Click += async (_, _) => await Install();
        _open.Click += (_, _) => { Installer.LaunchTray(); Close(); };
        _cancel.Click += (_, _) => Close();
        AcceptButton = installed is not null && action == "Reinstalla" ? _open : _install;
        CancelButton = _cancel;
    }

    private async Task Install()
    {
        _install.Enabled = _open.Enabled = _cancel.Enabled = false;
        _status.ForeColor = Ui.Text;
        _status.Text = "Installazione in corso…";
        var rc = await Task.Run(() => Installer.RunElevated("--install"));
        if (rc == 0)
        {
            _status.ForeColor = Ui.Ok;
            _status.Text = "Fatto. Varco è attivo: trovi la sua icona viola vicino all'orologio.";
            Installer.LaunchTray();
            await Task.Delay(1500);
            Close();
            return;
        }
        _status.ForeColor = Ui.Bad;
        _status.Text = rc == -2
            ? "Installazione annullata: per installare Varco serve confermare la richiesta di Windows."
            : $"Installazione non riuscita. {ReadError()}";
        _install.Enabled = _open.Enabled = _cancel.Enabled = true;
    }

    private static string ReadError()
    {
        try { return File.Exists(Installer.ErrorFile) ? File.ReadAllText(Installer.ErrorFile) : ""; }
        catch { return ""; }
    }
}
