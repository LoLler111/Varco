using System.Security.Cryptography;
using System.Text;

namespace Varco.Core.Config;

/// <summary>
/// Chiave di gruppo: 20 byte casuali (160 bit) mostrati come 8 blocchi base32,
/// es. "K7QF-2M9X-...". Chi conosce la chiave fa parte del gruppo.
/// </summary>
public static class GroupKey
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789"; // niente I, O, 0, 1
    public const int KeyBytes = 20;

    public static string Generate() => Format(RandomNumberGenerator.GetBytes(KeyBytes));

    public static string Format(byte[] key)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0, count = 0;
        foreach (var b in key)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                if (count > 0 && count % 4 == 0) sb.Append('-');
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
                count++;
            }
        }
        return sb.ToString();
    }

    /// <summary>Normalizza e decodifica la chiave scritta dall'utente.</summary>
    public static bool TryParse(string? text, out byte[] key)
    {
        key = [];
        if (string.IsNullOrWhiteSpace(text)) return false;
        var clean = new StringBuilder();
        foreach (var c in text.ToUpperInvariant())
        {
            if (c is '-' or ' ' or '\t') continue;
            if (Alphabet.IndexOf(c) < 0) return false;
            clean.Append(c);
        }
        if (clean.Length != KeyBytes * 8 / 5) return false;

        var result = new byte[KeyBytes];
        int buffer = 0, bits = 0, idx = 0;
        foreach (var c in clean.ToString())
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                result[idx++] = (byte)(buffer >> (bits - 8));
                bits -= 8;
            }
        }
        key = result;
        return true;
    }

    /// <summary>Etichetta pubblica del gruppo (non rivela la chiave): serve a scartare subito estranei.</summary>
    public static byte[] Tag(byte[] key) =>
        HMACSHA256.HashData(key, "varco-group-tag"u8)[..8];
}
