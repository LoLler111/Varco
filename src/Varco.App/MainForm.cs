using Varco.Core.Control;

namespace Varco.App;

/// <summary>Finestra principale: i tuoi PC, i PC trovati nella rete (con "Collega") e la disposizione.</summary>
internal sealed class MainForm : Form
{
    private static readonly int W = Ui.S(480);

    private readonly StatusChip _chip = new();
    private readonly Label _subtitle = Ui.Label("", color: Ui.Muted, maxWidth: W - Ui.S(72));

    private readonly Card _pcCard = new(W, "I tuoi PC");
    private readonly LayoutCanvas _strip = new();
    private readonly Label _stripHint;
    private readonly FlowLayoutPanel _peers = Ui.Column();

    private readonly Card _foundCard = new(W, "PC trovati nella rete");
    private readonly FlowLayoutPanel _found = Ui.Column();
    private readonly Tint _pairBox = new(Ui.AccentSoft) { Margin = Ui.P(0, 10, 0, 0), Visible = false };
    private readonly Label _pairText;
    private readonly Label _pairCode = Ui.Label("", Ui.Code, Ui.Accent, margin: Ui.P(0, 2, 0, 0));
    private readonly VButton _pairCancel = new("Annulla", minWidth: 80);
    private readonly LineRow _pairCodeRow;

    private readonly FlowLayoutPanel _manualRow = Ui.Flow(Ui.S(6));
    private readonly InputBox _address;
    private readonly Label _myAddress;
    private long? _myAddressAt;

    private readonly Label _version = Ui.Label("", Ui.Small, Ui.Muted);

    private string? _peersSig, _foundSig;
    private StatusDto? _last;

    /// <summary>Testo sullo stato degli aggiornamenti (breve per la finestra principale, completo per Avanzate).</summary>
    internal static string UpdateText(StatusDto? s, bool brief = false)
    {
        if (s is null) return "";
        var v = $"Versione {s.Version}";
        var u = s.Update;
        if (u is null) return brief ? v : $"{v}. Gli aggiornamenti funzionano solo con Varco installato.";
        var state = u.State switch
        {
            "Downloading" => $"scarico la versione {u.Latest}…",
            "Waiting" => $"la {u.Latest} si installerà appena smetti di usare un altro PC",
            "Installing" => $"installo la versione {u.Latest}…",
            "Checking" => brief ? null : "controllo…",
            "Error" => brief ? null : $"ultimo controllo non riuscito ({u.Message})",
            "UpToDate" => brief ? null : "è la più recente",
            _ => brief ? null : !s.AutoUpdate ? "aggiornamenti automatici spenti" : "controllo entro pochi minuti",
        };
        if (!brief && u.LastCheckUtc is { } t && u.State is "UpToDate" or "Error")
            state += $" (controllato alle {t.ToLocalTime():HH:mm})";
        return state is null ? v : $"{v}: {state}";
    }

