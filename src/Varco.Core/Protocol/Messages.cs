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
    InstallUpdate = 6,
    FileDialBack = 7,
    MouseMove = 10,
    MouseButton = 11,
    MouseWheel = 12,
    Key = 13,
    Enter = 20,
    EdgeHit = 21,
    Leave = 22,
    TakeOver = 23,
    LockKeys = 24,
    SecureAttention = 25,
    GroupKeyChange = 26,
    DragFiles = 27,
    LockScreen = 28,
    AudioActive = 29,
    ClipChunk = 30,
    MediaKey = 31,
    PointerScale = 32,
    AudioRouting = 33,
    AudioFrame = 34,
    PeerList = 40,
    RelayOpen = 41,
    RelayData = 42,
    RelayClose = 43,
}

public enum Edge : byte { Left = 0, Right = 1, Top = 2, Bottom = 3 }

public enum MouseButton : byte { Left = 0, Right = 1, Middle = 2, X1 = 3, X2 = 4 }

[Flags]
public enum KeyFlags : byte { None = 0, Up = 1, Extended = 2 }

public static class KeyCodes
{
    public const ushort VK_RSHIFT = 0xA1;
    public const ushort VK_CAPITAL = 0x14, VK_NUMLOCK = 0x90, VK_SCROLL = 0x91;

    /// <summary>Il tasto di blocco corrispondente al VK, o null se non è un tasto di blocco.</summary>
    public static LockKeys? LockKeyOf(ushort vk) => vk switch
    {
        VK_CAPITAL => LockKeys.Caps,
        VK_NUMLOCK => LockKeys.Num,
        VK_SCROLL => LockKeys.Scroll,
        _ => null,
    };

    /// <summary>
    /// Corregge i flag di un tasto prima di iniettarlo per scancode. L'hook di Windows segnala il Maiusc destro vero
    /// (VK_RSHIFT, scancode 0x36) come "esteso", ma iniettato come E0 36 diventa il Maiusc "finto" che le tastiere
    /// mandano attorno ai tasti di navigazione, e Windows lo ignora: sull'altro PC il Maiusc destro non faceva nulla.
    /// Solo con VK_RSHIFT: un Maiusc finto inoltrato (che non ha quel VK) resta finto.
    /// </summary>
    public static KeyFlags ForInjection(ushort vk, ushort scan, KeyFlags flags) =>
        vk == VK_RSHIFT && scan == 0x36 ? flags & ~KeyFlags.Extended : flags;

    public const ushort VK_PAUSE = 0x13;

    /// <summary>
    /// Se il tasto va iniettato per VK invece che per scancode. Bloc Num e Pausa hanno tutti e due lo scancode 45 e
    /// Windows li distingue solo dal tipo di sequenza (Pausa è E1 1D 45), che con SendInput non si può riprodurre.
    /// Misurato: Bloc Num come lo segnala l'hook (45 esteso) iniettato per scancode diventa VK FF, cioè niente, e
    /// Pausa (45 non esteso) diventa Bloc Num. Sul PC comandato Bloc Num non cambiava, il numpad dava frecce e
    /// Home/Fine invece dei numeri, e Pausa accendeva o spegneva Bloc Num. Questi due VK non dipendono dal layout.
    /// Anche i tasti senza scancode (multimediali) vanno per VK.
    /// </summary>
    public static bool InjectByVk(ushort vk, ushort scan) => scan == 0 || vk is VK_NUMLOCK or VK_PAUSE;

    public const ushort VK_LCONTROL = 0xA2;

    /// <summary>Muto, volume giù, volume su.</summary>
    public static bool IsVolumeKey(ushort vk) => vk is >= 0xAD and <= 0xAF;

    /// <summary>Tasti multimediali: muto, volume giù/su, brano successivo/precedente, stop, play/pausa.</summary>
    public static bool IsMediaKey(ushort vk) => vk is >= 0xAD and <= 0xB3;

