using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Varco.Core;
using Varco.Core.Control;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace Varco.App;

/// <summary>
/// File copiati su un altro PC → negli appunti di questo come "file virtuali" (come fa Desktop remoto).
/// Esplora risorse incolla leggendo FileGroupDescriptorW (nomi, dimensioni) e FileContents (un IStream per file):
/// i dati vengono chiesti all'agent, e da lì all'altro PC, solo in quel momento, con la barra di avanzamento di Windows.
///
/// Gira nell'icona perché Esplora risorse deve poter chiamare l'oggetto COM: l'agent è SYSTEM, l'icona è l'utente.
/// L'oggetto vive su un thread STA dedicato: le letture bloccanti non congelano il menu dell'icona.
/// </summary>
internal sealed class VirtualFileClipboard : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Thread _sta;
    private readonly ManualResetEventSlim _ready = new();
    private Control? _invoker;
    private VirtualFilesDataObject? _current;

    public VirtualFileClipboard()
    {
        _sta = new Thread(RunSta) { IsBackground = true, Name = "Varco file virtuali" };
        _sta.SetApartmentState(ApartmentState.STA);
        _sta.Start();
        _ready.Wait(3000);
        _ = Task.Run(PollLoop);
    }

    private void RunSta()
    {
        Application.OleRequired(); // OleInitialize: gli appunti OLE non bastano con il solo CoInitialize
        _invoker = new Control();
        _ = _invoker.Handle; // crea la finestra (e la coda messaggi) su questo thread
        _ready.Set();
        Application.Run();
    }

    /// <summary>Attesa lunga sull'agent: appena un altro PC copia dei file, l'offerta arriva qui.</summary>
    private async Task PollLoop()
    {
        long? seq = null;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                var r = await ControlClient.SendAsync(new ControlRequest { Cmd = "filesOffer", Seq = seq }, 35_000).ConfigureAwait(false);
                if (r.FilesOffer is { } offer)
                {
                    // Al primo giro si prende solo il numero: un'offerta vecchia non deve sovrascrivere gli appunti all'avvio.
                    if (seq is not null && offer.Seq > seq && offer.Entries.Count > 0) Publish(offer);
                    seq = offer.Seq;
                }
            }
            catch (Exception ex) when (!_cts.IsCancellationRequested)
            {
                Log.Debug($"Offerte di file: {ex.Message}");
                try { await Task.Delay(3000, _cts.Token).ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            catch (OperationCanceledException) { }
        }
    }

    private void Publish(FilesOfferDto offer)
    {
        if (_invoker is not { IsHandleCreated: true } inv) return;
        inv.BeginInvoke(() =>
        {
            var obj = new VirtualFilesDataObject(offer);
            var hr = 0;
            for (var i = 0; i < 10; i++)
            {
                hr = OleSetClipboard(obj);
                if (hr == 0)
                {
                    _current = obj;
                    Log.Info($"Negli appunti {obj.Count} elementi copiati su {offer.PeerName}: si possono incollare");
                    return;
                }
                Thread.Sleep(50); // appunti aperti da un altro programma
            }
            Log.Warn($"Impossibile mettere negli appunti i file arrivati dall'altro PC (0x{hr:X8})");
        });
    }

    public void Dispose()
    {
        _cts.Cancel();
        if (_invoker is { IsHandleCreated: true } inv)
        {
            try
            {
                inv.Invoke(() =>
                {
                    // Mai OleFlushClipboard: scaricherebbe tutti i file. Se gli appunti sono ancora nostri, si svuotano.
                    if (_current is not null && OleIsCurrentClipboard(_current) == 0) OleSetClipboard(null);
                    Application.ExitThread();
                });
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException) { }
        }
    }

    [DllImport("ole32.dll")]
    private static extern int OleSetClipboard(ComTypes.IDataObject? obj);
    [DllImport("ole32.dll")]
    private static extern int OleIsCurrentClipboard(ComTypes.IDataObject obj);
}

