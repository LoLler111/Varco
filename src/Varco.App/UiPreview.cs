#if DEBUG
using Varco.Core.Control;
using Varco.Core.Input;

namespace Varco.App;

/// <summary>
/// Solo nelle build di debug: <c>Varco.exe --ui-preview cartella</c> salva le finestre, con dati di esempio, come PNG.
/// Serve a controllare la grafica senza servizio né altri PC.
/// </summary>
internal static class UiPreview
{
    public static int Run(string dir)
    {
        Directory.CreateDirectory(dir);
        var me = Guid.NewGuid();
        var lap = Guid.NewGuid();
        var off = Guid.NewGuid();

        var empty = new StatusDto { MachineId = me, MachineName = "DESKTOP-CASA", Version = "0.4.0", Update = new UpdateStatusDto { State = "UpToDate" } };
        var found = new StatusDto
        {
            MachineId = me, MachineName = "DESKTOP-CASA", Version = "0.4.0", Update = new UpdateStatusDto { State = "UpToDate" },
            Discovered = [new DiscoveredDto { Id = lap, Name = "PORTATILE", Address = "192.168.1.34" }],
            OutgoingPair = new PairProgressDto { PeerName = "PORTATILE", Code = "482 913", State = "WaitingConfirm" },
        };
        var linked = new StatusDto
        {
            MachineId = me, MachineName = "DESKTOP-CASA", Version = "0.4.0", Update = new UpdateStatusDto { State = "Waiting", Latest = "0.4.1" },
            Peers =
            [
                new PeerDto { Id = lap, Name = "PORTATILE", State = "Connected", RttMs = 4 },
                new PeerDto { Id = off, Name = "UFFICIO", State = "Disconnected" },
            ],
            Layout =
            [
                new LayoutEntry { Id = me, Name = "DESKTOP-CASA", X = 0, Y = 0, Width = 3440, Height = 2520,
                    Monitors = [new LayoutMonitor(0, 0, 3440, 1440), new LayoutMonitor(760, 1440, 1920, 1080)] },
                new LayoutEntry { Id = lap, Name = "PORTATILE", X = 2680, Y = 1500, Width = 1536, Height = 864 },
                new LayoutEntry { Id = off, Name = "UFFICIO", X = -1920, Y = 0, Width = 1920, Height = 1080 },
            ],
        };

        Save(() => { var f = new MainForm(); f.UpdateStatus(empty); return f; }, Path.Combine(dir, "main-vuota.png"));
        Save(() => { var f = new MainForm(); f.UpdateStatus(found); return f; }, Path.Combine(dir, "main-trovato.png"));
        Save(() => { var f = new MainForm(); f.UpdateStatus(linked); return f; }, Path.Combine(dir, "main-collegati.png"));
        var paused = new StatusDto
        {
            MachineId = me, MachineName = "DESKTOP-CASA", Version = linked.Version, Update = linked.Update, Paused = true,
            Peers = linked.Peers.Select(p => new PeerDto { Id = p.Id, Name = p.Name, State = "Paused" }).ToList(),
            Layout = linked.Layout,
        };
        Save(() => { var f = new MainForm(); f.UpdateStatus(paused); return f; }, Path.Combine(dir, "main-disattivato.png"));
        Save(() => new MainForm(), Path.Combine(dir, "main-servizio-spento.png"), s => ((MainForm)s).UpdateStatus(null));
        Save(() => new AdvancedForm(linked), Path.Combine(dir, "avanzate.png"));
        var hub = new StatusDto
        {
            MachineId = me, MachineName = linked.MachineName, Version = linked.Version, Update = linked.Update, Peers = linked.Peers,
            Layout = linked.Layout, AudioHub = me, AudioSources = [lap], AudioReceiving = [lap],
        };
        Save(() => new AdvancedForm(hub), Path.Combine(dir, "avanzate-audio-centrale.png"));
        var source = new StatusDto
        {
            MachineId = me, MachineName = linked.MachineName, Version = linked.Version, Update = linked.Update, Peers = linked.Peers,
            Layout = linked.Layout, AudioHub = lap, AudioSources = [me], AudioSending = true,
        };
        Save(() => new AdvancedForm(source), Path.Combine(dir, "avanzate-audio-invio.png"));
        var diag = new StatusDto
        {
            MachineId = me, MachineName = "DESKTOP-CASA", Version = linked.Version, AgentStartedUtc = DateTime.UtcNow.AddHours(-3),
            Peers =
            [
                new PeerDto { Id = lap, Name = "PORTATILE", State = "Connected", RttMs = 3.2, Link = "Wi-Fi", Adapter = "Wi-Fi",
                    LocalAddress = "192.168.1.20", Address = "192.168.1.34:47800", ConnectedSinceUtc = DateTime.UtcNow.AddMinutes(-133),
                    Drops24h = 2, LastDropUtc = DateTime.UtcNow.AddMinutes(-140), LastDropReason = "nessuna risposta da 3000 ms" },
                new PeerDto { Id = off, Name = "UFFICIO", State = "Disconnected", LastError = "connessione rifiutata" },
                new PeerDto { Id = Guid.NewGuid(), Name = "MANSARDA", State = "Connected", RttMs = 6.1, Link = "Cavo", Adapter = "Ethernet",
                    LocalAddress = "192.168.1.20", Address = "tramite PORTATILE", Via = "PORTATILE", ConnectedSinceUtc = DateTime.UtcNow.AddMinutes(-12) },
            ],
        };
        Save(() => new DiagnosticsForm(diag), Path.Combine(dir, "diagnostica.png"));
        Save(() => new PairRequestForm(new PairRequestDto { PeerName = "PORTATILE", Code = "482 913" }), Path.Combine(dir, "richiesta.png"));
        Save(() => new SetupForm(), Path.Combine(dir, "setup.png"));
        SaveTrayStates(Path.Combine(dir, "tray.png"), me, lap, off);
        // Una cattura vera dallo schermo: è l'unico modo di vedere la barra del titolo disegnata da Windows.
        Save(() => { var f = new MainForm(); f.UpdateStatus(linked); return f; }, Path.Combine(dir, "schermo-collegati.png"), onScreen: true);
        return 0;
    }

