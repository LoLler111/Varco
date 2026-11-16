namespace Varco.Core.Clipboard;

/// <summary>
/// Testo, HTML e immagini trascinati da un PC all'altro (per esempio da un browser). Viaggiano nello stesso messaggio
/// dei file trascinati (<see cref="Protocol.DragFiles"/>), compressi, con un primo byte diverso da quello di un'offerta
/// di file. Si mandano solo ai PC che annunciano <see cref="Protocol.PeerCaps.DragData"/>.
/// </summary>
public static class DragPayload
{
    /// <summary>Primo byte (dopo la decompressione): un'offerta di file comincia con la sua versione, 1.</summary>
    private const byte Marker = 0xDA;

    /// <summary>
    /// File senza percorso trascinati da un programma (l'immagine trascinata da un browser, un allegato): numero di file,
    /// poi per ognuno nome e contenuto.
    /// </summary>
    public const string VirtualFilesFormat = "Varco.VirtualFiles";

    /// <summary>Formati che si trascinano: quelli degli appunti condivisi più i file senza percorso.</summary>
    public static readonly string[] Formats = ["CF_UNICODETEXT", "HTML Format", "Rich Text Format", "PNG", "CF_DIB", VirtualFilesFormat];

    public static byte[] Wrap(ClipContent content)
    {
        var raw = ClipboardSync.Serialize(new ClipContent(content.Formats.Where(f => Formats.Contains(f.Name)).ToList()));
        return ClipboardSync.Compress([Marker, .. raw]);
    }

    /// <summary>Contenuto trascinato, se <paramref name="raw"/> (già decompresso) lo è; null se è un'offerta di file.</summary>
    public static ClipContent? TryUnwrap(byte[] raw)
    {
        if (raw.Length == 0 || raw[0] != Marker) return null;
        var content = ClipboardSync.Deserialize(raw[1..]);
        return new ClipContent(content.Formats.Where(f => Formats.Contains(f.Name)).ToList());
    }

    public static byte[] PackFiles(IReadOnlyList<(string Name, byte[] Data)> files)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, System.Text.Encoding.UTF8);
        w.Write(files.Count);
        foreach (var (name, data) in files)
        {
            w.Write(name);
            w.Write(data.Length);
            w.Write(data);
        }
        w.Flush();
        return ms.ToArray();
    }

    public static List<(string Name, byte[] Data)> UnpackFiles(byte[] packed)
    {
        using var r = new BinaryReader(new MemoryStream(packed), System.Text.Encoding.UTF8);
        var count = r.ReadInt32();
        if (count < 0 || count > 1000) throw new InvalidDataException("Troppi file");
        var files = new List<(string, byte[])>(count);
        for (var i = 0; i < count; i++)
        {
            var name = r.ReadString();
            var len = r.ReadInt32();
            if (len < 0 || len > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("File troncato");
            files.Add((name, r.ReadBytes(len)));
        }
        return files;
    }

    /// <summary>Per il log: cosa si sta trascinando.</summary>
    public static string Describe(ClipContent c)
    {
        var names = c.Formats.Select(f => f.Name).ToList();
        var parts = new List<string>();
        if (names.Contains(VirtualFilesFormat))
        {
            var n = UnpackFiles(c.Formats.First(f => f.Name == VirtualFilesFormat).Data).Count;
            parts.Add(n == 1 ? "1 file senza percorso" : $"{n} file senza percorso");
        }
        if (names.Contains("PNG") || names.Contains("CF_DIB")) parts.Add("immagine");
        if (names.Contains("HTML Format") || names.Contains("Rich Text Format")) parts.Add("testo formattato");
        else if (names.Contains("CF_UNICODETEXT")) parts.Add("testo");
        var kb = Math.Max(1, c.TotalBytes / 1024);
        return $"{string.Join(", ", parts)} ({kb} KB)";
    }
}
