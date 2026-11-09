using System.Text;
using System.Text.Json;
using Varco.Core.Input;

namespace Varco.Core.Protocol;

public enum MsgType : byte
{
    Ping = 1,
    Pong = 2,
    Layout = 4,
    MouseMove = 10,
    MouseButton = 11,
    MouseWheel = 12,
    Key = 13,
    Enter = 20,
    EdgeHit = 21,
    Leave = 22,
    TakeOver = 23,
}

public enum Edge : byte { Left = 0, Right = 1, Top = 2, Bottom = 3 }

public enum MouseButton : byte { Left = 0, Right = 1, Middle = 2, X1 = 3, X2 = 4 }

[Flags]
public enum KeyFlags : byte { None = 0, Up = 1, Extended = 2 }

public abstract record Message;

/// <summary>Heartbeat: il ricevente risponde con <see cref="Pong"/> con lo stesso timestamp (per misurare l'RTT).</summary>
public sealed record Ping(long Timestamp) : Message;
public sealed record Pong(long Timestamp) : Message;

/// <summary>Disposizione degli schermi condivisa da tutto il gruppo.</summary>
public sealed record LayoutMsg(Layout Layout) : Message;

/// <summary>Spostamento relativo (pixel fisici del PC che controlla).</summary>
public sealed record MouseMove(int Dx, int Dy) : Message;
public sealed record MouseButtonMsg(MouseButton Button, bool Down) : Message;
public sealed record MouseWheel(int Delta, bool Horizontal) : Message;
public sealed record KeyMsg(ushort Vk, ushort Scan, KeyFlags Flags) : Message;

/// <summary>Controller → target: "ora comandi tu", il cursore entra dal bordo <paramref name="From"/> alla posizione relativa <paramref name="Ratio"/> (0..1).</summary>
public sealed record Enter(Edge From, float Ratio) : Message;
/// <summary>Target → controller: il cursore ha toccato un bordo dello schermo del target.</summary>
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
}