/// <summary>Oggetto appunti con FileGroupDescriptorW + FileContents (IStream) + Preferred DropEffect = copia.</summary>
[ComVisible(true)]
public sealed class VirtualFilesDataObject : ComTypes.IDataObject
{
    private const int DV_E_FORMATETC = unchecked((int)0x80040064);
    private const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    private const int DATA_S_SAMEFORMATETC = 0x00040130;
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const uint FD_ATTRIBUTES = 0x4, FD_WRITESTIME = 0x20, FD_FILESIZE = 0x40, FD_PROGRESSUI = 0x4000, FD_UNICODE = 0x80000000;
    private const int DescriptorSize = 592;
    private const FileAttributes KeptAttributes = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System |
                                                  FileAttributes.Directory | FileAttributes.Archive;

    private static readonly short CfDescriptor = (short)DataFormats.GetFormat("FileGroupDescriptorW").Id;
    private static readonly short CfContents = (short)DataFormats.GetFormat("FileContents").Id;
    private static readonly short CfDropEffect = (short)DataFormats.GetFormat("Preferred DropEffect").Id;

    private readonly FilesOfferDto _offer;
    /// <summary>Indice nel descrittore → indice nell'offerta (i nomi troppo lunghi per Esplora risorse restano fuori).</summary>
    private readonly int[] _map;

    internal int Count => _map.Length;

    internal VirtualFilesDataObject(FilesOfferDto offer)
    {
        _offer = offer;
        var map = new List<int>();
        for (var i = 0; i < offer.Entries.Count; i++)
        {
            if (offer.Entries[i].Path.Length < 260) map.Add(i);
            else Log.Warn($"Nome troppo lungo per incollarlo, saltato: {offer.Entries[i].Path}");
        }
        _map = map.ToArray();
    }

    private static ComTypes.FORMATETC Format(short cf, ComTypes.TYMED tymed) => new()
    {
        cfFormat = cf,
        dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
        lindex = -1,
        tymed = tymed,
    };

    private static readonly ComTypes.FORMATETC[] Formats =
    [
        Format(CfDescriptor, ComTypes.TYMED.TYMED_HGLOBAL),
        Format(CfContents, ComTypes.TYMED.TYMED_ISTREAM),
        Format(CfDropEffect, ComTypes.TYMED.TYMED_HGLOBAL),
    ];

    public void GetData(ref ComTypes.FORMATETC format, out ComTypes.STGMEDIUM medium)
    {
        medium = default;
        if (format.cfFormat == CfDescriptor && format.tymed.HasFlag(ComTypes.TYMED.TYMED_HGLOBAL))
        {
            medium = HGlobal(BuildDescriptor());
            return;
        }
        if (format.cfFormat == CfDropEffect && format.tymed.HasFlag(ComTypes.TYMED.TYMED_HGLOBAL))
        {
            medium = HGlobal(BitConverter.GetBytes(1)); // DROPEFFECT_COPY: i file sull'altro PC non si toccano mai
            return;
        }
        if (format.cfFormat == CfContents && format.tymed.HasFlag(ComTypes.TYMED.TYMED_ISTREAM)
            && format.lindex >= 0 && format.lindex < _map.Length)
        {
            var index = _map[format.lindex];
            var entry = _offer.Entries[index];
            if (entry.IsDirectory) throw new COMException("È una cartella", DV_E_FORMATETC);
            var stream = new PipeFileStream(_offer.PeerId, _offer.OfferId, index, entry);
            medium = new ComTypes.STGMEDIUM
            {
                tymed = ComTypes.TYMED.TYMED_ISTREAM,
                unionmember = Marshal.GetComInterfaceForObject(stream, typeof(ComTypes.IStream)),
            };
            return;
        }
        throw new COMException("Formato non disponibile", DV_E_FORMATETC);
    }

    public int QueryGetData(ref ComTypes.FORMATETC format)
    {
        foreach (var f in Formats)
            if (f.cfFormat == format.cfFormat && (f.tymed & format.tymed) != 0) return 0;
        return DV_E_FORMATETC;
    }

    public ComTypes.IEnumFORMATETC EnumFormatEtc(ComTypes.DATADIR direction)
    {
        if (direction != ComTypes.DATADIR.DATADIR_GET) throw new COMException("Solo lettura", E_NOTIMPL);
        Marshal.ThrowExceptionForHR(SHCreateStdEnumFmtEtc((uint)Formats.Length, Formats, out var e));
        return e;
    }

