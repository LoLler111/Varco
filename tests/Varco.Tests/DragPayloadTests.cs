using System.Text;
using Varco.Core.Clipboard;
using Varco.Core.Files;

namespace Varco.Tests;

public class DragPayloadTests
{
    [Fact]
    public void Dragged_text_and_files_round_trip_and_unknown_formats_are_dropped()
    {
        var files = DragPayload.PackFiles([("foto.png", [1, 2, 3]), ("nota.txt", Encoding.UTF8.GetBytes("ciao"))]);
        var content = new ClipContent([
            new ClipFormat("CF_UNICODETEXT", Encoding.Unicode.GetBytes("testo")),
            new ClipFormat("HTML Format", Encoding.UTF8.GetBytes("<b>x</b>")),
            new ClipFormat("Formato sconosciuto", [9]),
            new ClipFormat(DragPayload.VirtualFilesFormat, files),
        ]);

        var back = DragPayload.TryUnwrap(ClipboardSync.Decompress(DragPayload.Wrap(content)));

        Assert.NotNull(back);
        Assert.Equal(["CF_UNICODETEXT", "HTML Format", DragPayload.VirtualFilesFormat], back!.Formats.Select(f => f.Name));
        Assert.Equal("testo", Encoding.Unicode.GetString(back.Formats[0].Data));
        var unpacked = DragPayload.UnpackFiles(back.Formats[2].Data);
        Assert.Equal(["foto.png", "nota.txt"], unpacked.Select(f => f.Name));
        Assert.Equal([1, 2, 3], unpacked[0].Data);
    }

    [Fact]
    public void A_file_offer_is_not_mistaken_for_dragged_data()
    {
        var offer = new FileOffer(Guid.NewGuid(), [new FileEntry(@"C:\a.txt", 1, FileAttributes.Normal, DateTime.UtcNow, false)]);
        Assert.Null(DragPayload.TryUnwrap(offer.Serialize()));
    }
}

public class DiagnosticsTests
{
    [Fact]
    public void Exported_config_has_no_group_keys()
    {
        var cfg = new Varco.Core.Config.VarcoConfig
        {
            GroupKey = "SEGRETA-1234",
            RetiredKeys = [new Varco.Core.Config.RetiredGroupKey { Key = "VECCHIA-5678" }],
            MachineName = "PC-PROVA",
        };
        var json = Varco.Core.Diagnostics.RedactedConfig(cfg);
        Assert.DoesNotContain("SEGRETA-1234", json);
        Assert.DoesNotContain("VECCHIA-5678", json);
        Assert.Contains("PC-PROVA", json);
        Assert.Equal("SEGRETA-1234", cfg.GroupKey); // la configurazione vera non cambia
    }
}