    public MainForm()
    {
        Ui.StyleForm(this);
        Text = "Varco";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.CenterScreen;
        Padding = Ui.P(20, 18, 20, 16);

        var root = Ui.Column();
        root.Location = new Point(Padding.Left, Padding.Top);
        Controls.Add(root);

        // intestazione: logo, nome, stato in breve e spiegazione
        var header = Ui.Flow();
        header.Controls.Add(new PictureBox { Image = VarcoIcon.RenderApp(Ui.S(56), gradient: true), Size = Ui.S(56, 56), Margin = Ui.P(0, 0, 16, 0) });
        var titles = Ui.Column();
        var titleRow = Ui.Flow();
        titleRow.Controls.Add(Ui.Label("Varco", Ui.Title, margin: Ui.P(0, 0, 10, 0)));
        _chip.Margin = Ui.P(0, 6, 0, 0);
        titleRow.Controls.Add(_chip);
        titles.Controls.Add(titleRow);
        _subtitle.Margin = Ui.P(1, 4, 0, 0);
        titles.Controls.Add(_subtitle);
        header.Controls.Add(titles);
        root.Controls.Add(header);

        // i tuoi PC
        _stripHint = Ui.Label("Trascina i PC come sono messi sulla scrivania: il mouse passa dove gli schermi si toccano.",
            Ui.Small, Ui.Muted, _pcCard.InnerWidth, Ui.P(0, 6, 0, 6));
        _strip.Size = new Size(_pcCard.InnerWidth, Ui.S(180));
        _strip.Margin = Padding.Empty;
        _pcCard.Body.Controls.Add(_strip);
        _pcCard.Body.Controls.Add(_stripHint);
        _pcCard.Body.Controls.Add(_peers);
        root.Controls.Add(_pcCard);

        // PC trovati
        _foundCard.Body.Controls.Add(_found);
        var pairWidth = _foundCard.InnerWidth - _pairBox.Padding.Horizontal;
        _pairText = Ui.Label("", Ui.Body, Ui.AccentText, pairWidth);
        _pairBox.MinimumSize = _pairBox.MaximumSize = new Size(_foundCard.InnerWidth, 0);
        _pairBox.Edge = Color.FromArgb(0x4A, 0x43, 0x8A);
        _pairCode.Text = "000 000";
        _pairCodeRow = new LineRow(pairWidth, Math.Max(_pairCode.PreferredHeight, _pairCancel.Height), [_pairCode], _pairCancel) { Margin = Ui.P(0, 6, 0, 0) };
        var pairInner = Ui.Column();
        pairInner.Controls.Add(_pairText);
        pairInner.Controls.Add(_pairCodeRow);
        pairInner.Location = new Point(_pairBox.Padding.Left, _pairBox.Padding.Top);
        _pairBox.Controls.Add(pairInner);
        _pairCancel.Click += async (_, _) => await Send(new ControlRequest { Cmd = "pairCancel" });
        _foundCard.Body.Controls.Add(_pairBox);

        var sep = new Panel { Height = 1, Width = _foundCard.InnerWidth, BackColor = Ui.Border, Margin = Ui.P(0, 12, 0, 10) };
        _foundCard.Body.Controls.Add(sep);
        _foundCard.Body.Controls.Add(Ui.Link("Non vedi l'altro PC? Collegalo con il suo indirizzo IP", ToggleManual));
        var connect = new VButton("Collega", primary: true, minWidth: 96);
        _address = new InputBox(_foundCard.InnerWidth - connect.Width - Ui.S(8), connect.Height, "es. 192.168.1.20") { Margin = Ui.P(0, 0, 8, 0) };
        connect.Click += async (_, _) => await PairByAddress();
        _address.Box.KeyDown += async (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            await PairByAddress();
        };
        _manualRow.Controls.Add(_address);
        _manualRow.Controls.Add(connect);
        _manualRow.Visible = false;
        _foundCard.Body.Controls.Add(_manualRow);
        _myAddress = Ui.Label("", Ui.Small, Ui.Muted, _foundCard.InnerWidth, Ui.P(0, 6, 0, 0));
        _foundCard.Body.Controls.Add(_myAddress);
        root.Controls.Add(_foundCard);

        // piè di pagina
        var footer = new LineRow(W, Ui.S(28), [Ui.Link("Avanzate…", () => { using var f = new AdvancedForm(_last); f.ShowDialog(this); })], _version)
        {
            Margin = Ui.P(0, 10, 0, 0),
        };
        root.Controls.Add(footer);

        _strip.PositionsChanged += async moved => await Send(new ControlRequest
        {
            Cmd = "setLayout",
            Positions = moved.Select(m => new LayoutPositionDto { Id = m.Id, X = m.X, Y = m.Y }).ToList(),
        });
    }

    private void ToggleManual()
    {
        _manualRow.Visible = !_manualRow.Visible;
        if (_manualRow.Visible) _address.Box.Focus();
    }

    private async Task PairByAddress()
    {
        var a = _address.Box.Text.Trim();
        if (a.Length == 0) { _address.Box.Focus(); return; }
        // A collegamento riuscito il servizio ricorda da solo l'indirizzo.
        if (await Send(new ControlRequest { Cmd = "pair", Address = a }))
        {
            _address.Box.Clear();
            _manualRow.Visible = false;
        }
    }

