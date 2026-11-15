using System.Security.Cryptography;
using System.Text;

namespace Varco.Core.Files;

/// <summary>
/// Percorsi relativi adatti ai "file virtuali" di Esplora risorse: nel descrittore (FILEDESCRIPTORW) il nome è lungo al
/// massimo 259 caratteri, cartelle comprese. Gli elementi più profondi prima venivano saltati.
///
/// Ora si accorciano i nomi delle cartelle che li contengono, a partire dalla più lunga: "Documentazione tecnica del
/// progetto" diventa "Documentazio~3F2A" (inizio del nome + impronta del percorso completo, così due cartelle diverse non
/// finiscono mai nella stessa). La scelta vale per tutta l'offerta: tutto ciò che stava in una cartella resta insieme,
/// nella cartella con il nome accorciato. Il nome del file si accorcia solo se non basta, mantenendo l'estensione.
/// </summary>
public static class VirtualPaths
{
    public const int MaxLength = 259;
    private const int KeepChars = 12;

    /// <summary>Per ogni percorso, quello da usare nel descrittore (identico se ci sta già), o null se non c'è modo di farcelo stare.</summary>
    /// <param name="directories">Quali elementi sono cartelle (anche il loro ultimo nome si può accorciare); null = solo quelle che ne contengono altri.</param>
    public static string?[] Fit(IReadOnlyList<string> paths, IReadOnlyList<bool>? directories = null, int maxLength = MaxLength)
    {
        var result = new string?[paths.Count];
        if (paths.All(p => p.Length <= maxLength))
        {
            for (var i = 0; i < paths.Count; i++) result[i] = paths[i];
            return result;
        }

        // Cartelle (percorso originale completo, senza separatore finale) da accorciare.
        var shortened = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
            for (var i = p.IndexOf('\\'); i >= 0; i = p.IndexOf('\\', i + 1))
                dirs.Add(p[..i]);
        // Elementi che sono cartelle: anche il loro ultimo nome si può accorciare.
        var isDir = new HashSet<string>(paths.Where((p, i) => dirs.Contains(p) || directories?[i] == true), StringComparer.OrdinalIgnoreCase);

        bool changed;
        do
        {
            changed = false;
            foreach (var p in paths)
            {
                if (Map(p, shortened).Length <= maxLength) continue;
                // La cartella con il nome più lungo, fra quelle non ancora accorciate.
                var parts = p.Split('\\');
                string? best = null;
                var bestLen = KeepChars + 5;
                var prefix = "";
                var count = isDir.Contains(p) ? parts.Length : parts.Length - 1;
                for (var k = 0; k < count; k++)
                {
                    prefix = k == 0 ? parts[0] : prefix + "\\" + parts[k];
                    if (!shortened.Contains(prefix) && parts[k].Length > bestLen) { best = prefix; bestLen = parts[k].Length; }
                }
                if (best is not null && shortened.Add(best)) changed = true;
            }
        } while (changed);

        for (var i = 0; i < paths.Count; i++)
        {
            var mapped = Map(paths[i], shortened);
            if (mapped.Length > maxLength && !isDir.Contains(paths[i])) mapped = ShortenFileName(mapped, paths[i], maxLength);
            result[i] = mapped.Length <= maxLength ? mapped : null;
        }
        return result;
    }

    private static string Map(string path, HashSet<string> shortened)
    {
        if (shortened.Count == 0) return path;
        var parts = path.Split('\\');
        var sb = new StringBuilder(path.Length);
        var prefix = "";
        for (var k = 0; k < parts.Length; k++)
        {
            prefix = k == 0 ? parts[0] : prefix + "\\" + parts[k];
            if (k > 0) sb.Append('\\');
            sb.Append(shortened.Contains(prefix) ? Short(parts[k], prefix) : parts[k]);
        }
        return sb.ToString();
    }

    /// <summary>"Nome lunghissimo" → "Nome lunghis~1A2B": l'impronta è del percorso completo, non del solo nome.</summary>
    private static string Short(string name, string fullPath)
    {
        return name[..Math.Min(KeepChars, name.Length)].TrimEnd(' ', '.') + "~" + Hash(fullPath);
    }

    private static string ShortenFileName(string mapped, string original, int maxLength)
    {
        var cut = mapped.LastIndexOf('\\') + 1;
        var dir = mapped[..cut];
        var name = mapped[cut..];
        var ext = Path.GetExtension(name);
        if (ext.Length > 16) ext = "";
        var room = maxLength - dir.Length - ext.Length - 5; // "~ABCD"
        if (room < 1) return mapped;
        var stem = Path.GetFileNameWithoutExtension(name);
        return dir + stem[..Math.Min(room, stem.Length)].TrimEnd(' ', '.') + "~" + Hash(original) + ext;
    }

    private static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.ToUpperInvariant())), 0, 2);
}
