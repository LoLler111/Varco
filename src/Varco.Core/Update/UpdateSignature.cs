using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Varco.Core.Update;

/// <summary>
/// Firma degli aggiornamenti (ECDSA P-256 + SHA-256). Ogni release su GitHub contiene Varco.exe e Varco.exe.sig:
/// il .sig dice versione, hash e dimensione dell'exe ed è firmato con la chiave privata che resta sul PC
/// di chi pubblica. Senza quella chiave nessuno può far installare un exe ai PC con Varco,
/// nemmeno chi riuscisse a caricare file nella pagina delle release.
/// </summary>
public static class UpdateSignature
{
    /// <summary>Chiave pubblica (SubjectPublicKeyInfo, base64). La privata: %USERPROFILE%\.varco\varco-update-key.pem.</summary>
    public const string PublicKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEG+Aunawk35vtD1akBub/x9sWqaVyyfzuTRTzbugaBw2iF/YvaUWwMxHeqPVjxjC0VIa1KsdabEnrMWO1F//XJQ==";

    public const string ExeAssetName = "Varco.exe";
    public const string SigAssetName = "Varco.exe.sig";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>Dati firmati: la versione è dentro la firma, così non si può spacciare un exe vecchio per nuovo.</summary>
    public static byte[] SignedData(UpdateManifest m) =>
        Encoding.UTF8.GetBytes($"VARCO-UPDATE\n{m.Version}\n{m.Sha256.ToLowerInvariant()}\n{m.Size}\n");

    public static UpdateManifest Sign(string exePath, Version version, ECDsa privateKey)
    {
        var (sha, size) = HashFile(exePath);
        var m = new UpdateManifest { Version = Normalize(version).ToString(3), Sha256 = sha, Size = size };
        m.Signature = Convert.ToBase64String(privateKey.SignData(SignedData(m), HashAlgorithmName.SHA256));
        return m;
    }

    public static bool Verify(UpdateManifest m, string publicKey = PublicKey)
    {
        try
        {
            if (!TryParseVersion(m.Version, out _) || m.Sha256.Length != 64 || m.Size <= 0) return false;
            using var ec = ECDsa.Create();
            ec.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return ec.VerifyData(SignedData(m), Convert.FromBase64String(m.Signature), HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    public static (string Sha256, long Size) HashFile(string path)
    {
        using var f = File.OpenRead(path);
        var hash = SHA256.HashData(f);
        return (Convert.ToHexStringLower(hash), f.Length);
    }

    public static string Serialize(UpdateManifest m) => JsonSerializer.Serialize(m, Json);

    public static UpdateManifest? Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<UpdateManifest>(json, Json); }
        catch { return null; }
    }

    /// <summary>"v0.3.0", "0.3", "0.3.0.0" → 0.3.0 (sempre tre parti, così i confronti sono coerenti).</summary>
    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;
        var t = text.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        var plus = t.IndexOfAny(['+', '-']);
        if (plus >= 0) t = t[..plus];
        if (!Version.TryParse(t, out var v)) return false;
        version = Normalize(v);
        return true;
    }

    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));
}

/// <summary>Contenuto di Varco.exe.sig.</summary>
public sealed class UpdateManifest
{
    public string Version { get; set; } = "";
    public string Sha256 { get; set; } = "";
    public long Size { get; set; }
    public string Signature { get; set; } = "";
}