    /// <summary>
    /// Il Ctrl sinistro "finto" che Windows aggiunge da sé attorno ad AltGr con i layout che lo hanno (italiano compreso):
    /// l'hook lo vede con VK_LCONTROL e scancode 0x21D. Se AltGr è stato iniettato da Varco, quel Ctrl arriva senza il
    /// marcatore di Varco (misurato): non va scambiato per un tasto premuto fisicamente.
    /// </summary>
    public static bool IsAltGrFakeCtrl(ushort vk, uint scan) => vk == VK_LCONTROL && (scan & 0x200) != 0;
}

/// <summary>Stato dei tasti di blocco (le spie della tastiera).</summary>
[Flags]
public enum LockKeys : byte { None = 0, Caps = 1, Num = 2, Scroll = 4 }

/// <summary>Funzioni che un PC sa gestire, annunciate appena connesso (le versioni senza questo messaggio non ne hanno).</summary>
[Flags]
public enum PeerCaps : uint { None = 0, Clipboard = 1, Files = 2, DragFiles = 4, DragData = 8 }

public abstract record Message;

/// <summary>Heartbeat: il ricevente risponde con <see cref="Pong"/> con lo stesso timestamp (per misurare l'RTT).</summary>
public sealed record Ping(long Timestamp) : Message;
public sealed record Pong(long Timestamp) : Message;

/// <summary>Disposizione degli schermi condivisa da tutto il gruppo.</summary>
public sealed record LayoutMsg(Layout Layout) : Message;

/// <summary>Mandato a ogni nuova connessione e quando cambiano le impostazioni. Le versioni vecchie lo ignorano.</summary>
public sealed record Capabilities(PeerCaps Caps) : Message;

/// <summary>
/// "Installa subito l'aggiornamento", mandato a tutti i PC collegati quando l'utente preme «Aggiorna subito tutti i PC».
/// Chi lo riceve installa (scaricandola se serve) l'ultima release firmata, senza aspettare che mouse e tastiera siano
/// liberi; se ha già la <paramref name="Version"/> o una più nuova non fa nulla. Le versioni vecchie lo ignorano.
/// </summary>
public sealed record InstallUpdate(string Version) : Message;

/// <summary>
/// Chi incolla → chi ha copiato i file: "non riesco a collegarmi a te, collegati tu" (vedi <see cref="Files.FileTransfer"/>).
/// Chi lo riceve apre una connessione per i file verso chi lo manda, presentandosi con <paramref name="Token"/>.
/// Le versioni vecchie lo ignorano (e chi incolla, dopo poco, segnala che non è raggiungibile).
/// </summary>
public sealed record FileDialBack(Guid Token) : Message;

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
/// <summary>
/// Controller → target, subito dopo <see cref="Enter"/>: lo stato di Bloc Maiusc/Num/Scorr del PC con la tastiera fisica,
/// che il target copia. Così le spie della tastiera valgono anche per il PC comandato. Le versioni vecchie lo ignorano.
/// </summary>
public sealed record LockKeysMsg(LockKeys State) : Message;

/// <summary>
/// Controller → target: "premi Ctrl+Alt+Canc". Il vero Ctrl+Alt+Canc non si può intercettare (agisce sempre sul PC
/// fisico): sul PC che comanda si usa Ctrl+Alt+Fine, come in Desktop remoto, e il target lo esegue con SendSAS.
/// Le versioni vecchie lo ignorano.
/// </summary>
public sealed record SecureAttention() : Message;

/// <summary>
/// Nuova chiave di gruppo (dopo aver rimosso un PC). Mandata a tutti i PC collegati, e a chi si collega più tardi con
/// una chiave sostituita. <paramref name="ChangedUtcTicks"/> decide fra due cambi fatti in parallelo; <paramref name="Removed"/>
/// sono i PC rimossi, che chi riceve toglie dai propri PC noti. Le versioni vecchie lo ignorano.
/// </summary>
public sealed record GroupKeyChange(byte[] Key, long ChangedUtcTicks, Guid[] Removed) : Message;

