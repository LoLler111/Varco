using System.Text;
using System.Text.Json;
using Varco.Core.Input;

namespace Varco.Core.Protocol;

public enum MsgType : byte
{
    Ping = 1,
    Pong = 2,
    Layout = 4,
    Capabilities = 5,
    MouseMove = 10,
    MouseButton = 11,
    MouseWheel = 12,
    Key = 13,
    Enter = 20,
    EdgeHit = 21,
    Leave = 22,
    TakeOver = 23,
    ClipChunk = 30,
}

public enum Edge : byte { Left = 0, Right = 1, Top = 2, Bottom = 3 }

public enum MouseButton : byte { Left = 0, Right = 1, Middle = 2, X1 = 3, X2 = 4 }

[Flags]
public enum KeyFlags : byte { None = 0, Up = 1, Extended = 2 }

public static class KeyCodes
{
    public const ushort VK_RSHIFT = 0xA1;

    /// <summary>
    /// Corregge i flag di un tasto prima di iniettarlo per scancode. L'hook di Windows segnala il Maiusc destro vero
    /// (VK_RSHIFT, scancode 0x36) come "esteso", ma iniettato come E0 36 diventa il Maiusc "finto" che le tastiere
    /// mandano attorno ai tasti di navigazione, e Windows lo ignora: sull'altro PC il Maiusc destro non faceva nulla.
    /// Solo con VK_RSHIFT: un Maiusc finto inoltrato (che non ha quel VK) resta finto.
    /// </summary>
    public static KeyFlags ForInjection(ushort vk, ushort scan, KeyFlags flags) =>
        vk == VK_RSHIFT && scan == 0x36 ? flags & ~KeyFlags.Extended : flags;
}

/// <summary>Funzioni che un PC sa gestire, annunciate appena connesso (le versioni senza questo messaggio non ne hanno).</summary>
[Flags]
public enum PeerCaps : uint { None = 0, Clipboard = 1 }

public abstract record Message;

/// <summary>Heartbeat: il ricevente risponde con <see cref="Pong"/> con lo stesso timestamp (per misurare l'RTT).</summary>
public sealed record Ping(long Timestamp) : Message;
public sealed record Pong(long Timestamp) : Message;

/// <summary>Disposizione degli schermi condivisa da tutto il gruppo.</summary>
public sealed record LayoutMsg(Layout Layout) : Message;

/// <summary>Mandato a ogni nuova connessione e quando cambiano le impostazioni. Le versioni vecchie lo ignorano.</summary>
public sealed record Capabilities(PeerCaps Caps) : Message;

/// <summary>
/// Un pezzo degli appunti compressi (<paramref name="Total"/> byte in tutto), in ordine di <paramref name="Offset"/>.
/// Viaggia in coda a bassa priorità: mouse e tastiera passano davanti.
/// </summary>
public sealed record ClipChunk(uint ClipId, int Total, int Offset, byte[] Data) : Message;

/// <summary>Spostamento relativo (pixel fisici del PC che controlla).</summary>
public sealed record MouseMove(int Dx, int Dy) : Message;
public sealed record MouseButtonMsg(MouseButton Button, bool Down) : Message;
public sealed record MouseWheel(int Delta, bool Horizontal) : Message;
public sealed record KeyMsg(ushort Vk, ushort Scan, KeyFlags Flags) : Message;

/// <summary>
/// Controller → target: "ora comandi tu", il cursore entra dal bordo <paramref name="From"/> alla posizione <paramref name="Ratio"/>
/// (0..1 lungo il bordo del desktop del target). Il controller la ricava dalla disposizione 2D (<see cref="Layout.Cross"/>),
/// così il cursore passa alla stessa altezza anche fra PC con risoluzioni e posizioni diverse.
/// </summary>
public sealed record Enter(Edge From, float Ratio) : Message;
/// <summary>Target → controller: il cursore ha toccato un bordo dello schermo del target, alla posizione <paramref name="Ratio"/> (0..1 lungo quel bordo).</summary>
public sealed record EdgeHit(Edge Edge, float Ratio) : Message;
/// <summary>Controller → target: non sei più controllato, rilascia tutto.</summary>
public sealed record Leave() : Message;
/// <summary>Target → controller: qualcuno ha usato mouse/tastiera fisici del target, riprendo il controllo.</summary>
public sealed record TakeOver() : Message;