    public void UpdateStatus(StatusDto? s)
    {
        if (IsDisposed) return;
        _last = s;
        RefreshMyAddress();
        if (s is null)
        {
            _chip.Set("Servizio spento", Ui.Bad, Ui.BadSoft);
            _subtitle.Text = "Il servizio Varco non risponde. Prova a riavviare il PC o a reinstallare Varco.";
            return;
        }

        _version.Text = UpdateText(s, brief: true);

        var connected = s.Peers.Where(p => p.State == "Connected").ToList();
        switch (s.Mode)
        {
            case "Controlling":
                _chip.Set($"Stai usando {s.ActivePeerName}", Ui.Accent, Ui.AccentSoft);
                _subtitle.Text = "Mouse e tastiera di questo PC comandano l'altro. Ctrl+Alt+F12 per tornare qui.";
                break;
            case "Controlled":
                _chip.Set($"Comandato da {s.ActivePeerName}", Ui.Accent, Ui.AccentSoft);
                _subtitle.Text = $"{s.ActivePeerName} sta usando questo PC. Tocca il mouse per riprenderlo.";
                break;
            default:
                if (connected.Count > 0)
                {
                    _chip.Set("Collegato", Ui.Ok, Ui.OkSoft);
                    _subtitle.Text = $"{s.MachineName} è collegato a {string.Join(", ", connected.Select(p => p.Name))}. Porta il mouse oltre il bordo dello schermo.";
                }
                else if (s.Peers.Count > 0)
                {
                    _chip.Set("In attesa", Ui.Idle, Ui.IdleSoft);
                    _subtitle.Text = $"{s.MachineName} aspetta che gli altri PC si accendano o tornino in rete.";
                }
                else
                {
                    _chip.Set("Non collegato", Ui.Idle, Ui.IdleSoft);
                    _subtitle.Text = $"{s.MachineName} non è ancora collegato a nessun PC.";
                }
                break;
        }

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
                ? [Ui.Label("Nessuno, per ora. I PC che colleghi compariranno qui.", color: Ui.Muted, maxWidth: _pcCard.InnerWidth)]
                : s.Peers.OrderBy(p => p.Name).Select(PeerRow).ToArray());
        }

        // PC trovati
        var foundSig = s.Peers.Count + "#" + string.Join("|", s.Discovered.Select(d => d.Id + d.Name + d.Address));
        if (foundSig != _foundSig)
        {
            _foundSig = foundSig;
            var empty = s.Peers.Count == 0
                ? "Nessun altro PC trovato per ora. Installa Varco anche sull'altro PC e tienilo acceso: comparirà qui da solo, anche se uno è via cavo e l'altro in Wi-Fi."
                : "Nessun altro PC da collegare. Per aggiungerne uno, installa Varco anche lì: comparirà qui.";
            Rebuild(_found, s.Discovered.Count == 0
                ? [Ui.Label(empty, color: Ui.Muted, maxWidth: _foundCard.InnerWidth)]
                : s.Discovered.Select(FoundRow).ToArray());
        }

        UpdatePairBox(s.OutgoingPair);
    }

    private void RefreshMyAddress()
    {
        var now = Environment.TickCount64;
        if (_myAddressAt is { } at && now - at < 5000) return;
        _myAddressAt = now;
        var ips = Ui.LocalAddresses();
        _myAddress.Text = ips.Count == 0
            ? "Questo PC non sembra collegato a una rete."
            : $"Indirizzo di questo PC: {string.Join(", ", ips)}";
    }

    private Control PeerRow(PeerDto p)
    {
        var (color, hollow, text) = p.State switch
        {
            "Connected" => (Ui.Ok, false, p.RttMs >= 0 ? $"collegato · {p.RttMs:0} ms" : "collegato"),
            "Connecting" => (Ui.Idle, true, "collegamento in corso…"),
            _ => (Ui.Idle, true, "spento o non raggiungibile"),
        };
        var name = Ui.Label(p.Name, Ui.BodyStrong, margin: Ui.P(10, 0, 10, 0));
        var detail = Ui.Label(text, color: Ui.Muted);
        Control? right = null;
        if (p.State != "Connected" && p.Id is Guid id)
        {
            right = Ui.Link("Rimuovi", async () =>
            {
                if (MessageBox.Show(this, $"Rimuovere {p.Name} dai tuoi PC?\nPotrai ricollegarlo in qualsiasi momento.", "Varco",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    await Send(new ControlRequest { Cmd = "forget", Id = id });
            });
        }
        return new LineRow(_pcCard.InnerWidth, Ui.S(30), [new Dot(color, hollow), name, detail], right, stretch: detail);
    }

    private Control FoundRow(DiscoveredDto d)
    {
        var btn = new VButton("Collega", primary: true, minWidth: 96);
        btn.Click += async (_, _) => await Send(new ControlRequest { Cmd = "pair", Id = d.Id });
        var texts = Ui.Column();
        texts.Controls.Add(Ui.Label(d.Name, Ui.BodyStrong));
        if (!string.IsNullOrEmpty(d.Address)) texts.Controls.Add(Ui.Label(d.Address, Ui.Small, Ui.Muted));
        texts.Margin = Ui.P(12, 0, 0, 0);
        return new LineRow(_foundCard.InnerWidth, Ui.S(44), [new PcBadge(), texts], btn);
    }

    private void UpdatePairBox(PairProgressDto? o)
    {
        if (o is null)
        {
            _pairBox.Visible = false;
            return;
        }
        _pairBox.Visible = true;
        var showCode = o.State == "WaitingConfirm" && o.Code is not null;
        var showCancel = o.State is "Connecting" or "WaitingConfirm";
        _pairCode.Visible = showCode;
        _pairCancel.Visible = showCancel;
        var (fg, bg) = o.State switch
        {
            "Done" => (Ui.Ok, Ui.OkSoft),
            "Rejected" or "Failed" => (Ui.Bad, Ui.BadSoft),
            _ => (Ui.AccentText, Ui.AccentSoft),
        };
        _pairText.ForeColor = fg;
        if (_pairBox.Fill != bg) _pairBox.Fill = bg;
        _pairBox.Edge = Color.FromArgb(80, fg);
        _pairCodeRow.Visible = showCode || showCancel;
        _pairText.Text = o.State switch
        {
            "Connecting" => $"Collegamento a {o.PeerName}…",
            "WaitingConfirm" => $"Ora vai su {o.PeerName} e premi «Accetta». Deve comparire questo codice:",
            _ => o.Message ?? "",
        };
        _pairCode.Text = o.Code ?? "";
    }

    private async Task<bool> Send(ControlRequest req)
    {
        try
        {
            var r = await ControlClient.SendAsync(req, 5000);
            if (!r.Ok)
            {
                MessageBox.Show(this, r.Error ?? "Errore sconosciuto", "Varco", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (r.Status is not null) UpdateStatus(r.Status);
            return true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Il servizio Varco non risponde: {ex.Message}", "Varco", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
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
}
