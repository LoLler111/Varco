using Varco.Core.Control;

namespace Varco.App;

/// <summary>Finestra principale: i tuoi PC, i PC trovati nella rete (con "Collega") e la disposizione.</summary>
internal sealed class MainForm : Form
{
    private static readonly Color Muted = Color.FromArgb(0x5F, 0x5E, 0x5A);
    private static readonly Color Ok = Color.FromArgb(0x0F, 0x6E, 0x56);
    private static readonly Color Bad = Color.FromArgb(0xA3, 0x2D, 0x2D);
    private const int ContentWidth = 440;

    private readonly Label _subtitle = new() { AutoSize = true, ForeColor = Muted, MaximumSize = new Size(ContentWidth - 70, 0) };
    private readonly LayoutCanvas _strip = new() { Width = ContentWidth, Margin = new Padding(0, 4, 0, 0) };
    private readonly Label _stripHint = Hint("Trascina i PC come sono messi sulla tua scrivania, anche più in alto o più in basso: il mouse passa dove gli schermi si toccano, alla stessa altezza.");
    private readonly FlowLayoutPanel _peers = Column();
    private readonly FlowLayoutPanel _found = Column();
    private readonly Panel _pairBox = new() { Width = ContentWidth, Height = 0, Visible = false, Margin = new Padding(0, 8, 0, 0) };
    private readonly Label _pairText = new() { AutoSize = true, MaximumSize = new Size(ContentWidth - 120, 0), Location = new Point(12, 12) };
    private readonly Label _pairCode = new() { AutoSize = true, Location = new Point(12, 36) };
    private readonly Button _pairCancel = new() { Text = "Annulla", AutoSize = true };

    private string? _peersSig, _foundSig;
    private StatusDto? _last;

