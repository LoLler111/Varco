using System.Drawing.Imaging;
using Varco.Tray;

// Scrive assets/varco.ico (più dimensioni, frame PNG) e alcune anteprime PNG.
var root = FindRoot(AppContext.BaseDirectory);
var assets = Directory.CreateDirectory(Path.Combine(root, "assets")).FullName;

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var frames = sizes.Select(s => (Size: s, Png: ToPng(VarcoIcon.RenderApp(s)))).ToList();

using (var fs = File.Create(Path.Combine(assets, "varco.ico")))
using (var w = new BinaryWriter(fs))
{
    w.Write((ushort)0); // riservato
    w.Write((ushort)1); // tipo: icona
    w.Write((ushort)frames.Count);
    var offset = 6 + 16 * frames.Count;
    foreach (var (size, png) in frames)
    {
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)(size >= 256 ? 0 : size));
        w.Write((byte)0);  // palette
        w.Write((byte)0);  // riservato
        w.Write((ushort)1); // piani
        w.Write((ushort)32); // bit per pixel
        w.Write(png.Length);
        w.Write(offset);
        offset += png.Length;
    }
    foreach (var (_, png) in frames) w.Write(png);
}

File.WriteAllBytes(Path.Combine(assets, "varco-256.png"), frames.Single(f => f.Size == 256).Png);

// Anteprima: icona dell'app a varie dimensioni + i quattro stati della tray.
using (var sheet = new Bitmap(560, 200))
using (var g = Graphics.FromImage(sheet))
{
    g.Clear(Color.White);
    var x = 10;
    foreach (var s in new[] { 128, 64, 48, 32, 24, 16 })
    {
        using var b = VarcoIcon.RenderApp(s);
        g.DrawImageUnscaled(b, x, 10 + (128 - s));
        x += s + 16;
    }
    x = 10;
    foreach (var c in new[] { Color.FromArgb(140, 140, 140), Color.FromArgb(46, 160, 67), Color.FromArgb(219, 140, 20), Color.FromArgb(40, 110, 220) })
    {
        using var t32 = VarcoIcon.RenderTray(32, c);
        using var t16 = VarcoIcon.RenderTray(16, c);
        g.DrawImageUnscaled(t32, x, 176 - 16);
        g.DrawImageUnscaled(t16, x + 38, 176 - 8);
        x += 70;
    }
    sheet.Save(Path.Combine(assets, "anteprima.png"), ImageFormat.Png);
}

Console.WriteLine($"Icone scritte in {assets}");

static byte[] ToPng(Bitmap bmp)
{
    using (bmp)
    using (var ms = new MemoryStream())
    {
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }
}

static string FindRoot(string start)
{
    for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
        if (File.Exists(Path.Combine(d.FullName, "Varco.slnx"))) return d.FullName;
    throw new DirectoryNotFoundException("Varco.slnx non trovato");
}
