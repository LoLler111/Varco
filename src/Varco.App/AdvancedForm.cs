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
    private readonly Toggle _standby = new("Spegni i monitor di questo PC mentre usi un altro PC");
    private readonly Toggle _audioKeepsOn = new("Tieni acceso lo schermo mentre suona musica o un video") { Margin = Ui.P(0, 12, 0, 0) };
    private readonly Label _updateText;
    private readonly VButton _installNow = new("Aggiorna subito tutti i PC", primary: true);
    private readonly Card _audio = new(W, "Audio fra i PC");
    private readonly FlowLayoutPanel _audioBody = Ui.Column();
    private string _audioShown = "";
    private StatusDto? _status;

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

        // Due colonne: in una sola la finestra non starebbe in uno schermo da 1080 righe.
        var columns = Ui.Flow();
        columns.Location = new Point(Padding.Left, Padding.Top);
        Controls.Add(columns);
        var left = Ui.Column();
        var right = Ui.Column();
        right.Margin = Ui.P(16, 0, 0, 0);
        columns.Controls.Add(left);
        columns.Controls.Add(right);

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
        left.Controls.Add(manual);

        // appunti
        var clip = new Card(W, "Appunti");
        _clipboard.Checked = status?.ShareClipboard ?? true;
        _clipboard.CheckedChanged += async (_, _) => await Send(new ControlRequest { Cmd = "setClipboard", Enabled = _clipboard.Checked });
        clip.Body.Controls.Add(_clipboard);
        clip.Body.Controls.Add(Ui.Label("Copi su un PC e incolli su un altro: testo, testo formattato, immagini e file. " +
            "I file passano solo quando li incolli, con la barra di avanzamento di Windows. " +
            "Non passa ciò che i gestori di password segnano come privato. " +
            "Testo e immagini richiedono Varco 0.5 su tutti e due i PC, i file Varco 0.5.1.", Ui.Small, Ui.Muted, clip.InnerWidth, Ui.P(0, 8, 0, 0)));
        left.Controls.Add(clip);

        // monitor
        var screens = new Card(W, "Monitor");
        _standby.Checked = status?.MonitorStandby ?? true;
        _standby.CheckedChanged += async (_, _) => await Send(new ControlRequest { Cmd = "setMonitorStandby", Enabled = _standby.Checked });
        screens.Body.Controls.Add(_standby);
        screens.Body.Controls.Add(Ui.Label("Mentre comandi un altro PC, Windows crede che tu stia usando questo e non ne spegne mai lo schermo. " +
            "Varco mette in standby i monitor dopo il tempo impostato in Windows (Impostazioni › Sistema › Alimentazione) " +
            "e li riaccende quando torni qui, salvo che un video o una presentazione li tenga accesi. " +
            "Funziona con i monitor esterni che supportano DDC/CI (di solito si attiva dal menu del monitor); " +
            "lo schermo dei portatili resta acceso.", Ui.Small, Ui.Muted, screens.InnerWidth, Ui.P(0, 8, 0, 0)));
        _audioKeepsOn.Checked = status?.KeepScreenOnWithAudio ?? true;
        _audioKeepsOn.CheckedChanged += async (_, _) => await Send(new ControlRequest { Cmd = "setKeepScreenOnWithAudio", Enabled = _audioKeepsOn.Checked });
        screens.Body.Controls.Add(_audioKeepsOn);
        screens.Body.Controls.Add(Ui.Label("Utile se le casse sono collegate al monitor e si spengono con lui. " +
            "Sul PC che usi da un altro PC, Windows spegnerebbe lo schermo anche con la musica: Varco lo tiene acceso finché esce del suono. " +
            "Sul PC da cui comandi, i monitor non vanno in standby finché esce del suono. " +
            "Da impostare su ogni PC.", Ui.Small, Ui.Muted, screens.InnerWidth, Ui.P(0, 8, 0, 0)));
        left.Controls.Add(screens);

        // mouse e tastiera
        var input = new Card(W, "Mouse e tastiera");
        void InputToggle(string text, string option, bool value, string help, bool first = false)
        {
            var t = new Toggle(text) { Checked = value, Margin = first ? Padding.Empty : Ui.P(0, 12, 0, 0) };
            t.CheckedChanged += async (_, _) => await Send(new ControlRequest { Cmd = "setInputOption", Option = option, Enabled = t.Checked });
            input.Body.Controls.Add(t);
            input.Body.Controls.Add(Ui.Label(help, Ui.Small, Ui.Muted, input.InnerWidth, Ui.P(0, 6, 0, 0)));
        }
        InputToggle("Bloccando questo PC blocca anche gli altri", "groupLock", status?.GroupLock ?? true,
            "Con Win+L o Start › Blocca si bloccano tutti i PC collegati; lo sblocco resta separato. " +
            "Il blocco automatico per inattività non blocca gli altri PC.", first: true);
        InputToggle("Tasti multimediali al PC che suona", "mediaKeys", status?.MediaKeysFollowAudio ?? true,
            "Volume e play/pausa vanno al PC da cui esce la musica, anche se in quel momento usi un altro PC.");
        InputToggle("Compensa la scala dello schermo", "pointerScale", status?.PointerScaleCompensation ?? false,
            "Quando comandi un altro PC il cursore segue la scala dei monitor (100%, 150%…) invece dei pixel: " +
            "su un monitor 4K non sembra più lento. Serve la stessa versione di Varco sull'altro PC.");
        right.Controls.Add(input);

        // bordi
        var edges = new Card(W, "Bordi dello schermo");
        void EdgeToggle(string text, string option, bool value, bool first = false)
        {
            var t = new Toggle(text) { Checked = value, Margin = first ? Padding.Empty : Ui.P(0, 8, 0, 0) };
            t.CheckedChanged += async (_, _) => await Send(new ControlRequest { Cmd = "setEdgeOption", Option = option, Enabled = t.Checked });
            edges.Body.Controls.Add(t);
        }
        EdgeToggle("Negli angoli dello schermo non passare all'altro PC", "corners", status?.EdgeCorners ?? true, first: true);
        EdgeToggle("Bloc Scorr tiene il cursore su questo PC", "scrollLock", status?.EdgeScrollLock ?? true);
        EdgeToggle("Non passare mentre un'app è a schermo intero", "fullscreen", status?.EdgeBlockFullscreen ?? true);
        EdgeToggle("Passa solo dopo una breve sosta sul bordo", "delay", status?.EdgeDelay ?? false);
        edges.Body.Controls.Add(Ui.Label("Così la X di una finestra massimizzata o le schede del browser non ti portano sull'altro PC per sbaglio. " +
            "Con Bloc Scorr acceso il cursore resta dove si trova, su qualunque PC. " +
            "Lo schermo intero (giochi, presentazioni) conta solo sul PC con il mouse che stai usando. " +
            "Con la sosta il cursore deve restare contro il bordo per un attimo prima di passare. " +
            "Ogni PC usa le proprie impostazioni per i propri bordi.", Ui.Small, Ui.Muted, edges.InnerWidth, Ui.P(0, 8, 0, 0)));
        right.Controls.Add(edges);

        // audio fra i PC
        _audio.Body.Controls.Add(_audioBody);
        _audio.Body.Controls.Add(Ui.Label("Ogni PC compare nel Mixer volume di Windows come \"Audio NOME-PC\", con il suo volume. " +
            "Le sue casse restano mute e i tasti del volume regolano il PC centrale. Il microfono non passa. " +
            "Vale per tutti i PC collegati; serve la stessa versione di Varco su tutti.",
            Ui.Small, Ui.Muted, _audio.InnerWidth, Ui.P(0, 8, 0, 0)));
        ShowAudio(status);
        right.Controls.Add(_audio);

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
        var diagnostics = new VButton("Diagnostica");
        diagnostics.Click += (_, _) =>
        {
            using var form = new DiagnosticsForm(_status);
            form.ShowDialog(this);
        };
        reconnect.Margin = Ui.P(0, 0, 8, 0);
        diagnostics.Margin = Ui.P(0, 0, 8, 0);
        problems.Body.Controls.Add(Row(reconnect, diagnostics, logs));
        problems.Body.Controls.Add(Ui.Link("Scollega questo PC da tutti gli altri…", async () =>
        {
            if (MessageBox.Show(this, "Questo PC si scollegherà da tutti gli altri. Potrai ricollegarlo quando vuoi. Continuare?",
                    "Varco", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                await Send(new ControlRequest { Cmd = "leaveGroup" });
        }, Ui.P(0, 12, 0, 0)));
        left.Controls.Add(problems);

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
        _installNow.Margin = Ui.P(0, 10, 0, 0);
        _installNow.Visible = false;
        _installNow.Click += async (_, _) => await MainForm.InstallNowEverywhere(this, _status, async r =>
        {
            if (await Send(r) is not { } resp) return false;
            ShowUpdate(resp.Status);
            return true;
        });
        updates.Body.Controls.Add(_installNow);
        right.Controls.Add(updates);
        ShowUpdate(status);

        // Lo stato dell'aggiornamento cambia mentre la finestra è aperta (controllo, download…).
        var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += async (_, _) =>
        {
            try
            {
                var s = (await ControlClient.SendAsync(new ControlRequest { Cmd = "status" })).Status;
                ShowUpdate(s);
                ShowAudio(s);
            }
            catch { }
        };
        timer.Start();
        FormClosed += (_, _) => timer.Dispose();
    }

    /// <summary>
    /// Riquadro dell'audio: sul PC centrale l'elenco dei PC da ascoltare, sugli altri l'invio del proprio audio.
    /// Si ridisegna solo quando cambia qualcosa, per non perdere un clic a metà.
    /// </summary>
    private void ShowAudio(StatusDto? s)
    {
        if (IsDisposed || s is null) return;
        var me = s.MachineId;
        var others = s.Peers.Where(p => p.Id is Guid id && id != me).Select(p => (Id: p.Id!.Value, p.Name)).ToList();
        var shown = string.Join("|", s.AudioHub, string.Join(",", s.AudioSources), s.AudioSending,
            string.Join(",", s.AudioReceiving), string.Join(",", others.Select(o => $"{o.Id}={o.Name}")));
        if (shown == _audioShown) return;
        _audioShown = shown;

        _audioBody.SuspendLayout();
        foreach (Control c in _audioBody.Controls.Cast<Control>().ToList()) c.Dispose();
        _audioBody.Controls.Clear();
        string NameOf(Guid id) => id == me ? s.MachineName : others.FirstOrDefault(o => o.Id == id).Name ?? "un PC non più collegato";
        void Help(string text) => _audioBody.Controls.Add(Ui.Label(text, Ui.Small, Ui.Muted, _audio.InnerWidth, Ui.P(0, 6, 0, 0)));

        if (others.Count == 0)
        {
            Help("Collega almeno un altro PC per ascoltarne l'audio da qui.");
        }
        else
        {
            var hubHere = s.AudioHub == me;
            var hubElsewhere = s.AudioHub != Guid.Empty && !hubHere;
            var here = new Toggle("Ascolta qui l'audio degli altri PC") { Checked = hubHere };
            // Diventando centrale si ascoltano subito tutti gli altri PC: poi si tolgono quelli che non servono.
            here.CheckedChanged += (_, _) => SendAudio(here.Checked ? me : null, here.Checked ? others.Select(o => o.Id) : []);
            _audioBody.Controls.Add(here);
            if (hubElsewhere)
                Help($"Ora il PC centrale è {NameOf(s.AudioHub)}. Attivando qui, il centrale diventa questo PC.");

            if (hubHere)
            {
                foreach (var (id, name) in others)
                {
                    var on = s.AudioSources.Contains(id);
                    var t = new Toggle($"Audio di {name}") { Checked = on, Margin = Ui.P(0, 10, 0, 0) };
                    t.CheckedChanged += (_, _) =>
                    {
                        var next = s.AudioSources.Where(x => x != id).ToList();
                        if (t.Checked) next.Add(id);
                        SendAudio(me, next);
                    };
                    _audioBody.Controls.Add(t);
                }
                var receiving = s.AudioReceiving.Select(NameOf).ToList();
                Help(receiving.Count > 0 ? $"Arriva ora l'audio di {string.Join(", ", receiving)}." : "In questo momento non arriva audio dagli altri PC.");
            }
            else if (hubElsewhere)
            {
                var hub = NameOf(s.AudioHub);
                var send = new Toggle($"Manda l'audio di questo PC a {hub}") { Checked = s.AudioSources.Contains(me), Margin = Ui.P(0, 10, 0, 0) };
                send.CheckedChanged += (_, _) =>
                {
                    var next = s.AudioSources.Where(x => x != me).ToList();
                    if (send.Checked) next.Add(me);
                    SendAudio(s.AudioHub, next);
                };
                _audioBody.Controls.Add(send);
                if (s.AudioSending) Help($"L'audio di questo PC ora esce da {hub}: le casse di questo PC sono mute.");
                else if (s.AudioSources.Contains(me)) Help($"L'audio passerà a {hub} appena i due PC sono collegati.");
            }
        }
        _audioBody.ResumeLayout();
        _audio.PerformLayout();
    }

    private void SendAudio(Guid? hub, IEnumerable<Guid> sources)
    {
        var req = new ControlRequest { Cmd = "setAudio", Id = hub, Sources = sources.ToList() };
        // Dopo il clic: il riquadro si ridisegna con lo stato nuovo, fuori dall'evento del pulsante che verrà eliminato.
        BeginInvoke(async () =>
        {
            var r = await Send(req);
            _audioShown = "";
            ShowAudio(r?.Status ?? _status);
        });
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
        _status = s;
        _updateText.Text = MainForm.UpdateText(s);
        _updateText.Parent?.PerformLayout();
        _installNow.Visible = MainForm.CanInstallNow(s);
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
