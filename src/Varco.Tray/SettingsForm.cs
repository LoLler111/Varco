using Varco.Core.Control;

namespace Varco.Tray;

internal sealed class SettingsForm : Form
{
    private readonly TextBox _key = new() { ReadOnly = true, Width = 360, Font = new Font(FontFamily.GenericMonospace, 10f) };
    private readonly TextBox _joinKey = new() { Width = 360, Font = new Font(FontFamily.GenericMonospace, 10f), PlaceholderText = "XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX-XXXX" };
    private readonly ListBox _order = new() { Width = 360, Height = 110, IntegralHeight = false };
    private readonly ListBox _addresses = new() { Width = 360, Height = 70, IntegralHeight = false };
    private readonly TextBox _newAddress = new() { Width = 240, PlaceholderText = "192.168.1.20 oppure nomepc" };
    private readonly Label _status = new() { AutoSize = true, MaximumSize = new Size(520, 0) };
    private List<Entry> _layout = [];
    private bool _orderDirty;

    private sealed record Entry(Guid Id, string Name, bool Connected)
    {
        public override string ToString() => Connected ? Name : $"{Name} (non connesso)";
    }

    public SettingsForm()
    {
        Text = "Varco — impostazioni";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        StartPosition = FormStartPosition.CenterScreen;
        Padding = new Padding(12);

        var root = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, WrapContents = false };
        Controls.Add(root);

        root.Controls.Add(Header("Gruppo"));
        root.Controls.Add(Hint("Tutti i PC con la stessa chiave si collegano tra loro. Copia questa chiave sugli altri PC."));
        root.Controls.Add(Row(_key,
            MakeButton("Copia", (_, _) => { if (_key.Text.Length > 0) Clipboard.SetText(_key.Text); }),
            MakeButton("Nuovo gruppo", async (_, _) => await NewGroup())));
        root.Controls.Add(Hint("Oppure incolla la chiave di un PC già configurato:"));
        root.Controls.Add(Row(_joinKey, MakeButton("Unisciti", async (_, _) => await Join())));

        root.Controls.Add(Header("Disposizione degli schermi (da sinistra a destra)"));
        root.Controls.Add(Row(_order,
            MakeButton("◀ Sinistra", (_, _) => MoveSelected(-1)),
            MakeButton("Destra ▶", (_, _) => MoveSelected(+1)),
            MakeButton("Applica", async (_, _) => await ApplyOrder())));

        root.Controls.Add(Header("Indirizzi manuali (solo se un PC non viene trovato da solo)"));
        root.Controls.Add(Row(_addresses, MakeButton("Rimuovi", async (_, _) => await RemoveAddress())));
        root.Controls.Add(Row(_newAddress, MakeButton("Aggiungi", async (_, _) => await AddAddress())));

        root.Controls.Add(Header("Stato"));
        root.Controls.Add(_status);

        Load += async (_, _) => await LoadKey();
    }

    public void UpdateStatus(StatusDto? s)
    {
        if (IsDisposed) return;
        if (s is null)
        {
            _status.Text = "Il servizio Varco non risponde. Verifica che sia installato e avviato (services.msc → Varco).";
            return;
        }
        var lines = new List<string> { $"Questo PC: {s.MachineName} — porta {s.ListenPort}" };
        lines.AddRange(s.Peers.Select(p => p.State switch
        {
            "Connected" => $"• {p.Name}: connesso via {p.Address} ({p.RttMs:0.#} ms)",
            "Connecting" => $"• {p.Name}: connessione in corso…",
            _ => $"• {p.Name}: non raggiungibile{(p.LastError is null ? "" : $" — {p.LastError}")}",
        }));
        if (s.Mode == "Controlling") lines.Add($"Stai comandando {s.ActivePeerName}  (Ctrl+Alt+F12 per tornare qui)");
        if (s.Mode == "Controlled") lines.Add($"Comandato da {s.ActivePeerName}");
        _status.Text = string.Join(Environment.NewLine, lines);

        if (!_orderDirty)
        {
            var connected = s.Peers.Where(p => p.State == "Connected" && p.Id is not null).Select(p => p.Id!.Value).ToHashSet();
            connected.Add(s.MachineId);
            var entries = s.Layout.OrderBy(e => e.Y).ThenBy(e => e.X)
                .Select(e => new Entry(e.Id, e.Id == s.MachineId ? $"{e.Name} (questo PC)" : e.Name, connected.Contains(e.Id)))
                .ToList();
            if (!entries.SequenceEqual(_layout))
            {
                _layout = entries;
                var sel = _order.SelectedIndex;
                _order.Items.Clear();
                _order.Items.AddRange(entries.ToArray());
                if (sel >= 0 && sel < _order.Items.Count) _order.SelectedIndex = sel;
            }
        }

        var addrs = s.ManualAddresses;
        if (!addrs.SequenceEqual(_addresses.Items.Cast<string>()))
        {
            _addresses.Items.Clear();
            _addresses.Items.AddRange(addrs.ToArray());
        }
    }

    private async Task LoadKey()
    {
        var r = await Send(new ControlRequest { Cmd = "getKey" });
        if (r is not null) _key.Text = r.Key ?? "";
    }

    private async Task NewGroup()
    {
        if (_key.Text.Length > 0 && MessageBox.Show(this,
                "Creando un nuovo gruppo questo PC si scollega dagli altri finché non inserisci la nuova chiave anche su di loro. Continuare?",
                "Varco", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        var r = await Send(new ControlRequest { Cmd = "newGroup" });
        if (r is not null) _key.Text = r.Key ?? "";
    }

    private async Task Join()
    {
        var r = await Send(new ControlRequest { Cmd = "setKey", Key = _joinKey.Text });
        if (r is null) return;
        _key.Text = r.Key ?? "";
        _joinKey.Clear();
        MessageBox.Show(this, "Fatto: questo PC ora fa parte del gruppo e cercherà gli altri automaticamente.", "Varco");
    }

    private void MoveSelected(int delta)
    {
        var i = _order.SelectedIndex;
        var j = i + delta;
        if (i < 0 || j < 0 || j >= _order.Items.Count) return;
        var item = _order.Items[i];
        _order.Items.RemoveAt(i);
        _order.Items.Insert(j, item);
        _order.SelectedIndex = j;
        _orderDirty = true;
    }

    private async Task ApplyOrder()
    {
        var ids = _order.Items.Cast<Entry>().Select(e => e.Id).ToList();
        if (await Send(new ControlRequest { Cmd = "setRow", Order = ids }) is not null) _orderDirty = false;
    }

    private async Task AddAddress()
    {
        if (string.IsNullOrWhiteSpace(_newAddress.Text)) return;
        if (await Send(new ControlRequest { Cmd = "addAddress", Address = _newAddress.Text.Trim() }) is not null) _newAddress.Clear();
    }

    private async Task RemoveAddress()
    {
        if (_addresses.SelectedItem is string a) await Send(new ControlRequest { Cmd = "removeAddress", Address = a });
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

    private static Label Header(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Font = new Font(SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
        Margin = new Padding(0, 12, 0, 4),
    };

    private static Label Hint(string text) => new() { Text = text, AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = SystemColors.GrayText };

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true };
        b.Click += onClick;
        return b;
    }

    private static FlowLayoutPanel Row(Control main, params Control[] buttons)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        p.Controls.Add(main);
        var side = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0) };
        foreach (var b in buttons) side.Controls.Add(b);
        p.Controls.Add(side);
        return p;
    }
}
