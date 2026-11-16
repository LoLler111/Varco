using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Varco.Core.Config;
using Varco.Core.Control;

namespace Varco.Core;

/// <summary>
/// «Esporta log»: uno zip con i log dell'agent, del servizio e del setup, lo stato dei collegamenti e la configurazione
/// senza chiavi di gruppo. Lo crea l'agent (SYSTEM), perché la cartella di Varco in ProgramData è protetta; l'icona lo
/// salva dove sceglie l'utente.
/// </summary>
public static class Diagnostics
{
    private static readonly string[] LogFiles = ["agent.log", "agent.log.1", "service.log", "service.log.1", "setup.log", "setup.log.1"];
    private const string Hidden = "(tolta dall'esportazione)";

    public static byte[] ExportLogs(ConfigStore store, StatusDto status)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            var logs = Path.Combine(store.Directory, "logs");
            foreach (var name in LogFiles)
            {
                var path = Path.Combine(logs, name);
                if (!File.Exists(path)) continue;
                try
                {
                    // L'agent sta scrivendo agent.log proprio adesso: lo si legge condiviso.
                    using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var dst = zip.CreateEntry(name, CompressionLevel.Optimal).Open();
                    src.CopyTo(dst);
                }
                catch (IOException ex)
                {
                    Log.Warn($"Esporta log: {name} non letto ({ex.Message})");
                }
            }
            Add(zip, "config.json", RedactedConfig(store.Current));
            Add(zip, "stato.json", JsonSerializer.Serialize(status, new JsonSerializerOptions(ControlProtocol.Json) { WriteIndented = true }));
        }
        Log.Info($"Log esportati ({ms.Length / 1024} KB)");
        return ms.ToArray();
    }

    /// <summary>La configurazione senza la chiave di gruppo né quelle sostituite: con quelle si entra nel gruppo.</summary>
    public static string RedactedConfig(VarcoConfig config)
    {
        var node = JsonSerializer.SerializeToNode(config, new JsonSerializerOptions { WriteIndented = true })!.AsObject();
        if (node["GroupKey"] is not null) node["GroupKey"] = Hidden;
        if (node["RetiredKeys"] is JsonArray retired)
            foreach (var r in retired.OfType<JsonObject>())
                if (r["Key"] is not null) r["Key"] = Hidden;
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void Add(ZipArchive zip, string name, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(name, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        w.Write(text);
    }
}
