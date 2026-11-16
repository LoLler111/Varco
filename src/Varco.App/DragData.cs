using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using Varco.Core;
using Varco.Core.Clipboard;
using Varco.Core.Control;
using ComTypes = System.Runtime.InteropServices.ComTypes;

namespace Varco.App;

/// <summary>
/// Testo, HTML e immagini trascinati (da un browser, da Word…) letti dall'oggetto OLE del trascinamento, con gli stessi
/// formati degli appunti condivisi. Le immagini trascinate da un browser arrivano spesso come "file senza percorso"
/// (FileGroupDescriptorW + FileContents): si leggono anche quelli, con un limite di dimensione.
/// </summary>
internal static class DragDataReader
{
    private const int MaxFormatBytes = 8 * 1024 * 1024;
    private const int MaxFilesBytes = 8 * 1024 * 1024;
    private const int MaxFiles = 20;
    private const int DescriptorSize = 592;
    private const uint FD_ATTRIBUTES = 0x4, FD_FILESIZE = 0x40;
    private const int CF_DIB = 8, CF_UNICODETEXT = 13;

    public static List<ClipFormatDto> Read(ComTypes.IDataObject data)
    {
        var formats = new List<ClipFormatDto>();
        void Take(int cf, string name, bool text = false)
        {
            if (ReadFormat(data, (short)cf, -1, MaxFormatBytes) is not { Length: > 0 } bytes) return;
            if (text) bytes = TrimTerminator(bytes, cf == CF_UNICODETEXT ? 2 : 1);
            if (bytes.Length > 0) formats.Add(new ClipFormatDto { Name = name, Data = bytes });
        }
        Take(CF_UNICODETEXT, "CF_UNICODETEXT", text: true);
        Take(DataFormats.GetFormat("HTML Format").Id, "HTML Format", text: true);
        Take(DataFormats.GetFormat("Rich Text Format").Id, "Rich Text Format", text: true);
        Take(DataFormats.GetFormat("PNG").Id, "PNG");
        Take(CF_DIB, "CF_DIB");
        if (ReadVirtualFiles(data) is { Count: > 0 } files)
            formats.Add(new ClipFormatDto { Name = DragPayload.VirtualFilesFormat, Data = DragPayload.PackFiles(files) });
        return formats;
    }

    private static List<(string Name, byte[] Data)>? ReadVirtualFiles(ComTypes.IDataObject data)
    {
        var cfDescriptor = (short)DataFormats.GetFormat("FileGroupDescriptorW").Id;
        var cfContents = (short)DataFormats.GetFormat("FileContents").Id;
        if (ReadFormat(data, cfDescriptor, -1, 4 + MaxFiles * DescriptorSize) is not { Length: >= 4 } desc) return null;
        var count = BitConverter.ToUInt32(desc, 0);
        if (count == 0 || count > MaxFiles || desc.Length < 4 + count * DescriptorSize)
        {
            Log.Info($"Trascinamento di {count} file senza percorso: troppi, non passano");
            return null;
        }
        var files = new List<(string, byte[])>();
        long total = 0;
        for (var i = 0; i < (int)count; i++)
        {
            var d = desc.AsSpan(4 + i * DescriptorSize, DescriptorSize);
            var flags = BitConverter.ToUInt32(d);
            if ((flags & FD_ATTRIBUTES) != 0 && (BitConverter.ToUInt32(d[36..]) & (uint)FileAttributes.Directory) != 0)
            {
                Log.Info("Trascinamento di cartelle senza percorso: non passano");
                return null;
            }
            if ((flags & FD_FILESIZE) != 0)
            {
                var size = ((long)BitConverter.ToUInt32(d[64..]) << 32) | BitConverter.ToUInt32(d[68..]);
                if (total + size > MaxFilesBytes) return TooBig();
            }
            var name = Encoding.Unicode.GetString(d.Slice(72, 520)).Split('\0')[0];
            name = Path.GetFileName(name.Replace('/', '\\'));
            if (string.IsNullOrWhiteSpace(name)) name = $"file{i + 1}";
            if (ReadFormat(data, cfContents, i, (int)(MaxFilesBytes - total)) is not { } content) return null;
            total += content.Length;
            files.Add((name, content));
        }
        return files;

        static List<(string, byte[])>? TooBig()
        {
            Log.Info("File senza percorso trascinati troppo grandi: usa copia e incolla o salvali prima");
            return null;
        }
    }

