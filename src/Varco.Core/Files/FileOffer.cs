using System.Security.Cryptography;
using System.Text;

namespace Varco.Core.Files;

/// <summary>Un elemento dell'offerta: percorso relativo ("Cartella\file.mp4"), dimensione, attributi, data di modifica.</summary>
public sealed record FileEntry(string Path, long Size, FileAttributes Attributes, DateTime LastWriteUtc, bool IsDirectory);

/// <summary>
/// File copiati su un PC e offerti agli altri: viaggia negli appunti (formato <see cref="Clipboard.ClipboardSync.FilesFormat"/>)
/// e contiene solo nomi e dimensioni. I dati si trasferiscono quando l'utente incolla, con <see cref="FileTransfer"/>.
/// </summary>
public sealed record FileOffer(Guid Id, IReadOnlyList<FileEntry> Entries)
{
    private const byte Version = 1;

    public long TotalBytes => Entries.Sum(e => e.Size);
    public int FileCount => Entries.Count(e => !e.IsDirectory);

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Version);
        w.Write(Id.ToByteArray());
        w.Write(Entries.Count);
        foreach (var e in Entries)
        {
            w.Write(e.Path);
            w.Write(e.Size);
            w.Write((uint)e.Attributes);
            w.Write(e.LastWriteUtc.Ticks);
            w.Write(e.IsDirectory);
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Null se il formato è di una versione futura o non valido.</summary>
    public static FileOffer? Deserialize(byte[] data)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
            if (r.ReadByte() != Version) return null;
            var id = new Guid(r.ReadBytes(16));
            var count = r.ReadInt32();
            if (count < 0 || count > FileOffers.MaxEntries) return null;
            var list = new List<FileEntry>(count);
            for (var i = 0; i < count; i++)
            {
                var path = r.ReadString();
                var size = r.ReadInt64();
                var attrs = (FileAttributes)r.ReadUInt32();
                var ticks = r.ReadInt64();
                var dir = r.ReadBoolean();
                if (!IsSafeRelativePath(path) || size < 0) return null;
                list.Add(new FileEntry(path, size, attrs, new DateTime(Math.Clamp(ticks, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), DateTimeKind.Utc), dir));
            }
            return new FileOffer(id, list);
        }
        catch (Exception ex) when (ex is EndOfStreamException or IOException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Percorso relativo innocuo: niente unità, niente "..", niente caratteri vietati.
    /// Chi incolla crea cartelle e file con questi nomi: un PC del gruppo non deve poter scrivere fuori dalla destinazione.
    /// </summary>
    public static bool IsSafeRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024) return false;
        if (path.StartsWith('\\') || path.StartsWith('/') || path.Contains(':')) return false;
        foreach (var part in path.Split('\\'))
        {
            if (part.Length == 0 || part == "." || part == "..") return false;
            if (part.IndexOfAny(['/', '<', '>', '"', '|', '?', '*']) >= 0 || part.Any(char.IsControl)) return false;
        }
        return true;
    }
}

/// <summary>Accesso ai file con i permessi dell'utente collegato (l'agent gira come SYSTEM).</summary>
public interface IFileAccess
{
    T RunAsUser<T>(Func<T> action);
}

/// <summary>Senza impersonazione: test e agent avviato a mano.</summary>
public sealed class DirectFileAccess : IFileAccess
{
    public static readonly DirectFileAccess Instance = new();
    public T RunAsUser<T>(Func<T> action) => action();
}

/// <summary>
/// Offerte create su questo PC: per ognuna ricorda il percorso vero di ogni elemento.
/// Un altro PC può chiedere solo "elemento N dell'offerta X", mai un percorso arbitrario.
/// </summary>
public sealed class FileOffers(IFileAccess access)
{
    /// <summary>Oltre questo numero di elementi (es. una cartella enorme) l'offerta non viene creata.</summary>
    public const int MaxEntries = 20_000;
    private const int KeepOffers = 8;

    private readonly object _gate = new();
    private readonly LinkedList<(FileOffer Offer, string[] FullPaths)> _offers = new();

