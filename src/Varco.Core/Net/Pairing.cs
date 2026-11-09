using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Varco.Core.Crypto;

namespace Varco.Core.Net;

/// <summary>Chi sono, nella fase di abbinamento.</summary>
public sealed record PairOffer(Guid Id, string Name, int ListenPort, int PeerCount);

/// <summary>Canale cifrato temporaneo fra due PC che si stanno abbinando.</summary>
public sealed class PairingSession(Stream stream, FrameCipher cipher, PairOffer peer, string code) : IDisposable
{
    public PairOffer Peer { get; } = peer;
    /// <summary>Codice a 6 cifre mostrato su entrambi i PC: se coincide, nessuno si è messo in mezzo.</summary>
    public string Code { get; } = code;

    public Task SendAsync(byte[] plain, CancellationToken ct) => Framing.WriteFrameAsync(stream, cipher.Seal(plain), ct);

    public async Task<byte[]> ReceiveAsync(CancellationToken ct) => cipher.Open(await Framing.ReadFrameAsync(stream, ct).ConfigureAwait(false));

    public void Dispose()
    {
        cipher.Dispose();
        stream.Dispose();
    }
}

/// <summary>
/// Abbinamento di due PC senza chiavi da copiare, sicuro contro chi si mette in mezzo:
///
///   A → B : Hello   (id, nome, impegno = SHA256(chiave pubblica A ‖ nonce A))
///   B → A : Reply   (id, nome, chiave pubblica B, nonce B)
///   A → B : Reveal  (chiave pubblica A, nonce A)        B verifica l'impegno
///
/// Entrambi derivano dal transcript un codice a 6 cifre. Grazie all'impegno, un attaccante non può
/// scegliere le sue chiavi per far coincidere i codici: ha una sola possibilità su un milione.
/// Poi, sul canale cifrato, B accetta o rifiuta e uno dei due manda all'altro la chiave di gruppo.
/// </summary>
public static class PairingProtocol
{
    private static readonly byte[] Magic = "VRCP"u8.ToArray();
    private const byte Version = 1;

    public static bool IsPairingFrame(byte[] frame) => frame.Length >= 4 && frame.AsSpan(0, 4).SequenceEqual(Magic);

    public static async Task<PairingSession> InitiateAsync(Stream s, PairOffer me, CancellationToken ct)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var pub = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var nonce = RandomNumberGenerator.GetBytes(32);
        var commit = SHA256.HashData([.. pub, .. nonce]);

        var hello = Write(w => { WriteHeader(w, me); w.Write(commit); });
        await Framing.WriteFrameAsync(s, hello, ct).ConfigureAwait(false);

        var reply = await Framing.ReadFrameAsync(s, ct).ConfigureAwait(false);
        PairOffer peer;
        byte[] peerPub, peerNonce;
        using (var r = Reader(reply))
        {
            peer = ReadHeader(r);
            peerPub = r.ReadBytes(r.ReadUInt16());
            peerNonce = r.ReadBytes(32);
        }
        if (peer.Id == me.Id) throw new HandshakeException("connessione verso me stesso");

        var reveal = Write(w => { w.Write((ushort)pub.Length); w.Write(pub); w.Write(nonce); });
        await Framing.WriteFrameAsync(s, reveal, ct).ConfigureAwait(false);

        var (a2b, b2a, code) = Derive(ecdh, peerPub, [.. hello, .. reply, .. reveal]);
        return new PairingSession(s, new FrameCipher(a2b, b2a), peer, code);
    }

    public static async Task<PairingSession> RespondAsync(Stream s, byte[] hello, PairOffer me, CancellationToken ct)
    {
        PairOffer peer;
        byte[] commit;
        using (var r = Reader(hello))
        {
            peer = ReadHeader(r);
            commit = r.ReadBytes(32);
        }
        if (peer.Id == me.Id) throw new HandshakeException("connessione verso me stesso");

        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var pub = ecdh.PublicKey.ExportSubjectPublicKeyInfo();
        var reply = Write(w => { WriteHeader(w, me); w.Write((ushort)pub.Length); w.Write(pub); w.Write(RandomNumberGenerator.GetBytes(32)); });
        await Framing.WriteFrameAsync(s, reply, ct).ConfigureAwait(false);

        var reveal = await Framing.ReadFrameAsync(s, ct).ConfigureAwait(false);
        byte[] peerPub, peerNonce;
        using (var r = Reader(reveal))
        {
            peerPub = r.ReadBytes(r.ReadUInt16());
            peerNonce = r.ReadBytes(32);
        }
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData([.. peerPub, .. peerNonce]), commit))
            throw new HandshakeException("abbinamento non valido (impegno non rispettato)");

        var (a2b, b2a, code) = Derive(ecdh, peerPub, [.. hello, .. reply, .. reveal]);
        return new PairingSession(s, new FrameCipher(b2a, a2b), peer, code);
    }

    private static (byte[] A2B, byte[] B2A, string Code) Derive(ECDiffieHellman mine, byte[] peerPub, byte[] transcriptBytes)
    {
        byte[] secret;
        using (var peer = ECDiffieHellman.Create())
        {
            peer.ImportSubjectPublicKeyInfo(peerPub, out _);
            secret = mine.DeriveRawSecretAgreement(peer.PublicKey);
        }
        var transcript = SHA256.HashData(transcriptBytes);
        var okm = HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 64, "varco-pair"u8.ToArray(), transcript);
        CryptographicOperations.ZeroMemory(secret);
        var n = BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData([.. "varco-pair-code"u8, .. transcript])) % 1_000_000;
        var digits = n.ToString("D6");
        return (okm[..32], okm[32..], $"{digits[..3]} {digits[3..]}");
    }

    private static void WriteHeader(BinaryWriter w, PairOffer me)
    {
        w.Write(Magic);
        w.Write(Version);
        w.Write(me.Id.ToByteArray());
        w.Write(me.Name.Length > 60 ? me.Name[..60] : me.Name);
        w.Write((ushort)me.ListenPort);
        w.Write((ushort)Math.Min(me.PeerCount, ushort.MaxValue));
    }

    private static PairOffer ReadHeader(BinaryReader r)
    {
        if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic)) throw new HandshakeException("protocollo sconosciuto");
        var ver = r.ReadByte();
        if (ver != Version) throw new HandshakeException($"versione di abbinamento {ver} non supportata: aggiorna Varco su entrambi i PC");
        return new PairOffer(new Guid(r.ReadBytes(16)), r.ReadString(), r.ReadUInt16(), r.ReadUInt16());
    }

    private static byte[] Write(Action<BinaryWriter> body)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        body(w);
        w.Flush();
        return ms.ToArray();
    }

    private static BinaryReader Reader(byte[] data) => new(new MemoryStream(data), Encoding.UTF8);

    // ── messaggi sul canale cifrato ──

    /// <summary>Risposta di B: accetta/rifiuta e, se il suo gruppo "vince", la sua chiave.</summary>
    public static byte[] EncodeDecision(bool accept, byte[]? groupKey) =>
        [(byte)(accept ? 1 : 0), (byte)(groupKey?.Length ?? 0), .. groupKey ?? []];

    public static (bool Accept, byte[]? GroupKey) DecodeDecision(byte[] data)
    {
        if (data.Length < 2) throw new InvalidDataException("decisione non valida");
        var len = data[1];
        return (data[0] == 1, len == 0 ? null : data.AsSpan(2, len).ToArray());
    }
}