/// <summary>
/// Controller → target, subito dopo <see cref="Enter"/>: l'utente sta trascinando dei file da questo PC e ha appena
/// attraversato il bordo con il pulsante premuto. <paramref name="Offer"/> è un <see cref="Files.FileOffer"/> serializzato:
/// il target fa partire il trascinamento lì, e i dati arrivano quando l'utente rilascia sopra una cartella.
/// </summary>
public sealed record DragFiles(byte[] Offer) : Message;

/// <summary>
/// "Blocca lo schermo": mandato a tutti i PC collegati quando l'utente blocca il PC che ha davanti (Win+L, Start →
/// Blocca). Chi lo riceve si blocca, se ha il blocco di gruppo attivo. Le versioni vecchie lo ignorano.
/// </summary>
public sealed record LockScreen() : Message;

/// <summary>
/// Esce del suono da chi lo manda (musica, video)? Mandato appena connesso e a ogni cambio: serve a mandare i tasti
/// multimediali al PC che suona (vedi <see cref="Input.MediaKeyRouter"/>). Chi lo manda sa anche ricevere
/// <see cref="MediaKeyMsg"/>. Le versioni vecchie lo ignorano.
/// </summary>
public sealed record AudioActive(bool Playing) : Message;

/// <summary>
/// Un tasto multimediale (volume, play/pausa, brano successivo...) premuto su un altro PC, da eseguire qui anche se
/// questo PC non è comandato: qui sta suonando la musica. Solo verso i PC che hanno mandato <see cref="AudioActive"/>.
/// </summary>
public sealed record MediaKeyMsg(ushort Vk, ushort Scan, KeyFlags Flags) : Message;

/// <summary>
/// Controller → target, subito dopo <see cref="Enter"/>: la scala (DPI, 96 = 100%) del monitor da cui è partito il
/// cursore. Con un valore diverso da 0 il target moltiplica gli spostamenti per il rapporto fra la scala del suo monitor
/// e questa, così lo stesso gesto attraversa la stessa porzione di finestre su tutti e due. 0 = spostamenti al pixel,
/// come sempre. Le versioni vecchie lo ignorano.
/// </summary>
public sealed record PointerScale(ushort Dpi) : Message;

/// <summary>
/// Audio fra i PC, impostazione di tutto il gruppo: <paramref name="Hub"/> è il PC centrale che riceve l'audio
/// (<see cref="Guid.Empty"/> = funzione spenta), <paramref name="Sources"/> i PC che gli mandano il loro.
/// Mandato appena connesso e a ogni cambio; vince il cambio più recente (<paramref name="ChangedUtcTicks"/>).
/// Le versioni vecchie lo ignorano.
/// </summary>
public sealed record AudioRoutingMsg(Guid Hub, Guid[] Sources, long ChangedUtcTicks) : Message
{
    public bool Equals(AudioRoutingMsg? other) =>
        other is not null && Hub == other.Hub && ChangedUtcTicks == other.ChangedUtcTicks && Sources.SequenceEqual(other.Sources);
    public override int GetHashCode() => HashCode.Combine(Hub, ChangedUtcTicks, Sources.Length);
}

/// <summary>
/// 20 ms dell'audio di chi lo manda, compressi con Opus (48 kHz stereo), verso il PC centrale. <paramref name="Seq"/>
/// conta i pacchetti: un salto vuol dire pacchetti scartati da chi manda perché la rete non stava al passo.
/// Viaggia in una coda a parte che tiene solo gli ultimi pacchetti (vedi <see cref="Net.PeerConnection"/>).
/// </summary>
public sealed record AudioFrame(ushort Seq, byte[] Data) : Message;

/// <summary>
/// I PC con cui chi lo manda è collegato direttamente (senza inoltro). Mandato appena connesso e quando cambiano: dice
/// tramite chi si può raggiungere un PC che non si vede direttamente (vedi <see cref="Net.RelayTunnel"/>).
/// Chi lo manda sa inoltrare. Le versioni vecchie lo ignorano.
/// </summary>
public sealed record PeerList(PeerRef[] Connected) : Message;

