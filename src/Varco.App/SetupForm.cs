using System.Runtime.InteropServices;

namespace Varco.App;

/// <summary>Finestra di setup: un pulsante e basta. Compare quando Varco.exe viene aperto fuori da Programmi\Varco.</summary>
internal sealed class SetupForm : Form
{
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(380, 0), Margin = new Padding(0, 12, 0, 12) };
    private readonly Button _install = new() { AutoSize = true, MinimumSize = new Size(130, 34), FlatStyle = FlatStyle.System };
    private readonly Button _open = new() { Text = "Apri Varco", AutoSize = true, MinimumSize = new Size(110, 34) };
    private readonly Button _cancel = new() { Text = "Chiudi", AutoSize = true, MinimumSize = new Size(90, 34) };

    public SetupForm()
    {
        Text = "Installa Varco";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(22, 18, 22, 16);
        Font = SystemFonts.MessageBoxFont ?? Font;

        var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        Controls.Add(root);

        var header = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        header.Controls.Add(new PictureBox { Image = VarcoIcon.RenderApp(64), Size = new Size(64, 64), Margin = new Padding(0, 0, 14, 0) });
        var titles = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        titles.Controls.Add(new Label { Text = "Varco", AutoSize = true, Font = new Font(Font.FontFamily, 18f, FontStyle.Bold) });
        titles.Controls.Add(new Label { Text = "Un mouse e una tastiera per tutti i tuoi PC.", AutoSize = true, MaximumSize = new Size(300, 0) });
        header.Controls.Add(titles);
        root.Controls.Add(header);
        root.Controls.Add(_status);

        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        buttons.Controls.AddRange([_install, _open, _cancel]);
        root.Controls.Add(buttons);

        _install.Click += async (_, _) => await Install();
        _open.Click += (_, _) => { Installer.LaunchTray(); Close(); };
        _cancel.Click += (_, _) => Close();
        AcceptButton = _install;
        CancelButton = _cancel;

        var installed = Installer.InstalledVersion();
        var current = Installer.CurrentVersion;
        _open.Visible = installed is not null;
        if (installed is null)
        {
            _install.Text = "Installa";
            _status.Text = "Premi «Installa» e conferma la richiesta di Windows. Fai lo stesso sugli altri PC: si troveranno da soli nella rete.";
        }
        else if (installed < new Version(current.Major, current.Minor, current.Build))
        {
            _install.Text = "Aggiorna";
            _status.Text = $"Su questo PC c'è Varco {installed.ToString(3)}. Verrà aggiornato alla versione {current.ToString(3)}, mantenendo i PC già collegati.";
        }
        else
        {
            _install.Text = "Reinstalla";
            _status.Text = "Varco è già installato su questo PC.";
            AcceptButton = _open;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SendMessage(_install.Handle, 0x160C /* BCM_SETSHIELD */, IntPtr.Zero, new IntPtr(1));
    }

    private async Task Install()
    {
        _install.Enabled = _open.Enabled = _cancel.Enabled = false;
        _status.ForeColor = SystemColors.ControlText;
        _status.Text = "Installazione in corso…";
        var rc = await Task.Run(() => Installer.RunElevated("--install"));
        if (rc == 0)
        {
            _status.Text = "Fatto. Varco è attivo: trovi la sua icona viola vicino all'orologio.";
            Installer.LaunchTray();
            await Task.Delay(1500);
            Close();
            return;
        }
        _status.ForeColor = Color.FromArgb(0xA3, 0x2D, 0x2D);
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

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
