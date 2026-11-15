using Varco.Core.Files;

namespace Varco.Tests;

public class VirtualPathsTests
{
    [Fact]
    public void Short_paths_are_untouched()
    {
        string[] paths = ["a.txt", @"Cartella", @"Cartella\b.txt"];
        Assert.Equal(paths, VirtualPaths.Fit(paths));
    }

    [Fact]
    public void Long_paths_shorten_folders_consistently_and_keep_file_names()
    {
        var deep = "Documentazione tecnica del progetto di ristrutturazione";
        var root = "Progetto";
        var dir1 = root + "\\" + deep;
        var dir2 = dir1 + "\\" + new string('x', 90);
        var dir3 = dir2 + "\\" + new string('y', 90);
        var longFile = dir3 + @"\relazione finale.docx";   // oltre 259
        var shortFile = dir1 + @"\indice.txt";              // ci sta, ma è nella stessa cartella
        string[] paths = [root, dir1, dir2, dir3, longFile, shortFile];
        Assert.True(longFile.Length > 259);
        bool[] dirs = [true, true, true, true, false, false];

        var fit = VirtualPaths.Fit(paths, dirs);

        Assert.All(fit, f => Assert.True(f is not null && f.Length <= 259));
        Assert.EndsWith(@"\relazione finale.docx", fit[4]);
        // Struttura conservata: ogni elemento resta dentro la versione (eventualmente accorciata) della sua cartella.
        Assert.StartsWith(fit[3] + "\\", fit[4]);
        Assert.StartsWith(fit[2] + "\\", fit[3]);
        Assert.StartsWith(fit[1] + "\\", fit[2]);
        Assert.StartsWith(fit[1] + "\\", fit[5]);
        Assert.Equal(root, fit[0]);
        // Nomi unici: nessun elemento finisce sopra un altro.
        Assert.Equal(fit.Length, fit.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Two_long_folders_with_the_same_start_stay_apart()
    {
        var a = "Cartella con un nome molto lungo numero uno " + new string('a', 120);
        var b = "Cartella con un nome molto lungo numero due " + new string('a', 120);
        var tail = "\\" + new string('z', 120) + ".bin";
        string[] paths = [a + tail, b + tail];
        var fit = VirtualPaths.Fit(paths);
        Assert.All(fit, f => Assert.True(f!.Length <= 259));
        Assert.NotEqual(fit[0], fit[1]);
    }
}
