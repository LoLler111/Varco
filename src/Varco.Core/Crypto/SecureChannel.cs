using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Varco.Core.Config;

namespace Varco.Core.Crypto;

/// <summary>Frame con lunghezza (uint32 little-endian) + payload.</summary>
internal static class Framing
{
    public const int MaxFrame = 1 << 20;

    public static async Task WriteFrameAsync(Stream s, byte[] payload, CancellationToken ct)
    {
        var buf = new byte[4 + payload.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)payload.Length);
        payload.CopyTo(buf, 4);
        await s.WriteAsync(buf, ct).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadFrameAsync(Stream s, CancellationToken ct)
    {
        var header = new byte[4];
        await s.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var len = BinaryPrimitives.ReadUInt32LittleEndian(header);
        if (len > MaxFrame) throw new InvalidDataException($"Frame troppo grande ({len} byte)");
        var payload = new byte[len];
        await s.ReadExactlyAsync(payload, ct).ConfigureAwait(false);
        return payload;
    }
}

/// <summary>AES-256-GCM con chiavi separate per direzione e nonce = contatore (mai riusato).</summary>
public sealed class FrameCipher : IDisposable
{
    private const int TagSize = 16;
    private readonly AesGcm _send;
    private readonly AesGcm _recv;
    private ulong _sendCounter;
    private ulong _recvCounter;
    private readonly object _sendGate = new();

    internal FrameCipher(byte[] sendKey, byte[] recvKey)
    {
        _send = new AesGcm(sendKey, TagSize);
        _recv = new AesGcm(recvKey, TagSize);
    }

    public byte[] Seal(byte[] plain)
    {
        var output = new byte[plain.Length + TagSize];
        Span<byte> nonce = stackalloc byte[12];
        lock (_sendGate)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _sendCounter++);
            _send.Encrypt(nonce, plain, output.AsSpan(0, plain.Length), output.AsSpan(plain.Length));
        }
        return output;
    }

    /// <summary>Decifra; lancia <see cref="CryptographicException"/> se il frame è stato alterato o riordinato.</summary>
    public byte[] Open(byte[] frame)
    {
        if (frame.Length < TagSize) throw new CryptographicException("Frame troppo corto");
        var plain = new byte[frame.Length - TagSize];
        Span<byte> nonce = stackalloc byte[12];
        BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], _recvCounter++);
        _recv.Decrypt(nonce, frame.AsSpan(0, plain.Length), frame.AsSpan(plain.Length), plain);
        return plain;
    }

    public void Dispose()
    {
        _send.Dispose();
        _recv.Dispose();
    }
}

public sealed record LocalIdentity(Guid Id, string Name, int ListenPort, byte[] GroupKey)
{
    public byte[] GroupTag { get; } = Config.GroupKey.Tag(GroupKey);
}

/// <param name="SessionId">Hash del transcript: identico sui due lati, serve a risolvere in modo deterministico le connessioni doppie.</param>
public sealed record HandshakeResult(Guid PeerId, string PeerName, int PeerListenPort, FrameCipher Cipher, byte[] SessionId);

public sealed class HandshakeException(string message) : Exception(message);

/// <summary>
/// Handshake autenticato dalla chiave di gruppo, con forward secrecy:
/// ECDH effimero P-256, chiavi derivate con HKDF usando la chiave di gruppo come salt,
/// conferma reciproca con HMAC sul transcript (chi non ha la chiave non passa).
///
///   client → server : Hello
///   server → client : Hello + Finished(server)
///   client → server : Finished(client)
/// </summary>
public static class Handshake
{
    private static readonly byte[] Magic = "VRCO"u8.ToArray();
    private const byte Version = 1;

    private sealed record Hello(byte[] Raw, byte[] Tag, Guid Id, int Port, string Name, byte[] PubKey);

    public static Task<HandshakeResult> RunClientAsync(Stream s, LocalIdentity me, CancellationToken ct) =>
        RunAsync(s, me, isClient: true, ct);

    public static Task<HandshakeResult> RunServerAsync(Stream s, LocalIdentity me, CancellationToken ct) =>
        RunAsync(s, me, isClient: false, ct);