    /// <summary>Crea l'offerta per i file e le cartelle copiati. Null se non c'è niente di leggibile o se è troppo grande.</summary>
    public FileOffer? Create(IReadOnlyList<string> paths)
    {
        var collected = access.RunAsUser(() => Collect(paths));
        if (collected is null || collected.Count == 0) return null;

        // Id ricavato dal contenuto: la stessa copia ripetuta dà la stessa offerta (e gli appunti non la rispediscono).
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        // La data delle cartelle no: Windows la ritocca da sola (indicizzazione, antivirus) anche senza modifiche vere.
        foreach (var (e, full) in collected)
            hash.AppendData(Encoding.UTF8.GetBytes(e.IsDirectory ? $"{full}|dir\n" : $"{full}|{e.Size}|{e.LastWriteUtc.Ticks}\n"));
        var offer = new FileOffer(new Guid(hash.GetHashAndReset().AsSpan(0, 16)), collected.Select(c => c.Entry).ToList());

        lock (_gate)
        {
            var existing = _offers.FirstOrDefault(o => o.Offer.Id == offer.Id);
            if (existing.Offer is not null) _offers.Remove(existing);
            _offers.AddFirst((offer, collected.Select(c => c.FullPath).ToArray()));
            while (_offers.Count > KeepOffers) _offers.RemoveLast();
        }
        return offer;
    }

    private static List<(FileEntry Entry, string FullPath)>? Collect(IReadOnlyList<string> paths)
    {
        var list = new List<(FileEntry, string)>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in paths)
        {
            var path = raw.TrimEnd('\\');
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            if (!info.Exists) continue;
            var name = UniqueName(names, info.Name);
            if (!Add(list, info, name)) return null;
        }
        return list;

        static bool Add(List<(FileEntry, string)> list, FileSystemInfo info, string rel)
        {
            if (list.Count >= MaxEntries)
            {
                Log.Info($"Troppi file da condividere (oltre {MaxEntries})");
                return false;
            }
            if (info is FileInfo f)
            {
                list.Add((new FileEntry(rel, f.Length, f.Attributes, f.LastWriteTimeUtc, false), f.FullName));
                return true;
            }
            var d = (DirectoryInfo)info;
            list.Add((new FileEntry(rel, 0, d.Attributes, d.LastWriteTimeUtc, true), d.FullName));
            IEnumerable<FileSystemInfo> children;
            try { children = d.EnumerateFileSystemInfos().ToList(); }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                Log.Debug($"Cartella non leggibile: {d.FullName} ({ex.Message})");
                return true;
            }
            foreach (var c in children)
            {
                // Collegamenti a cartelle (junction): si salterebbe in giro per il disco, magari in cerchio.
                if (c is DirectoryInfo && c.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                if (!Add(list, c, rel + "\\" + c.Name)) return false;
            }
            return true;
        }
    }

    private static string UniqueName(HashSet<string> used, string name)
    {
        if (used.Add(name)) return name;
        var stem = Path.GetFileNameWithoutExtension(name);
        var ext = Path.GetExtension(name);
        for (var i = 2; ; i++)
        {
            var candidate = $"{stem} ({i}){ext}";
            if (used.Add(candidate)) return candidate;
        }
    }

    /// <summary>Apre l'elemento <paramref name="index"/> dell'offerta, dalla posizione <paramref name="offset"/>.</summary>
    public (Stream? Data, long Length, string? Error) Open(Guid offerId, int index, long offset)
    {
        string? full = null;
        FileEntry? entry = null;
        lock (_gate)
        {
            foreach (var (offer, paths) in _offers)
            {
                if (offer.Id != offerId) continue;
                if (index >= 0 && index < paths.Length)
                {
                    full = paths[index];
                    entry = offer.Entries[index];
                }
                break;
            }
        }
        if (full is null || entry is null) return (null, 0, "i file non sono più disponibili: copiali di nuovo");
        if (entry.IsDirectory) return (null, 0, "è una cartella");
        try
        {
            var stream = access.RunAsUser(() => (Stream)new FileStream(full, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan));
            if (offset < 0 || offset > stream.Length)
            {
                stream.Dispose();
                return (null, 0, "posizione non valida");
            }
            stream.Position = offset;
            return (stream, stream.Length - offset, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, 0, ex.Message);
        }
    }
}
