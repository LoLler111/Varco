using System.Diagnostics;
using Varco.Core.Config;
using Varco.Core.Control;

namespace Varco.App;

/// <summary>Opzioni per i casi particolari (reti diverse, PC che non si trovano da soli).</summary>
internal sealed class AdvancedForm : Form
{
    private static readonly int W = Ui.S(440);

    private readonly InputBox _address;
    private readonly ListBox _manual = new() { IntegralHeight = false, BorderStyle = BorderStyle.None, BackColor = Ui.Well, ForeColor = Ui.Text, DrawMode = DrawMode.OwnerDrawFixed, Margin = Padding.Empty };
    private readonly Toggle _autoUpdate = new("Aggiorna Varco automaticamente (consigliato)");
    private readonly Toggle _clipboard = new("Condividi gli appunti tra i PC");
    private readonly Label _updateText;

    public AdvancedForm(StatusDto? status)
    {
        Ui.StyleForm(this);
        Text = "Varco — avanzate";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = Ui.P(18, 6, 18, 18);

        var root = Ui.Column();
        root.Location = new Point(Padding.Left, Padding.Top);
        Controls.Add(root);

        // collegamento manuale
        var manual = new Card(W, "Collega un PC tramite indirizzo IP");
        manual.Body.Controls.Add(Ui.Label("Serve solo se l'altro PC non compare da solo, ad esempio perché è su un'altra rete. " +
            "L'indirizzo lo trovi nella finestra di Varco sull'altro PC.", color: Ui.Muted, maxWidth: manual.InnerWidth, margin: Ui.P(0, 0, 0, 10)));
        var connect = new VButton("Collega", primary: true);
        connect.Click += async (_, _) => await PairByAddress();
        _address = new InputBox(manual.InnerWidth - connect.Width - Ui.S(8), connect.Height, "es. 192.168.1.20") { Margin = Ui.P(0, 0, 8, 0) };
        _address.Box.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await PairByAddress();
        };
        manual.Body.Controls.Add(Row(_address, connect));
        var mine = Ui.LocalAddresses();
        if (mine.Count > 0)
            manual.Body.Controls.Add(Ui.Label($"Indirizzo di questo PC: {string.Join(", ", mine)}", Ui.Small, Ui.Muted, manual.InnerWidth, Ui.P(0, 8, 0, 0)));

        manual.Body.Controls.Add(Ui.Label("Indirizzi cercati sempre", Ui.BodyStrong, margin: Ui.P(0, 14, 0, 6)));
        var remove = new VButton("Rimuovi");
        var listW = manual.InnerWidth - remove.Width - Ui.S(8);
        var listBox = new Tint(Ui.Well) { Edge = Ui.Line, Padding = Ui.P(6, 6, 6, 6), AutoSize = false, Size = new Size(listW, Ui.S(80)), Margin = Ui.P(0, 0, 8, 0) };
        _manual.ItemHeight = Ui.S(22);
        _manual.Bounds = new Rectangle(Ui.S(6), Ui.S(6), listW - Ui.S(12), Ui.S(68));
        _manual.DrawItem += DrawAddress;
        listBox.Controls.Add(_manual);
        if (status is not null) _manual.Items.AddRange(status.ManualAddresses.ToArray());
        remove.Click += async (_, _) =>
        {
            if (_manual.SelectedItem is not string a) return;
            if (await Send(new ControlRequest { Cmd = "removeAddress", Address = a }) is not null) _manual.Items.Remove(a);
        };
        manual.Body.Controls.Add(Row(listBox, remove));
        root.Controls.Add(manual);