public static class MessageCodec
{
    private static readonly JsonSerializerOptions Json = new();

    public static byte[] Encode(Message m)
    {
        using var ms = new MemoryStream(16);
        using var w = new BinaryWriter(ms, Encoding.UTF8);
        switch (m)
        {
            case Ping p: w.Write((byte)MsgType.Ping); w.Write(p.Timestamp); break;
            case Pong p: w.Write((byte)MsgType.Pong); w.Write(p.Timestamp); break;
            case LayoutMsg l:
                w.Write((byte)MsgType.Layout);
                w.Write(JsonSerializer.Serialize(l.Layout, Json));
                break;
            case Capabilities c: w.Write((byte)MsgType.Capabilities); w.Write((uint)c.Caps); break;
            case ClipChunk c:
                w.Write((byte)MsgType.ClipChunk); w.Write(c.ClipId); w.Write(c.Total); w.Write(c.Offset);
                w.Write(c.Data.Length); w.Write(c.Data);
                break;
            case MouseMove mm: w.Write((byte)MsgType.MouseMove); w.Write(mm.Dx); w.Write(mm.Dy); break;
            case MouseButtonMsg b: w.Write((byte)MsgType.MouseButton); w.Write((byte)b.Button); w.Write(b.Down); break;
            case MouseWheel wh: w.Write((byte)MsgType.MouseWheel); w.Write(wh.Delta); w.Write(wh.Horizontal); break;
            case KeyMsg k: w.Write((byte)MsgType.Key); w.Write(k.Vk); w.Write(k.Scan); w.Write((byte)k.Flags); break;
            case Enter e: w.Write((byte)MsgType.Enter); w.Write((byte)e.From); w.Write(e.Ratio); break;
            case EdgeHit e: w.Write((byte)MsgType.EdgeHit); w.Write((byte)e.Edge); w.Write(e.Ratio); break;
            case Leave: w.Write((byte)MsgType.Leave); break;
            case TakeOver: w.Write((byte)MsgType.TakeOver); break;
            default: throw new ArgumentException($"Messaggio non supportato: {m.GetType().Name}");
        }
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>Decodifica; restituisce null per tipi sconosciuti (compatibilità con versioni future).</summary>
    public static Message? Decode(byte[] data)
    {
        using var r = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        var type = (MsgType)r.ReadByte();
        return type switch
        {
            MsgType.Ping => new Ping(r.ReadInt64()),
            MsgType.Pong => new Pong(r.ReadInt64()),
            MsgType.Layout => new LayoutMsg(JsonSerializer.Deserialize<Layout>(r.ReadString(), Json) ?? new Layout()),
            MsgType.Capabilities => new Capabilities((PeerCaps)r.ReadUInt32()),
            MsgType.ClipChunk => ReadClipChunk(r),
            MsgType.MouseMove => new MouseMove(r.ReadInt32(), r.ReadInt32()),
            MsgType.MouseButton => new MouseButtonMsg((MouseButton)r.ReadByte(), r.ReadBoolean()),
            MsgType.MouseWheel => new MouseWheel(r.ReadInt32(), r.ReadBoolean()),
            MsgType.Key => new KeyMsg(r.ReadUInt16(), r.ReadUInt16(), (KeyFlags)r.ReadByte()),
            MsgType.Enter => new Enter((Edge)r.ReadByte(), r.ReadSingle()),
            MsgType.EdgeHit => new EdgeHit((Edge)r.ReadByte(), r.ReadSingle()),
            MsgType.Leave => new Leave(),
            MsgType.TakeOver => new TakeOver(),
            _ => null,
        };
    }

    private static ClipChunk ReadClipChunk(BinaryReader r)
    {
        var id = r.ReadUInt32();
        var total = r.ReadInt32();
        var offset = r.ReadInt32();
        var len = r.ReadInt32();
        if (len < 0 || len > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("ClipChunk troncato");
        return new ClipChunk(id, total, offset, r.ReadBytes(len));
    }
}
