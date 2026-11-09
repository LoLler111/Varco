using System.Diagnostics;
using Varco.Core.Config;
using Varco.Core.Control;

namespace Varco.App;

/// <summary>Opzioni per i casi particolari (reti diverse, PC che non si trovano da soli).</summary>
internal sealed class AdvancedForm : Form
{
    private readonly TextBox _address = new() { Width = 250, PlaceholderText = "192.168.1.20" };
    private readonly ListBox _manual = new() { Width = 250, Height = 64, IntegralHeight = false };

    public AdvancedForm(StatusDto? status)
    {
        Text = "Varco — avanzate";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(18, 12, 18, 14);
        Font = SystemFonts.MessageBoxFont ?? Font;

        var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        Controls.Add(root);

        root.Controls.Add(Header("Collega un PC tramite indirizzo IP"));
        root.Controls.Add(Hint("Serve solo se l'altro PC non compare da solo, ad esempio perché è su un'altra rete."));
        root.Controls.Add(Row(_address, Btn("Collega", async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_address.Text)) return;
            if (await Send(new ControlRequest { Cmd = "pair", Address = _address.Text.Trim() }) is not null)
            {
                await Send(new ControlRequest { Cmd = "addAddress", Address = _address.Text.Trim() });
                Close(); // l'avanzamento si vede nella finestra principale
            }
        })));

        root.Controls.Add(Header("Indirizzi cercati sempre"));
        if (status is not null) _manual.Items.AddRange(status.ManualAddresses.ToArray());
        root.Controls.Add(Row(_manual, Btn("Rimuovi", async (_, _) =>
        {
            if (_manual.SelectedItem is not string a) return;
            if (await Send(new ControlRequest { Cmd = "removeAddress", Address = a }) is not null) _manual.Items.Remove(a);
        })));

        root.Controls.Add(Header("Problemi"));
        root.Controls.Add(Row(
            Btn("Riconnetti ora", async (_, _) => await Send(new ControlRequest { Cmd = "reconnect" })),
            Btn("Apri i log", (_, _) =>
            {
                var dir = Path.Combine(ConfigStore.DefaultDirectory, "logs");
                if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }),
            Btn("Scollega questo PC da tutti", async (_, _) =>
            {
                if (MessageBox.Show(this, "Questo PC si scollegherà da tutti gli altri. Potrai ricollegarlo quando vuoi. Continuare?",
                        "Varco", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                    await Send(new ControlRequest { Cmd = "leaveGroup" });
            })));
        root.Controls.Add(Header("Aggiornamenti"));
        _autoUpdate.Checked = status?.AutoUpdate ?? true;
        _autoUpdate.CheckedChanged += async (_, _) => await Send(new ControlRequest { Cmd = "setAutoUpdate", Enabled = _autoUpdate.Checked });
        root.Controls.Add(_autoUpdate);
        root.Controls.Add(Row(Btn("Controlla ora", async (_, _) =>
        {
            if (await Send(new ControlRequest { Cmd = "checkUpdate" }) is { Status: { } s }) ShowUpdate(s);
        }), _updateText));
        ShowUpdate(status);

        // Lo stato dell'aggiornamento cambia mentre la finestra è aperta (controllo, download…).
        var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += async (_, _) =>
        {
            try { ShowUpdate((await ControlClient.SendAsync(new ControlRequest { Cmd = "status" })).Status); } catch { }
        };
        timer.Start();
        FormClosed += (_, _) => timer.Dispose();
    }

    private readonly CheckBox _autoUpdate = new() { Text = "Aggiorna Varco automaticamente (consigliato)", AutoSize = true, Margin = new Padding(0, 2, 0, 2) };
    private readonly Label _updateText = new() { AutoSize = true, MaximumSize = new Size(300, 0), ForeColor = SystemColors.GrayText, Margin = new Padding(6, 7, 0, 0) };

    private void ShowUpdate(StatusDto? s)
    {
        if (IsDisposed) return;
        _updateText.Text = MainForm.UpdateText(s);
    }

    private async Task<ControlResponse?> Send(ControlRequest req)
    {
        try
        {
            var r = await ControlClient.SendAsync(req, 5000);
            if (r.Ok) return r;
            MessageBox.Show(this, r.Error ?? "Errore sconosciuto", "Varco", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Il servizio Varco non risponde: {ex.Message}", "Varco", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        return null;
    }

    private Label Header(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(Font, FontStyle.Bold),
        Margin = new Padding(0, 12, 0, 2),
    };

    private static Label Hint(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(420, 0), ForeColor = SystemColors.GrayText };

    private static Button Btn(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Height = 28 };
        b.Click += onClick;
        return b;
    }

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };
        p.Controls.AddRange(controls);
        return p;
    }
}
