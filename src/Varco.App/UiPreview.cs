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
                new LayoutEntry { Id = me, Name = "DESKTOP-CASA", X = 0, Y = 0, Width = 1920, Height = 1080 },
                new LayoutEntry { Id = lap, Name = "PORTATILE", X = 1920, Y = 200, Width = 1536, Height = 864 },
                new LayoutEntry { Id = off, Name = "UFFICIO", X = -1920, Y = 0, Width = 1920, Height = 1080 },
            ],
        };

        Save(() => { var f = new MainForm(); f.UpdateStatus(empty); return f; }, Path.Combine(dir, "main-vuota.png"));
        Save(() => { var f = new MainForm(); f.UpdateStatus(found); return f; }, Path.Combine(dir, "main-trovato.png"));
        Save(() => { var f = new MainForm(); f.UpdateStatus(linked); return f; }, Path.Combine(dir, "main-collegati.png"));
        Save(() => new MainForm(), Path.Combine(dir, "main-servizio-spento.png"), s => ((MainForm)s).UpdateStatus(null));
        Save(() => new AdvancedForm(linked), Path.Combine(dir, "avanzate.png"));
        Save(() => new PairRequestForm(new PairRequestDto { PeerName = "PORTATILE", Code = "482 913" }), Path.Combine(dir, "richiesta.png"));
        Save(() => new SetupForm(), Path.Combine(dir, "setup.png"));
        // Una cattura vera dallo schermo: è l'unico modo di vedere la barra del titolo disegnata da Windows.
        Save(() => { var f = new MainForm(); f.UpdateStatus(linked); return f; }, Path.Combine(dir, "schermo-collegati.png"), onScreen: true);
        return 0;
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