    private static async Task<HandshakeResult> RunAsync(Stream s, LocalIdentity me, bool isClient, CancellationToken ct)
    {
        using var ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var myHello = BuildHello(me, ecdh.PublicKey.ExportSubjectPublicKeyInfo());

        Hello peer;
        byte[] clientRaw, serverRaw;
        if (isClient)
        {
            await Framing.WriteFrameAsync(s, myHello, ct).ConfigureAwait(false);
            peer = ParseHello(await Framing.ReadFrameAsync(s, ct).ConfigureAwait(false));
            clientRaw = myHello;
            serverRaw = peer.Raw;
        }
        else
        {
            peer = ParseHello(await Framing.ReadFrameAsync(s, ct).ConfigureAwait(false));
            clientRaw = peer.Raw;
            serverRaw = myHello;
        }

        if (!CryptographicOperations.FixedTimeEquals(peer.Tag, me.GroupTag))
            throw new HandshakeException("il PC remoto appartiene a un altro gruppo");
        if (peer.Id == me.Id)
            throw new HandshakeException("connessione verso me stesso");

        byte[] secret;
        using (var peerKey = ECDiffieHellman.Create())
        {
            peerKey.ImportSubjectPublicKeyInfo(peer.PubKey, out _);
            secret = ecdh.DeriveRawSecretAgreement(peerKey.PublicKey);
        }

        var transcript = SHA256.HashData([.. clientRaw, .. serverRaw]);
        var prk = HKDF.Extract(HashAlgorithmName.SHA256, secret, me.GroupKey);
        var okm = HKDF.Expand(HashAlgorithmName.SHA256, prk, 128, [.. "varco v1 keys"u8, .. transcript]);
        CryptographicOperations.ZeroMemory(secret);

        var c2s = okm[..32];
        var s2c = okm[32..64];
        var clientFin = HMACSHA256.HashData(okm[64..96], transcript);
        var serverFin = HMACSHA256.HashData(okm[96..128], transcript);

        if (isClient)
        {
            var got = await Framing.ReadFrameAsync(s, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(got, serverFin))
                throw new HandshakeException("chiave di gruppo errata (verifica server fallita)");
            await Framing.WriteFrameAsync(s, clientFin, ct).ConfigureAwait(false);
            return new HandshakeResult(peer.Id, peer.Name, peer.Port, new FrameCipher(c2s, s2c), transcript);
        }
        else
        {
            await Framing.WriteFrameAsync(s, myHello, ct).ConfigureAwait(false);
            await Framing.WriteFrameAsync(s, serverFin, ct).ConfigureAwait(false);
            var got = await Framing.ReadFrameAsync(s, ct).ConfigureAwait(false);
            if (!CryptographicOperations.FixedTimeEquals(got, clientFin))
                throw new HandshakeException("chiave di gruppo errata (verifica client fallita)");
            return new HandshakeResult(peer.Id, peer.Name, peer.Port, new FrameCipher(s2c, c2s), transcript);
        }
    }

    private static byte[] BuildHello(LocalIdentity me, byte[] pub)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        w.Write(Magic);
        w.Write(Version);
        w.Write(me.GroupTag);
        w.Write(me.Id.ToByteArray());
        w.Write((ushort)me.ListenPort);
        w.Write(me.Name);
        w.Write((ushort)pub.Length);
        w.Write(pub);
        w.Write(RandomNumberGenerator.GetBytes(32));
        w.Flush();
        return ms.ToArray();
    }

    private static Hello ParseHello(byte[] raw)
    {
        try
        {
            using var r = new BinaryReader(new MemoryStream(raw), Encoding.UTF8);
            if (!r.ReadBytes(4).AsSpan().SequenceEqual(Magic)) throw new HandshakeException("protocollo sconosciuto");
            var ver = r.ReadByte();
            if (ver != Version) throw new HandshakeException($"versione protocollo {ver} non supportata");
            var tag = r.ReadBytes(8);
            var id = new Guid(r.ReadBytes(16));
            var port = r.ReadUInt16();
            var name = r.ReadString();
            var pub = r.ReadBytes(r.ReadUInt16());
            r.ReadBytes(32); // nonce: entra nel transcript tramite Raw
            return new Hello(raw, tag, id, port, name, pub);
        }
        catch (EndOfStreamException)
        {
            throw new HandshakeException("Hello troncato");
        }
    }
}