/// <summary>Un PC del gruppo in <see cref="PeerList"/>: chi riceve l'elenco impara anche i PC che non ha mai visto.</summary>
public sealed record PeerRef(Guid Id, string Name);

/// <summary>
/// Apre un tunnel di inoltro. Da chi vuole collegarsi al PC che fa da tramite (<paramref name="Forwarded"/> = false):
/// <paramref name="Peer"/> è il PC da raggiungere. Dal tramite al PC raggiunto (<paramref name="Forwarded"/> = true):
/// <paramref name="Peer"/> è chi vuole collegarsi.
/// </summary>
public sealed record RelayOpen(Guid Tunnel, Guid Peer, bool Forwarded) : Message;

/// <summary>Byte di un tunnel di inoltro: l'handshake e i frame cifrati fra i due PC alle estremità. Il tramite non li legge.</summary>
public sealed record RelayData(Guid Tunnel, byte[] Data) : Message;

/// <summary>Tunnel di inoltro chiuso (o rifiutato dal tramite).</summary>
public sealed record RelayClose(Guid Tunnel) : Message;

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
            case InstallUpdate u: w.Write((byte)MsgType.InstallUpdate); w.Write(u.Version); break;
            case FileDialBack f: w.Write((byte)MsgType.FileDialBack); w.Write(f.Token.ToByteArray()); break;
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
            case LockKeysMsg l: w.Write((byte)MsgType.LockKeys); w.Write((byte)l.State); break;
            case SecureAttention: w.Write((byte)MsgType.SecureAttention); break;
            case GroupKeyChange g:
                w.Write((byte)MsgType.GroupKeyChange); w.Write((byte)g.Key.Length); w.Write(g.Key); w.Write(g.ChangedUtcTicks);
                w.Write((ushort)g.Removed.Length);
                foreach (var id in g.Removed) w.Write(id.ToByteArray());
                break;
            case DragFiles d: w.Write((byte)MsgType.DragFiles); w.Write(d.Offer.Length); w.Write(d.Offer); break;
            case LockScreen: w.Write((byte)MsgType.LockScreen); break;
            case AudioActive a: w.Write((byte)MsgType.AudioActive); w.Write(a.Playing); break;
            case MediaKeyMsg k: w.Write((byte)MsgType.MediaKey); w.Write(k.Vk); w.Write(k.Scan); w.Write((byte)k.Flags); break;
            case PointerScale p: w.Write((byte)MsgType.PointerScale); w.Write(p.Dpi); break;
            case AudioRoutingMsg a:
                w.Write((byte)MsgType.AudioRouting); w.Write(a.Hub.ToByteArray()); w.Write(a.ChangedUtcTicks);
                w.Write((ushort)a.Sources.Length);
                foreach (var id in a.Sources) w.Write(id.ToByteArray());
                break;
            case AudioFrame f: w.Write((byte)MsgType.AudioFrame); w.Write(f.Seq); w.Write((ushort)f.Data.Length); w.Write(f.Data); break;
            case PeerList l:
                w.Write((byte)MsgType.PeerList); w.Write((ushort)l.Connected.Length);
                foreach (var p in l.Connected) { w.Write(p.Id.ToByteArray()); w.Write(p.Name); }
                break;
            case RelayOpen o: w.Write((byte)MsgType.RelayOpen); w.Write(o.Tunnel.ToByteArray()); w.Write(o.Peer.ToByteArray()); w.Write(o.Forwarded); break;
            case RelayData d: w.Write((byte)MsgType.RelayData); w.Write(d.Tunnel.ToByteArray()); w.Write(d.Data.Length); w.Write(d.Data); break;
            case RelayClose c: w.Write((byte)MsgType.RelayClose); w.Write(c.Tunnel.ToByteArray()); break;
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
            MsgType.InstallUpdate => new InstallUpdate(r.ReadString()),
            MsgType.FileDialBack => new FileDialBack(new Guid(r.ReadBytes(16))),
            MsgType.ClipChunk => ReadClipChunk(r),
            MsgType.MouseMove => new MouseMove(r.ReadInt32(), r.ReadInt32()),
            MsgType.MouseButton => new MouseButtonMsg((MouseButton)r.ReadByte(), r.ReadBoolean()),
            MsgType.MouseWheel => new MouseWheel(r.ReadInt32(), r.ReadBoolean()),
            MsgType.Key => new KeyMsg(r.ReadUInt16(), r.ReadUInt16(), (KeyFlags)r.ReadByte()),
            MsgType.Enter => new Enter((Edge)r.ReadByte(), r.ReadSingle()),
            MsgType.EdgeHit => new EdgeHit((Edge)r.ReadByte(), r.ReadSingle()),
            MsgType.Leave => new Leave(),
            MsgType.TakeOver => new TakeOver(),
            MsgType.LockKeys => new LockKeysMsg((LockKeys)r.ReadByte()),
            MsgType.SecureAttention => new SecureAttention(),
            MsgType.GroupKeyChange => ReadGroupKeyChange(r),
            MsgType.DragFiles => new DragFiles(ReadBlob(r)),
            MsgType.LockScreen => new LockScreen(),
            MsgType.AudioActive => new AudioActive(r.ReadBoolean()),
            MsgType.MediaKey => new MediaKeyMsg(r.ReadUInt16(), r.ReadUInt16(), (KeyFlags)r.ReadByte()),
            MsgType.PointerScale => new PointerScale(r.ReadUInt16()),
            MsgType.AudioRouting => ReadAudioRouting(r),
            MsgType.AudioFrame => ReadAudioFrame(r),
            MsgType.PeerList => ReadPeerList(r),
            MsgType.RelayOpen => new RelayOpen(new Guid(r.ReadBytes(16)), new Guid(r.ReadBytes(16)), r.ReadBoolean()),
            MsgType.RelayData => new RelayData(new Guid(r.ReadBytes(16)), ReadBlob(r)),
            MsgType.RelayClose => new RelayClose(new Guid(r.ReadBytes(16))),
            _ => null,
        };
    }

    private static GroupKeyChange ReadGroupKeyChange(BinaryReader r)
    {
        var key = r.ReadBytes(r.ReadByte());
        var ticks = r.ReadInt64();
        var removed = new Guid[r.ReadUInt16()];
        for (var i = 0; i < removed.Length; i++) removed[i] = new Guid(r.ReadBytes(16));
        return new GroupKeyChange(key, ticks, removed);
    }

    private static AudioRoutingMsg ReadAudioRouting(BinaryReader r)
    {
        var hub = new Guid(r.ReadBytes(16));
        var ticks = r.ReadInt64();
        var sources = new Guid[r.ReadUInt16()];
        for (var i = 0; i < sources.Length; i++) sources[i] = new Guid(r.ReadBytes(16));
        return new AudioRoutingMsg(hub, sources, ticks);
    }

    private static AudioFrame ReadAudioFrame(BinaryReader r)
    {
        var seq = r.ReadUInt16();
        var len = r.ReadUInt16();
        if (len > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("AudioFrame troncato");
        return new AudioFrame(seq, r.ReadBytes(len));
    }

    private static PeerList ReadPeerList(BinaryReader r)
    {
        var peers = new PeerRef[r.ReadUInt16()];
        for (var i = 0; i < peers.Length; i++) peers[i] = new PeerRef(new Guid(r.ReadBytes(16)), r.ReadString());
        return new PeerList(peers);
    }

    private static byte[] ReadBlob(BinaryReader r)
    {
        var len = r.ReadInt32();
        if (len < 0 || len > r.BaseStream.Length - r.BaseStream.Position) throw new InvalidDataException("Messaggio troncato");
        return r.ReadBytes(len);
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
