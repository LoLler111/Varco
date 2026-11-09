using System.Security.Cryptography;
using Varco.Core.Update;

// Strumento per firmare le release di Varco.
//   keygen <chiave.pem>                    crea la chiave privata e stampa la pubblica da mettere in UpdateSignature.cs
//   sign <Varco.exe> <versione> <chiave.pem>  scrive Varco.exe.sig accanto all'exe
//   verify <Varco.exe> [Varco.exe.sig]     controlla l'exe con la chiave pubblica inclusa in Varco
try
{
    switch (args.FirstOrDefault())
    {
        case "keygen" when args.Length == 2:
        {
            if (File.Exists(args[1])) throw new InvalidOperationException($"{args[1]} esiste già: non la sovrascrivo.");
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(args[1]))!);
            File.WriteAllText(args[1], ec.ExportPkcs8PrivateKeyPem());
            Console.WriteLine(Convert.ToBase64String(ec.ExportSubjectPublicKeyInfo()));
            return 0;
        }

        case "sign" when args.Length == 4:
        {
            if (!UpdateSignature.TryParseVersion(args[2], out var version)) throw new ArgumentException($"Versione non valida: {args[2]}");
            using var ec = ECDsa.Create();
            ec.ImportFromPem(File.ReadAllText(args[3]));
            var m = UpdateSignature.Sign(args[1], version, ec);
            var sig = args[1] + ".sig";
            File.WriteAllText(sig, UpdateSignature.Serialize(m));
            Console.WriteLine($"Firmato {m.Version}: {sig}");
            return 0;
        }

        case "verify" when args.Length is 2 or 3:
        {
            var sig = args.Length == 3 ? args[2] : args[1] + ".sig";
            var m = UpdateSignature.Deserialize(File.ReadAllText(sig)) ?? throw new InvalidDataException("File .sig illeggibile");
            var (sha, size) = UpdateSignature.HashFile(args[1]);
            if (!UpdateSignature.Verify(m)) throw new InvalidDataException("Firma NON valida per la chiave pubblica di Varco");
            if (!string.Equals(sha, m.Sha256, StringComparison.OrdinalIgnoreCase) || size != m.Size)
                throw new InvalidDataException("L'exe non corrisponde al file .sig");
            Console.WriteLine($"OK: Varco {m.Version}, {size:N0} byte, firma valida");
            return 0;
        }

        default:
            Console.Error.WriteLine("Uso: keygen <chiave.pem> | sign <exe> <versione> <chiave.pem> | verify <exe> [sig]");
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Errore: {ex.Message}");
    return 1;
}