        // appunti
        var clip = new Card(W, "Appunti");
        _clipboard.Checked = status?.ShareClipboard ?? true;
        _clipboard.CheckedChanged += async (_, _) => await Send(new ControlRequest { Cmd = "setClipboard", Enabled = _clipboard.Checked });
        clip.Body.Controls.Add(_clipboard);
        clip.Body.Controls.Add(Ui.Label("Copi su un PC e incolli su un altro: testo, testo formattato e immagini. " +
            "I file non vengono condivisi, e nemmeno ciò che i gestori di password segnano come privato. " +
            "Funziona tra PC con Varco 0.5 o successivo.", Ui.Small, Ui.Muted, clip.InnerWidth, Ui.P(0, 8, 0, 0)));
        root.Controls.Add(clip);

        // problemi
        var problems = new Card(W, "Problemi");
        var reconnect = new VButton("Riconnetti ora");
        reconnect.Click += async (_, _) => await Send(new ControlRequest { Cmd = "reconnect" });
        var logs = new VButton("Apri i log");
        logs.Click += (_, _) =>
        {
            var dir = Path.Combine(ConfigStore.DefaultDirectory, "logs");
            if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
        };
        reconnect.Margin = Ui.P(0, 0, 8, 0);
        problems.Body.Controls.Add(Row(reconnect, logs));
        problems.Body.Controls.Add(Ui.Link("Scollega questo PC da tutti gli altri…", async () =>
        {
            if (MessageBox.Show(this, "Questo PC si scollegherà da tutti gli altri. Potrai ricollegarlo quando vuoi. Continuare?",
                    "Varco", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                await Send(new ControlRequest { Cmd = "leaveGroup" });
        }, Ui.P(0, 12, 0, 0)));
        root.Controls.Add(problems);

        // aggiornamenti
        var updates = new Card(W, "Aggiornamenti");
        _autoUpdate.Checked = status?.AutoUpdate ?? true;
        _autoUpdate.CheckedChanged += async (_, _) => await Send(new ControlRequest { Cmd = "setAutoUpdate", Enabled = _autoUpdate.Checked });
        updates.Body.Controls.Add(_autoUpdate);
        var check = new VButton("Controlla ora");
        check.Click += async (_, _) =>
        {
            if (await Send(new ControlRequest { Cmd = "checkUpdate" }) is { Status: { } s }) ShowUpdate(s);
        };
        _updateText = Ui.Label("", Ui.Small, Ui.Muted, updates.InnerWidth - check.Width - Ui.S(12));
        var updateRow = new LineRow(updates.InnerWidth, Ui.S(44), [check, _updateText], stretch: null) { Margin = Ui.P(0, 10, 0, 0) };
        _updateText.Margin = Ui.P(12, 0, 0, 0);
        updates.Body.Controls.Add(updateRow);
        root.Controls.Add(updates);
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

    private async Task PairByAddress()
    {
        var a = _address.Box.Text.Trim();
        if (a.Length == 0) { _address.Box.Focus(); return; }
        if (await Send(new ControlRequest { Cmd = "pair", Address = a }) is not null)
        {
            // A collegamento riuscito il servizio ricorda da solo l'indirizzo.
            Close(); // l'avanzamento si vede nella finestra principale
        }
    }

    /// <summary>Voce della lista indirizzi: selezione viola scuro invece del blu di sistema.</summary>
    private void DrawAddress(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0) return;
        var selected = (e.State & DrawItemState.Selected) != 0;
        using (var bg = new SolidBrush(selected ? Ui.AccentSoft : Ui.Well))
            e.Graphics.FillRectangle(bg, e.Bounds);
        TextRenderer.DrawText(e.Graphics, _manual.Items[e.Index]?.ToString(), Ui.Body,
            new Rectangle(e.Bounds.X + Ui.S(6), e.Bounds.Y, e.Bounds.Width - Ui.S(6), e.Bounds.Height),
            selected ? Ui.AccentText : Ui.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    private void ShowUpdate(StatusDto? s)
    {
        if (IsDisposed) return;
        _updateText.Text = MainForm.UpdateText(s);
        _updateText.Parent?.PerformLayout();
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

    private static FlowLayoutPanel Row(params Control[] controls)
    {
        var p = Ui.Flow();
        p.Controls.AddRange(controls);
        return p;
    }
}