    /// <summary>Byte di un formato (HGLOBAL o IStream), null se manca o supera <paramref name="max"/>.</summary>
    private static byte[]? ReadFormat(ComTypes.IDataObject data, short cf, int lindex, int max)
    {
        var fmt = new ComTypes.FORMATETC
        {
            cfFormat = cf,
            dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
            lindex = lindex,
            tymed = ComTypes.TYMED.TYMED_HGLOBAL | ComTypes.TYMED.TYMED_ISTREAM,
        };
        if (lindex < 0 && data.QueryGetData(ref fmt) != 0) return null;
        ComTypes.STGMEDIUM medium;
        try { data.GetData(ref fmt, out medium); }
        catch (COMException) { return null; }
        try
        {
            if (medium.tymed == ComTypes.TYMED.TYMED_HGLOBAL)
            {
                var size = (long)GlobalSize(medium.unionmember);
                if (size > max) return null;
                var p = GlobalLock(medium.unionmember);
                if (p == IntPtr.Zero) return null;
                try
                {
                    var bytes = new byte[size];
                    Marshal.Copy(p, bytes, 0, (int)size);
                    return bytes;
                }
                finally { GlobalUnlock(medium.unionmember); }
            }
            if (medium.tymed == ComTypes.TYMED.TYMED_ISTREAM)
            {
                var stream = (ComTypes.IStream)Marshal.GetObjectForIUnknown(medium.unionmember);
                using var ms = new MemoryStream();
                var buf = new byte[64 * 1024];
                var readPtr = Marshal.AllocHGlobal(sizeof(int));
                try
                {
                    while (true)
                    {
                        stream.Read(buf, buf.Length, readPtr);
                        var n = Marshal.ReadInt32(readPtr);
                        if (n <= 0) break;
                        ms.Write(buf, 0, n);
                        if (ms.Length > max) return null;
                    }
                }
                finally { Marshal.FreeHGlobal(readPtr); }
                return ms.ToArray();
            }
            return null;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or IOException)
        {
            Log.Debug($"Formato {cf} trascinato non letto: {ex.Message}");
            return null;
        }
        finally
        {
            ReleaseStgMedium(ref medium);
        }
    }

    /// <summary>Toglie il terminatore e quello che segue (la memoria HGLOBAL può essere più grande del testo).</summary>
    private static byte[] TrimTerminator(byte[] data, int unit)
    {
        for (var i = 0; i + unit <= data.Length; i += unit)
        {
            if (data[i] == 0 && (unit == 1 || data[i + 1] == 0)) return data[..i];
        }
        return data;
    }

    [DllImport("kernel32.dll")] private static extern UIntPtr GlobalSize(IntPtr h);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM medium);
}

/// <summary>
/// Oggetto OLE per lasciar cadere sul PC dove arrivano testo, HTML, immagini e file senza percorso: tutto in memoria,
/// HGLOBAL. I file senza percorso vanno come FileGroupDescriptorW + FileContents (Esplora risorse crea il file); se uno
/// è un'immagine e non c'è già un formato immagine, la si offre anche come PNG e CF_DIB per Word, Paint e simili.
/// </summary>
[ComVisible(true)]
public sealed class MemoryDataObject : ComTypes.IDataObject
{
    private const int DV_E_FORMATETC = unchecked((int)0x80040064);
    private const int OLE_E_ADVISENOTSUPPORTED = unchecked((int)0x80040003);
    private const int DATA_S_SAMEFORMATETC = 0x00040130;
    private const int E_NOTIMPL = unchecked((int)0x80004001);
    private const uint FD_FILESIZE = 0x40, FD_UNICODE = 0x80000000;
    private const int DescriptorSize = 592;
    private const int CF_DIB = 8, CF_UNICODETEXT = 13;

    private static readonly short CfDescriptor = (short)DataFormats.GetFormat("FileGroupDescriptorW").Id;
    private static readonly short CfContents = (short)DataFormats.GetFormat("FileContents").Id;

    private readonly Dictionary<short, byte[]> _data = [];
    private readonly List<(string Name, byte[] Data)> _files = [];
    private readonly ComTypes.FORMATETC[] _formats;

    internal MemoryDataObject(IEnumerable<ClipFormatDto> formats)
    {
        foreach (var f in formats)
        {
            switch (f.Name)
            {
                case DragPayload.VirtualFilesFormat:
                    try { _files.AddRange(DragPayload.UnpackFiles(f.Data)); }
                    catch (Exception ex) { Log.Warn($"File trascinati non validi: {ex.Message}"); }
                    break;
                case "CF_UNICODETEXT":
                    _data[CF_UNICODETEXT] = [.. f.Data, 0, 0];
                    break;
                case "CF_DIB":
                    _data[CF_DIB] = f.Data;
                    break;
                case "HTML Format" or "Rich Text Format":
                    _data[(short)DataFormats.GetFormat(f.Name).Id] = [.. f.Data, 0];
                    break;
                case "PNG":
                    _data[(short)DataFormats.GetFormat(f.Name).Id] = f.Data;
                    break;
            }
        }
        var cfPng = (short)DataFormats.GetFormat("PNG").Id;
        if (!_data.ContainsKey(cfPng) && !_data.ContainsKey(CF_DIB) && _files.Count == 1 && ToPngAndDib(_files[0].Data) is { } img)
        {
            _data[cfPng] = img.Png;
            _data[CF_DIB] = img.Dib;
        }

        var list = _data.Keys.Select(cf => Format(cf, -1)).ToList();
        if (_files.Count > 0)
        {
            list.Add(Format(CfDescriptor, -1));
            list.Add(Format(CfContents, -1));
        }
        _formats = list.ToArray();
    }