    public int GetCanonicalFormatEtc(ref ComTypes.FORMATETC formatIn, out ComTypes.FORMATETC formatOut)
    {
        formatOut = formatIn;
        formatOut.ptd = IntPtr.Zero;
        return DATA_S_SAMEFORMATETC;
    }

    public void GetDataHere(ref ComTypes.FORMATETC format, ref ComTypes.STGMEDIUM medium) =>
        throw new COMException("Non supportato", E_NOTIMPL);

    /// <summary>Esplora risorse ci comunica l'esito ("Performed DropEffect", "Paste Succeeded"…): basta liberare la memoria.</summary>
    public void SetData(ref ComTypes.FORMATETC formatIn, ref ComTypes.STGMEDIUM medium, bool release)
    {
        if (release) ReleaseStgMedium(ref medium);
    }

    public int DAdvise(ref ComTypes.FORMATETC pFormatetc, ComTypes.ADVF advf, ComTypes.IAdviseSink adviseSink, out int connection)
    {
        connection = 0;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    public void DUnadvise(int connection) => throw new COMException("Non supportato", OLE_E_ADVISENOTSUPPORTED);

    public int EnumDAdvise(out ComTypes.IEnumSTATDATA enumAdvise)
    {
        enumAdvise = null!;
        return OLE_E_ADVISENOTSUPPORTED;
    }

    /// <summary>FILEGROUPDESCRIPTORW: numero di elementi + un FILEDESCRIPTORW (592 byte) per ognuno.</summary>
    private byte[] BuildDescriptor()
    {
        var buf = new byte[4 + _map.Length * DescriptorSize];
        BitConverter.TryWriteBytes(buf.AsSpan(0), (uint)_map.Length);
        for (var i = 0; i < _map.Length; i++)
        {
            var e = _offer.Entries[_map[i]];
            var d = buf.AsSpan(4 + i * DescriptorSize, DescriptorSize);
            BitConverter.TryWriteBytes(d[0..], FD_ATTRIBUTES | FD_WRITESTIME | FD_FILESIZE | FD_PROGRESSUI | FD_UNICODE);
            var attrs = ((FileAttributes)e.Attributes & KeptAttributes) | (e.IsDirectory ? FileAttributes.Directory : 0);
            if (attrs == 0) attrs = FileAttributes.Normal;
            BitConverter.TryWriteBytes(d[36..], (uint)attrs);
            BitConverter.TryWriteBytes(d[56..], e.LastWriteUtc.Ticks > new DateTime(1601, 1, 2).Ticks ? e.LastWriteUtc.ToFileTimeUtc() : 0L);
            BitConverter.TryWriteBytes(d[64..], (uint)(e.Size >> 32));
            BitConverter.TryWriteBytes(d[68..], (uint)(e.Size & 0xFFFFFFFF));
            Encoding.Unicode.GetBytes(e.Path, d[72..]);
        }
        return buf;
    }

    private static ComTypes.STGMEDIUM HGlobal(byte[] data)
    {
        var h = GlobalAlloc(0x0002 /* GMEM_MOVEABLE */, (UIntPtr)Math.Max(1, data.Length));
        if (h == IntPtr.Zero) throw new OutOfMemoryException();
        var p = GlobalLock(h);
        Marshal.Copy(data, 0, p, data.Length);
        GlobalUnlock(h);
        return new ComTypes.STGMEDIUM { tymed = ComTypes.TYMED.TYMED_HGLOBAL, unionmember = h };
    }

    [DllImport("shell32.dll")]
    private static extern int SHCreateStdEnumFmtEtc(uint cfmt, [In] ComTypes.FORMATETC[] afmt, out ComTypes.IEnumFORMATETC ppenumFormatEtc);
    [DllImport("ole32.dll")]
    private static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM medium);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr h);
}

/// <summary>
/// Il contenuto di un file remoto per Esplora risorse: legge dalla pipe dei file dell'agent.
/// Supporta lo spostamento (Seek) riaprendo dalla nuova posizione, così una copia ripresa non riparte da zero.
/// </summary>
[ComVisible(true)]
public sealed class PipeFileStream : ComTypes.IStream
{
    private const int STG_E_READFAULT = unchecked((int)0x8003001E);
    private const int STG_E_INVALIDFUNCTION = unchecked((int)0x80030001);