    /// <summary>Tutti gli stati dell'icona nella tray, a 16/24/32 px, su barra scura e chiara, con la frase del tooltip.</summary>
    private static void SaveTrayStates(string path, Guid me, Guid lap, Guid off)
    {
        StatusDto With(string mode, string? active, params string[] states) => new()
        {
            MachineId = me, MachineName = "DESKTOP-CASA", Mode = mode, ActivePeerName = active,
            Peers = states.Select((st, i) => new PeerDto { Id = i == 0 ? lap : off, Name = i == 0 ? "PORTATILE" : "UFFICIO", State = st }).ToList(),
        };
        static StatusDto WithController(StatusDto s, string name) { s.ControllerName = name; return s; }
        var cases = new (string Label, StatusDto? Status)[]
        {
            ("comanda", With("Controlling", "PORTATILE", "Connected", "Connected")),
            ("comandato", With("Controlled", "PORTATILE", "Connected", "Connected")),
            ("slave, mouse sul master", WithController(With("Local", null, "Connected", "Connected"), "PORTATILE")),
            ("tutti", With("Local", null, "Connected", "Connected")),
            ("alcuni", With("Local", null, "Connected", "Disconnected")),
            ("nessuno", With("Local", null, "Disconnected", "Disconnected")),
            ("servizio", null),
            ("disattivato", new StatusDto { Paused = true, Peers = [new PeerDto { Name = "PORTATILE", State = "Paused" }] }),
            ("non abbinato", With("Local", null)),
        };
        int[] sizes = [16, 24, 32];
        const int row = 44, iconsW = 2 * (16 + 24 + 32 + 3 * 12), textW = 440;
        using var bmp = new Bitmap(iconsW + textW, row * cases.Length);
        using var g = Graphics.FromImage(bmp);
        using var font = new Font("Segoe UI", 10f);
        g.Clear(Color.White);
        g.FillRectangle(new SolidBrush(Color.FromArgb(0x20, 0x20, 0x20)), 0, 0, iconsW / 2, bmp.Height);
        g.FillRectangle(new SolidBrush(Color.FromArgb(0xEE, 0xEE, 0xEE)), iconsW / 2, 0, iconsW / 2, bmp.Height);
        for (int i = 0; i < cases.Length; i++)
        {
            var st = TrayContext.Describe(cases[i].Status);
            var x = 12;
            foreach (var bg in new[] { 0, 1 })
                foreach (var sz in sizes)
                {
                    using var icon = VarcoIcon.RenderTray(sz, st.Color, st.Glyph);
                    g.DrawImage(icon, x, i * row + (row - sz) / 2);
                    x += sz + 12;
                }
            TextRenderer.DrawText(g, $"{cases[i].Label}: Varco: {st.Text}", font, new Point(iconsW + 8, i * row + 12), Color.Black);
        }
        bmp.Save(path);
    }

    private static void Save(Func<Form> make, string path, Action<Form>? after = null, bool onScreen = false)
    {
        using var f = make();
        f.StartPosition = FormStartPosition.Manual;
        f.Location = onScreen ? new Point(40, 40) : new Point(-5000, -5000);
        f.ShowInTaskbar = false;
        f.TopMost = onScreen;
        f.Show();
        after?.Invoke(f);
        Application.DoEvents();
        f.PerformLayout();
        Application.DoEvents();
        using var bmp = new Bitmap(f.Width, f.Height);
        if (onScreen)
        {
            for (int i = 0; i < 10; i++) { Thread.Sleep(50); Application.DoEvents(); }
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(f.Location, Point.Empty, f.Size);
        }
        else f.DrawToBitmap(bmp, new Rectangle(Point.Empty, f.Size));
        bmp.Save(path);
        f.Close();
    }
}
#endif