    internal bool IsEmpty => _formats.Length == 0;

    private static ComTypes.FORMATETC Format(short cf, int lindex) => new()
    {
        cfFormat = cf,
        dwAspect = ComTypes.DVASPECT.DVASPECT_CONTENT,
        lindex = lindex,
        tymed = ComTypes.TYMED.TYMED_HGLOBAL,
    };

    /// <summary>Immagine in un formato che System.Drawing sa leggere (PNG, JPEG, GIF, BMP…) → PNG e CF_DIB.</summary>
    private static (byte[] Png, byte[] Dib)? ToPngAndDib(byte[] file)
    {
        try
        {
            using var image = Image.FromStream(new MemoryStream(file), useEmbeddedColorManagement: false, validateImageData: true);
            using var png = new MemoryStream();
            image.Save(png, ImageFormat.Png);
            using var bitmap = new Bitmap(image);
            using var bmp = new MemoryStream();
            bitmap.Save(bmp, ImageFormat.Bmp);
            return (png.ToArray(), bmp.ToArray()[14..]); // CF_DIB = file BMP senza BITMAPFILEHEADER (14 byte)
        }
        catch (Exception ex) when (ex is ArgumentException or ExternalException or OutOfMemoryException)
        {
            return null; // non è un'immagine (o è un formato che System.Drawing non conosce, come WebP)
        }
    }

    public void GetData(ref ComTypes.FORMATETC format, out ComTypes.STGMEDIUM medium)
    {
        medium = default;
        if (!format.tymed.HasFlag(ComTypes.TYMED.TYMED_HGLOBAL)) throw new COMException("Solo HGLOBAL", DV_E_FORMATETC);
        if (format.cfFormat == CfDescriptor && _files.Count > 0)
        {
            medium = HGlobal(BuildDescriptor());
            return;
        }
        if (format.cfFormat == CfContents && format.lindex >= 0 && format.lindex < _files.Count)
        {
            medium = HGlobal(_files[format.lindex].Data);
            return;
        }
        if (_data.TryGetValue(format.cfFormat, out var bytes))
        {
            medium = HGlobal(bytes);
            return;
        }
        throw new COMException("Formato non disponibile", DV_E_FORMATETC);
    }

    public int QueryGetData(ref ComTypes.FORMATETC format)
    {
        foreach (var f in _formats)
            if (f.cfFormat == format.cfFormat && (format.tymed & ComTypes.TYMED.TYMED_HGLOBAL) != 0) return 0;
        return DV_E_FORMATETC;
    }

    public ComTypes.IEnumFORMATETC EnumFormatEtc(ComTypes.DATADIR direction)
    {
        if (direction != ComTypes.DATADIR.DATADIR_GET) throw new COMException("Solo lettura", E_NOTIMPL);
        Marshal.ThrowExceptionForHR(SHCreateStdEnumFmtEtc((uint)_formats.Length, _formats, out var e));
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

    private byte[] BuildDescriptor()
    {
        var buf = new byte[4 + _files.Count * DescriptorSize];
        BitConverter.TryWriteBytes(buf.AsSpan(0), (uint)_files.Count);
        for (var i = 0; i < _files.Count; i++)
        {
            var d = buf.AsSpan(4 + i * DescriptorSize, DescriptorSize);
            BitConverter.TryWriteBytes(d[0..], FD_FILESIZE | FD_UNICODE);
            BitConverter.TryWriteBytes(d[68..], (uint)_files[i].Data.Length);
            var name = _files[i].Name.Length > 259 ? _files[i].Name[..259] : _files[i].Name;
            Encoding.Unicode.GetBytes(name, d[72..]);
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
    [DllImport("ole32.dll")] private static extern void ReleaseStgMedium(ref ComTypes.STGMEDIUM medium);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);
    [DllImport("kernel32.dll")] private static extern IntPtr GlobalLock(IntPtr h);
    [DllImport("kernel32.dll")] private static extern bool GlobalUnlock(IntPtr h);
}