    private readonly Guid _peer, _offer;
    private readonly int _index;
    private readonly FileEntryDto _entry;
    private NamedPipeClientStream? _pipe;
    private long _position;
    private long _end;

    internal PipeFileStream(Guid peer, Guid offer, int index, FileEntryDto entry)
    {
        _peer = peer;
        _offer = offer;
        _index = index;
        _entry = entry;
    }

    public void Read(byte[] pv, int cb, IntPtr pcbRead)
    {
        var total = 0;
        try
        {
            while (total < cb && _position < _entry.Size)
            {
                var pipe = _pipe ?? Open();
                var n = pipe.Read(pv, total, (int)Math.Min(cb - total, _end - _position));
                if (n == 0) throw new IOException("trasferimento interrotto");
                total += n;
                _position += n;
            }
            if (_position >= _entry.Size) Close(); // finito: la connessione con l'altro PC si chiude subito
        }
        catch (IOException ex)
        {
            Close();
            Log.Warn($"Copia di {_entry.Path} non riuscita: {ex.Message}");
            throw new COMException(ex.Message, STG_E_READFAULT);
        }
        catch (TimeoutException ex)
        {
            Close();
            throw new COMException(ex.Message, STG_E_READFAULT);
        }
        if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, total);
    }

    private NamedPipeClientStream Open()
    {
        var pipe = new NamedPipeClientStream(".", ControlProtocol.FilesPipeName, PipeDirection.InOut);
        try
        {
            pipe.Connect(5000);
            var req = JsonSerializer.Serialize(new FileReadRequest { PeerId = _peer, OfferId = _offer, Index = _index, Offset = _position }, ControlProtocol.Json);
            pipe.Write(Encoding.UTF8.GetBytes(req + "\n"));
            pipe.Flush();
            var header = new byte[8];
            pipe.ReadExactly(header);
            var length = BitConverter.ToInt64(header);
            if (length < 0)
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8);
                throw new IOException(reader.ReadLine() ?? "file non disponibile");
            }
            _end = _position + length;
            if (_position == 0) Log.Info($"Incollo {_entry.Path} ({_entry.Size / 1024} KB) dall'altro PC");
            _pipe = pipe;
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private void Close()
    {
        _pipe?.Dispose();
        _pipe = null;
    }

    public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
    {
        var target = dwOrigin switch
        {
            0 => dlibMove,
            1 => _position + dlibMove,
            2 => _entry.Size + dlibMove,
            _ => throw new COMException("Origine non valida", STG_E_INVALIDFUNCTION),
        };
        if (target < 0 || target > _entry.Size) throw new COMException("Posizione non valida", STG_E_INVALIDFUNCTION);
        if (target != _position)
        {
            Close(); // alla prossima lettura si riapre da qui
            _position = target;
        }
        if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, _position);
    }

    public void Stat(out ComTypes.STATSTG pstatstg, int grfStatFlag)
    {
        var ft = _entry.LastWriteUtc.Ticks > new DateTime(1601, 1, 2).Ticks ? _entry.LastWriteUtc.ToFileTimeUtc() : 0L;
        pstatstg = new ComTypes.STATSTG
        {
            type = 2, // STGTY_STREAM
            cbSize = _entry.Size,
            mtime = new ComTypes.FILETIME { dwLowDateTime = (int)(ft & 0xFFFFFFFF), dwHighDateTime = (int)(ft >> 32) },
            pwcsName = (grfStatFlag & 1) != 0 ? null! : Path.GetFileName(_entry.Path),
        };
    }

    public void Commit(int grfCommitFlags) { }
    public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new NotImplementedException();
    public void SetSize(long libNewSize) => throw new NotImplementedException();
    public void CopyTo(ComTypes.IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new NotImplementedException();
    public void Revert() => throw new NotImplementedException();
    public void LockRegion(long libOffset, long cb, int dwLockType) => throw new NotImplementedException();
    public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new NotImplementedException();
    public void Clone(out ComTypes.IStream ppstm) => throw new NotImplementedException();

    ~PipeFileStream() => _pipe?.Dispose();
}
