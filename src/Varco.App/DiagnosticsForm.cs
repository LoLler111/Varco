using System.Diagnostics;
using System.IO.Compression;
using Varco.Core.Control;

namespace Varco.App;

/// <summary>
/// Diagnostica dei collegamenti: per ogni PC latenza, cavo o Wi-Fi, indirizzi in uso, cadute nelle ultime 24 ore e motivo
/// dell'ultima. Si aggiorna ogni secondo. «Esporta log» crea uno zip da allegare a una segnalazione.
/// </summary>
internal sealed class DiagnosticsForm : Form
{
    private static readonly int W = Ui.S(440);

    private readonly FlowLayoutPanel _peers = Ui.Column();
    private readonly Label _summary;
    private readonly Dictionary<string, (Label Title, Label Body)> _rows = [];
    private readonly VButton _export = new("Esporta log…", primary: true);
    private string _machine = "";

    public DiagnosticsForm(StatusDto? status)
    {
        Ui.StyleForm(this);
        Text = "Varco — diagnostica";
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

        var card = new Card(W, "Collegamenti");
        _summary = Ui.Label("", Ui.Small, Ui.Muted, card.InnerWidth, Ui.P(0, 0, 0, 4));
        card.Body.Controls.Add(_summary);
        card.Body.Controls.Add(_peers);
        root.Controls.Add(card);

        var logs = new Card(W, "Log");
        logs.Body.Controls.Add(Ui.Label("Crea uno zip con i log di Varco di questo PC, lo stato dei collegamenti e la configurazione " +
            "senza la chiave di gruppo: si può allegare a una segnalazione senza far entrare nessuno nei tuoi PC.",
            Ui.Small, Ui.Muted, logs.InnerWidth, Ui.P(0, 0, 0, 10)));
        _export.Click += async (_, _) => await ExportAsync();
        logs.Body.Controls.Add(_export);
        root.Controls.Add(logs);

        Show(status);
        var timer = new System.Windows.Forms.Timer { Interval = 1000 };
        timer.Tick += async (_, _) =>
        {
            try { Show((await ControlClient.SendAsync(new ControlRequest { Cmd = "status" })).Status); } catch { }
        };
        timer.Start();
        FormClosed += (_, _) => timer.Dispose();
    }

    private void Show(StatusDto? s)
    {
        if (s is null) return;
        _machine = s.MachineName;
        var started = s.AgentStartedUtc.ToLocalTime();
        var since24 = s.AgentStartedUtc > DateTime.UtcNow.AddHours(-24) && s.AgentStartedUtc != default;
        _summary.Text = s.Paused ? "La connessione è disattivata su questo PC."
            : s.Peers.Count == 0 ? "Nessun PC collegato."
            : since24 ? $"Varco su questo PC è partito alle {started:HH:mm}: le cadute si contano da lì."
            : "Le cadute si contano sulle ultime 24 ore.";

        var keys = s.Peers.Select(Key).ToList();
        if (!keys.SequenceEqual(_rows.Keys))
        {
            _peers.SuspendLayout();
            _peers.Controls.Clear();
            _rows.Clear();
            foreach (var p in s.Peers)
            {
                var title = Ui.Label("", Ui.BodyStrong, margin: Ui.P(0, 10, 0, 2));
                var body = Ui.Label("", Ui.Small, Ui.Muted, W - Ui.S(32));
                _peers.Controls.Add(title);
                _peers.Controls.Add(body);
                _rows[Key(p)] = (title, body);
            }
            _peers.ResumeLayout();
        }
        foreach (var p in s.Peers)
        {
            var (title, body) = _rows[Key(p)];
            title.Text = p.Name;
            title.ForeColor = p.State == "Connected" ? Ui.Text : Ui.Muted;
            body.Text = Describe(p, since24);
        }
    }

    private static string Key(PeerDto p) => p.Id?.ToString() ?? p.Name;

    private static string Describe(PeerDto p, bool since24)
    {
        var lines = new List<string>();
        switch (p.State)
        {
            case "Connected":
                var since = p.ConnectedSinceUtc is { } c ? $"collegato da {Duration(DateTime.UtcNow - c)}" : "collegato";
                lines.Add($"{Capitalize(since)} · latenza {(p.RttMs >= 0 ? $"{p.RttMs:0.0} ms" : "?")}");
                var link = p.Link is null ? "Rete" : p.Adapter is { } a && a != p.Link ? $"{p.Link} ({a})" : p.Link;
                lines.Add($"{link}: {p.LocalAddress ?? "?"} → {p.Address ?? "?"}");
                break;
            case "Connecting":
                lines.Add("Collegamento in corso…");
                break;
            case "Paused":
                lines.Add("Connessione disattivata su questo PC");
                break;
            default:
                lines.Add(p.LastError is { Length: > 0 } err ? $"Non collegato: {err}" : "Non collegato");
                break;
        }
        var window = since24 ? "da quando Varco è partito" : "nelle ultime 24 ore";
        var drops = p.Drops24h switch { 0 => $"Nessuna caduta {window}", 1 => $"1 caduta {window}", var n => $"{n} cadute {window}" };
        if (p.LastDropUtc is { } at && p.LastDropReason is { } why)
            drops += $" · l'ultima {When(at)}: {why}";
        lines.Add(drops);
        return string.Join(Environment.NewLine, lines);
    }

    private static string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        return local.Date == DateTime.Today ? $"alle {local:HH:mm}" : $"il {local:d MMM} alle {local:HH:mm}";
    }

    private static string Duration(TimeSpan t) =>
        t.TotalMinutes < 1 ? "meno di un minuto"
        : t.TotalHours < 1 ? $"{(int)t.TotalMinutes} min"
        : t.TotalDays < 1 ? $"{(int)t.TotalHours} h {t.Minutes} min"
        : $"{(int)t.TotalDays} g {t.Hours} h";

    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpper(s[0]) + s[1..];

    private async Task ExportAsync()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "Esporta i log di Varco",
            Filter = "Archivio zip (*.zip)|*.zip",
            FileName = $"Varco-log-{Sanitize(_machine)}-{DateTime.Now:yyyyMMdd-HHmm}.zip",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            OverwritePrompt = true,
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        _export.Enabled = false;
        try
        {
            var r = await ControlClient.SendAsync(new ControlRequest { Cmd = "exportLogs" }, 30_000);
            if (!r.Ok || r.Zip is not { } zip)
            {
                MessageBox.Show(this, r.Error ?? "Il servizio Varco non ha creato lo zip.", "Varco", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var bytes = AddTrayLogs(zip);
            await File.WriteAllBytesAsync(dialog.FileName, bytes);
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{dialog.FileName}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Log non esportati: {ex.Message}", "Varco", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _export.Enabled = true;
        }
    }

    /// <summary>Il log dell'icona sta nella cartella dell'utente: lo aggiunge l'icona stessa.</summary>
    private static byte[] AddTrayLogs(byte[] zip)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Varco", "logs");
        using var ms = new MemoryStream();
        ms.Write(zip);
        using (var archive = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true))
        {
            foreach (var name in new[] { "tray.log", "tray.log.1" })
            {
                var path = Path.Combine(dir, name);
                if (!File.Exists(path)) continue;
                try
                {
                    using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var dst = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
                    src.CopyTo(dst);
                }
                catch (IOException) { }
            }
        }
        return ms.ToArray();
    }

    private static string Sanitize(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(name.Select(c => bad.Contains(c) ? '_' : c).ToArray());
        return clean.Length == 0 ? "PC" : clean;
    }
}