    public MainForm()
    {
        Text = "Varco";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(18, 14, 18, 14);
        Font = SystemFonts.MessageBoxFont ?? Font;

        var root = Column();
        Controls.Add(root);

        // intestazione
        var header = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0, 0, 0, 6) };
        header.Controls.Add(new PictureBox { Image = VarcoIcon.RenderApp(48), Size = new Size(48, 48), Margin = new Padding(0, 0, 12, 0) });
        var titles = Column();
        titles.Controls.Add(new Label { Text = "Varco", AutoSize = true, Font = new Font(Font.FontFamily, 15f, FontStyle.Bold) });
        titles.Controls.Add(_subtitle);
        header.Controls.Add(titles);
        root.Controls.Add(header);

        root.Controls.Add(Section("I tuoi PC"));
        root.Controls.Add(_strip);
        root.Controls.Add(_stripHint);
        root.Controls.Add(_peers);

        root.Controls.Add(Section("PC trovati nella rete"));
        root.Controls.Add(_found);
        root.Controls.Add(_pairBox);

        var advanced = new LinkLabel { Text = "Avanzate…", AutoSize = true, Margin = new Padding(0, 14, 0, 0) };
        advanced.LinkClicked += (_, _) => { using var f = new AdvancedForm(_last); f.ShowDialog(this); };
        root.Controls.Add(advanced);

        _pairCode.Font = new Font(FontFamily.GenericMonospace, 22f, FontStyle.Bold);
        _pairCode.ForeColor = VarcoIcon.Brand;
        _pairBox.BackColor = Color.FromArgb(0xEE, 0xED, 0xFE);
        _pairBox.Controls.AddRange([_pairText, _pairCode, _pairCancel]);
        _pairCancel.Click += async (_, _) => await Send(new ControlRequest { Cmd = "pairCancel" });

        _strip.PositionsChanged += async moved => await Send(new ControlRequest
        {
            Cmd = "setLayout",
            Positions = moved.Select(m => new LayoutPositionDto { Id = m.Id, X = m.X, Y = m.Y }).ToList(),
        });
    }

    public void UpdateStatus(StatusDto? s)
    {
        if (IsDisposed) return;
        _last = s;
        if (s is null)
        {
            _subtitle.Text = "Il servizio Varco non risponde. Prova a riavviare il PC o a reinstallare Varco.";
            _subtitle.ForeColor = Bad;
            return;
        }

        var connected = s.Peers.Where(p => p.State == "Connected").ToList();
        _subtitle.ForeColor = Muted;
        _subtitle.Text = s.Mode switch
        {
            "Controlling" => $"Stai usando mouse e tastiera su {s.ActivePeerName}. Ctrl+Alt+F12 per tornare qui.",
            "Controlled" => $"{s.ActivePeerName} sta usando questo PC.",
            _ when connected.Count > 0 => $"{s.MachineName} è collegato a {string.Join(", ", connected.Select(p => p.Name))}. Porta il mouse oltre il bordo dello schermo.",
            _ when s.Peers.Count > 0 => $"{s.MachineName}: in attesa che gli altri PC si colleghino…",
            _ => $"{s.MachineName} non è ancora collegato a nessun PC.",
        };

        // disposizione
        var online = connected.Select(p => p.Id).ToHashSet();
        var items = s.Layout.OrderBy(e => e.Y).ThenBy(e => e.X)
            .Select(e => new LayoutCanvas.Item(e.Id, e.Name, e.Id == s.MachineId, e.Id == s.MachineId || online.Contains(e.Id),
                e.X, e.Y, e.Width, e.Height))
            .ToList();
        var showStrip = items.Count > 1;
        _strip.Visible = _stripHint.Visible = showStrip;
        if (showStrip) _strip.SetItems(items);

        // i miei PC
        var peersSig = string.Join("|", s.Peers.Select(p => $"{p.Id}{p.State}{p.Name}{Math.Round(p.RttMs)}"));
        if (peersSig != _peersSig)
        {
            _peersSig = peersSig;
            Rebuild(_peers, s.Peers.Count == 0
                ? [Hint("Nessuno, per ora. I PC che colleghi compariranno qui.")]
                : s.Peers.OrderBy(p => p.Name).Select(PeerRow).ToArray());
        }

        // PC trovati
        var foundSig = s.Peers.Count + "#" + string.Join("|", s.Discovered.Select(d => d.Id + d.Name));
        if (foundSig != _foundSig)
        {
            _foundSig = foundSig;
            var empty = s.Peers.Count == 0
                ? "Nessun altro PC trovato. Installa Varco anche sull'altro PC e tienilo acceso sulla stessa rete: comparirà qui da solo."
                : "Nessun altro PC da collegare. Per aggiungerne uno, installa Varco anche lì: comparirà qui.";
            Rebuild(_found, s.Discovered.Count == 0 ? [Hint(empty)] : s.Discovered.Select(FoundRow).ToArray());
        }

        UpdatePairBox(s.OutgoingPair);
    }

    private Control PeerRow(PeerDto p)
    {
        var row = Row();
        var (dot, color, text) = p.State switch
        {
            "Connected" => ("●", Ok, p.RttMs >= 0 ? $"collegato · {p.RttMs:0} ms" : "collegato"),
            "Connecting" => ("◌", Muted, "collegamento in corso…"),
            _ => ("○", Muted, "spento o non raggiungibile"),
        };
        row.Controls.Add(new Label { Text = dot, AutoSize = true, ForeColor = color, Margin = new Padding(0, 3, 4, 0) });
        row.Controls.Add(new Label { Text = p.Name, AutoSize = true, Font = new Font(Font, FontStyle.Bold), Margin = new Padding(0, 3, 6, 0) });
        row.Controls.Add(new Label { Text = text, AutoSize = true, ForeColor = Muted, Margin = new Padding(0, 3, 6, 0) });
        if (p.State != "Connected" && p.Id is Guid id)
        {
            var forget = new LinkLabel { Text = "Rimuovi", AutoSize = true, Margin = new Padding(6, 3, 0, 0) };
            forget.LinkClicked += async (_, _) =>
            {
                if (MessageBox.Show(this, $"Rimuovere {p.Name} dai tuoi PC?\nPotrai ricollegarlo in qualsiasi momento.", "Varco",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    await Send(new ControlRequest { Cmd = "forget", Id = id });
            };
            row.Controls.Add(forget);
        }
        return row;
    }

    private Control FoundRow(DiscoveredDto d)
    {
        var row = Row();
        var label = new Label { Text = d.Name, AutoSize = false, Width = ContentWidth - 110, Height = 28, TextAlign = ContentAlignment.MiddleLeft, Font = new Font(Font, FontStyle.Bold) };
        var btn = new Button { Text = "Collega", Width = 96, Height = 28 };
        btn.Click += async (_, _) => await Send(new ControlRequest { Cmd = "pair", Id = d.Id });
        row.Controls.Add(label);
        row.Controls.Add(btn);
        return row;
    }

    private void UpdatePairBox(PairProgressDto? o)
    {
        if (o is null)
        {
            _pairBox.Visible = false;
            return;
        }
        _pairBox.Visible = true;
        _pairCode.Visible = o.State == "WaitingConfirm" && o.Code is not null;
        _pairCancel.Visible = o.State is "Connecting" or "WaitingConfirm";
        _pairText.ForeColor = o.State switch { "Done" => Ok, "Rejected" or "Failed" => Bad, _ => Color.FromArgb(0x3C, 0x34, 0x89) };
        _pairText.Text = o.State switch
        {
            "Connecting" => $"Collegamento a {o.PeerName}…",
            "WaitingConfirm" => $"Ora vai su {o.PeerName} e premi «Accetta».\nDeve comparire questo codice:",
            _ => o.Message ?? "",
        };
        _pairCode.Text = o.Code ?? "";
        _pairBox.Height = _pairCode.Visible ? 84 : _pairText.PreferredHeight + 24;
        _pairCancel.Location = new Point(ContentWidth - _pairCancel.PreferredSize.Width - 12, 12);
    }

    private async Task Send(ControlRequest req)
    {
        try
        {
            var r = await ControlClient.SendAsync(req, 5000);
            if (!r.Ok) MessageBox.Show(this, r.Error ?? "Errore sconosciuto", "Varco", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            else if (r.Status is not null) UpdateStatus(r.Status);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Il servizio Varco non risponde: {ex.Message}", "Varco", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static void Rebuild(FlowLayoutPanel panel, Control[] controls)
    {
        panel.SuspendLayout();
        foreach (Control c in panel.Controls) c.Dispose();
        panel.Controls.Clear();
        panel.Controls.AddRange(controls);
        panel.ResumeLayout();
    }

    private static FlowLayoutPanel Column() => new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false, Margin = new Padding(0) };
    private static FlowLayoutPanel Row() => new() { AutoSize = true, WrapContents = false, Margin = new Padding(0, 2, 0, 2) };

    private Label Section(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold),
        Margin = new Padding(0, 14, 0, 4),
    };

    private static Label Hint(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(ContentWidth, 0),
        ForeColor = Muted,
        Margin = new Padding(0, 2, 0, 2),
    };
}
