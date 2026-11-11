using System.Drawing.Drawing2D;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace Varco.App;

/// <summary>
/// Tema "Notte" delle finestre: fondo scuro, schede in rilievo, viola acceso per le azioni e verde per "collegato".
/// Tutto è disegnato con GDI+ e ridisegnato solo quando cambia qualcosa: niente animazioni continue, niente librerie.
/// Le misure si scrivono a 96 DPI e passano da <see cref="S"/>.
/// </summary>
internal static class Ui
{
    // superfici
    public static readonly Color Bg = Color.FromArgb(0x15, 0x14, 0x1B);
    public static readonly Color Card = Color.FromArgb(0x1E, 0x1C, 0x27);
    public static readonly Color Border = Color.FromArgb(0x2B, 0x28, 0x38);
    public static readonly Color Well = Color.FromArgb(0x17, 0x16, 0x1F);      // incassi: tela degli schermi, campi di testo
    public static readonly Color Raised = Color.FromArgb(0x26, 0x24, 0x33);    // pulsanti secondari, chip neutri
    public static readonly Color RaisedHover = Color.FromArgb(0x30, 0x2D, 0x40);
    public static readonly Color Line = Color.FromArgb(0x38, 0x35, 0x48);

    // testo
    public static readonly Color Text = Color.FromArgb(0xEC, 0xEA, 0xF6);
    public static readonly Color Muted = Color.FromArgb(0x8E, 0x8A, 0xA6);
    public static readonly Color Faint = Color.FromArgb(0x6A, 0x66, 0x80);

    // colori di stato
    public static readonly Color Brand = VarcoIcon.Brand;
    public static readonly Color BrandLight = Color.FromArgb(0x8A, 0x82, 0xE6);
    public static readonly Color BrandDark = Color.FromArgb(0x43, 0x3B, 0x9C);
    public static readonly Color Accent = Color.FromArgb(0xAF, 0xA9, 0xEC);    // link, testo viola su fondo scuro
    public static readonly Color AccentSoft = Color.FromArgb(0x26, 0x21, 0x3F);
    public static readonly Color AccentText = Color.FromArgb(0xCE, 0xCB, 0xF6);
    public static readonly Color Ok = Color.FromArgb(0x5D, 0xCA, 0xA5);
    public static readonly Color OkSoft = Color.FromArgb(0x10, 0x36, 0x2C);
    public static readonly Color Bad = Color.FromArgb(0xF0, 0x95, 0x95);
    public static readonly Color BadSoft = Color.FromArgb(0x3A, 0x1C, 0x22);
    public static readonly Color Idle = Color.FromArgb(0x77, 0x73, 0x8C);
    public static readonly Color IdleSoft = Raised;

    private static readonly float Dpi = ReadDpi();

    private static float ReadDpi()
    {
        try
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            return g.DpiX;
        }
        catch { return 96f; }
    }

    /// <summary>Converte una misura pensata a 96 DPI in pixel reali.</summary>
    public static int S(int px) => (int)Math.Round(px * Dpi / 96f);
    public static Size S(int w, int h) => new(S(w), S(h));
    public static Padding P(int l, int t, int r, int b) => new(S(l), S(t), S(r), S(b));

    public static readonly Font Body = SystemFonts.MessageBoxFont ?? Control.DefaultFont;
    public static readonly Font BodyStrong = Semibold(Body.SizeInPoints);
    public static readonly Font Small = new(Body.FontFamily, Math.Max(7f, Body.SizeInPoints - 0.75f));
    public static readonly Font Section = Semibold(10.5f);
    public static readonly Font Title = Semibold(16f);
    public static readonly Font Code = Semibold(24f);

    public static Font Semibold(float size)
    {
        try
        {
            var f = new Font("Segoe UI Semibold", size);
            if (f.Name == "Segoe UI Semibold") return f;
            f.Dispose();
        }
        catch { }
        return new Font(Body.FontFamily, size, FontStyle.Bold);
    }

    /// <summary>Impostazioni comuni delle finestre di Varco, compresa la barra del titolo scura.</summary>
    public static void StyleForm(Form f)
    {
        f.AutoScaleMode = AutoScaleMode.None; // le misure sono già scalate con S()
        f.Font = Body;
        f.BackColor = Bg;
        f.ForeColor = Text;
        f.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        f.HandleCreated += (_, _) => DarkTitleBar(f.Handle);
    }

    /// <summary>
    /// Barra del titolo scura e dello stesso colore del fondo (Windows 10 20H1+ / Windows 11).
    /// Sui sistemi più vecchi le chiamate falliscono in silenzio e resta quella standard.
    /// </summary>
    public static void DarkTitleBar(IntPtr hwnd)
    {
        try
        {
            int on = 1;
            if (DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19 /* valore pre-20H1 */, ref on, sizeof(int));
            int caption = ColorRef(Bg), text = ColorRef(Muted), border = ColorRef(Border);
            DwmSetWindowAttribute(hwnd, 35 /* DWMWA_CAPTION_COLOR */, ref caption, sizeof(int));
            DwmSetWindowAttribute(hwnd, 36 /* DWMWA_TEXT_COLOR */, ref text, sizeof(int));
            DwmSetWindowAttribute(hwnd, 34 /* DWMWA_BORDER_COLOR */, ref border, sizeof(int));
        }
        catch { }
    }

    private static int ColorRef(Color c) => c.R | c.G << 8 | c.B << 16;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        var d = Math.Max(1, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>Sfumatura viola delle azioni principali e del PC attuale.</summary>
    public static LinearGradientBrush BrandGradient(RectangleF r, bool hover = false, bool down = false)
    {
        var (a, b) = down ? (Brand, BrandDark) : hover ? (Color.FromArgb(0x9A, 0x93, 0xEE), Color.FromArgb(0x5C, 0x53, 0xC2)) : (BrandLight, Brand);
        return new LinearGradientBrush(new RectangleF(r.X, r.Y - 1, r.Width, r.Height + 2), a, b, 60f);
    }

    /// <summary>Alone morbido attorno a un rettangolo arrotondato: pochi contorni sempre più trasparenti (economico).</summary>
    public static void Glow(Graphics g, RectangleF r, float radius, Color color, int size)
    {
        for (int i = size; i >= 1; i--)
        {
            var alpha = (int)(70f * (1 - (float)i / (size + 1)) / size * 2);
            using var path = Rounded(RectangleF.Inflate(r, i, i), radius + i);
            using var p = new Pen(Color.FromArgb(Math.Clamp(alpha, 0, 255), color), 2f);
            g.DrawPath(p, path);
        }
    }

    public static FlowLayoutPanel Column(int top = 0) =>
        new() { FlowDirection = FlowDirection.TopDown, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, top, 0, 0), Padding = Padding.Empty };

    public static FlowLayoutPanel Flow(int top = 0) =>
        new() { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, Margin = new Padding(0, top, 0, 0), Padding = Padding.Empty };

    public static Label Label(string text, Font? font = null, Color? color = null, int maxWidth = 0, Padding? margin = null) => new()
    {
        Text = text,
        AutoSize = true,
        Font = font ?? Body,
        ForeColor = color ?? Text,
        MaximumSize = new Size(maxWidth, 0),
        Margin = margin ?? Padding.Empty,
        UseMnemonic = false,
    };

    public static LinkLabel Link(string text, Action onClick, Padding? margin = null)
    {
        var l = new LinkLabel
        {
            Text = text,
            AutoSize = true,
            LinkColor = Accent,
            ActiveLinkColor = AccentText,
            VisitedLinkColor = Accent,
            LinkBehavior = LinkBehavior.HoverUnderline,
            Margin = margin ?? Padding.Empty,
            UseMnemonic = false,
        };
        l.LinkClicked += (_, _) => onClick();
        return l;
    }

    /// <summary>Indirizzi IPv4 di questo PC sulle reti vere (con gateway), da dettare all'altro PC.</summary>
    public static List<string> LocalAddresses()
    {
        var withGateway = new List<string>();
        var others = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                var props = nic.GetIPProperties();
                var hasGateway = props.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(System.Net.IPAddress.Any));
                foreach (var ua in props.UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    var b = ua.Address.GetAddressBytes();
                    if (b[0] == 169 && b[1] == 254) continue; // nessun DHCP: inutile
                    (hasGateway ? withGateway : others).Add(ua.Address.ToString());
                }
            }
        }
        catch { }
        return withGateway.Count > 0 ? withGateway : others;
    }
}

/// <summary>Scheda in rilievo con angoli arrotondati: un titolo facoltativo e un contenuto in colonna.</summary>
internal sealed class Card : Panel
{
    public FlowLayoutPanel Body { get; } = Ui.Column();
    public int InnerWidth { get; }

    public Card(int width, string? title = null, int top = 12)
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        BackColor = Ui.Bg;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = Ui.P(16, 14, 16, 14);
        Margin = new Padding(0, Ui.S(top), 0, 0);
        MinimumSize = MaximumSize = new Size(width, 0);
        InnerWidth = width - Padding.Horizontal;

        Body.BackColor = Ui.Card;
        Body.Location = new Point(Padding.Left, Padding.Top);
        if (title is not null) Body.Controls.Add(Ui.Label(title, Ui.Section, margin: new Padding(0, 0, 0, Ui.S(8))));
        Controls.Add(Body);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Ui.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.S(12));
        using var fill = new SolidBrush(Ui.Card);
        using var pen = new Pen(Ui.Border);
        g.FillPath(fill, path);
        g.DrawPath(pen, path);
    }
}

/// <summary>Riquadro colorato arrotondato (es. il codice di abbinamento). Si adatta al contenuto.</summary>
internal sealed class Tint : Panel
{
    private Color _fill;
    private Color _edge = Color.Empty;

    public Tint(Color fill)
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        _fill = fill;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = Ui.P(14, 12, 14, 12);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Fill
    {
        get => _fill;
        set { _fill = value; foreach (Control c in Controls) c.BackColor = value; Invalidate(); }
    }

    /// <summary>Bordo facoltativo (Color.Empty = nessuno).</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Edge
    {
        get => _edge;
        set { _edge = value; Invalidate(); }
    }

    protected override void OnControlAdded(ControlEventArgs e)
    {
        base.OnControlAdded(e);
        e.Control!.BackColor = _fill;
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Ui.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.S(10));
        using var b = new SolidBrush(_fill);
        g.FillPath(b, path);
        if (_edge != Color.Empty)
        {
            using var p = new Pen(_edge);
            g.DrawPath(p, path);
        }
    }
}

/// <summary>Campo di testo scuro con bordo arrotondato (il TextBox di sistema non ha un bordo che si possa colorare).</summary>
internal sealed class InputBox : Panel
{
    public TextBox Box { get; } = new() { BorderStyle = BorderStyle.None, BackColor = Ui.Well, ForeColor = Ui.Text };

    public InputBox(int width, int height, string placeholder)
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        Size = new Size(width, height);
        Margin = Padding.Empty;
        Box.PlaceholderText = placeholder;
        Box.Width = width - Ui.S(24);
        Box.Location = new Point(Ui.S(12), (height - Box.PreferredHeight) / 2);
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
        Controls.Add(Box);
        Cursor = Cursors.IBeam;
        Click += (_, _) => Box.Focus();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Ui.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.S(8));
        using var b = new SolidBrush(Ui.Well);
        using var p = new Pen(Box.Focused ? Ui.BrandLight : Ui.Line, Box.Focused ? 1.5f : 1f);
        g.FillPath(b, path);
        g.DrawPath(p, path);
    }
}

/// <summary>Interruttore on/off disegnato (al posto della casella di spunta, che sul fondo scuro resta bianca).</summary>
internal sealed class Toggle : Control
{
    private bool _checked;
    public event EventHandler? CheckedChanged;

    public Toggle(string text)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        Text = text;
        Font = Ui.Body;
        ForeColor = Ui.Text;
        Cursor = Cursors.Hand;
        TabStop = true;
        var t = TextRenderer.MeasureText(text, Font);
        Size = new Size(Ui.S(48) + t.Width, Math.Max(Ui.S(24), t.Height + Ui.S(4)));
        Margin = Padding.Empty;
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnClick(EventArgs e) { Focus(); Checked = !Checked; base.OnClick(e); }
    protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !Checked; base.OnKeyUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.Card);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var track = new RectangleF(1, (Height - Ui.S(20)) / 2f, Ui.S(36), Ui.S(20));
        using (var path = Ui.Rounded(track, track.Height / 2))
        {
            if (_checked)
            {
                using var b = Ui.BrandGradient(track);
                g.FillPath(b, path);
            }
            else
            {
                using var b = new SolidBrush(Ui.Raised);
                using var p = new Pen(Ui.Line);
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
            if (Focused && ShowFocusCues)
            {
                using var fp = new Pen(Ui.Accent, 1f) { DashStyle = DashStyle.Dot };
                using var ring = Ui.Rounded(RectangleF.Inflate(track, 2, 2), track.Height / 2 + 2);
                g.DrawPath(fp, ring);
            }
        }
        var d = track.Height - Ui.S(6);
        var x = _checked ? track.Right - d - Ui.S(3) : track.X + Ui.S(3);
        using (var knob = new SolidBrush(_checked ? Color.White : Ui.Muted))
            g.FillEllipse(knob, x, track.Y + Ui.S(3), d, d);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(Ui.S(46), 0, Width - Ui.S(46), Height), ForeColor,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Riga a larghezza fissa: elementi a sinistra in fila, uno facoltativo a destra, tutti centrati in verticale.</summary>
internal sealed class LineRow : Panel
{
    private readonly Control[] _left;
    private readonly Control? _right;
    private readonly Control? _stretch;

    /// <param name="stretch">Elemento di sinistra che prende lo spazio rimasto (testo con i puntini se non ci sta).</param>
    public LineRow(int width, int height, Control[] left, Control? right = null, Control? stretch = null)
    {
        _left = left;
        _right = right;
        _stretch = stretch;
        Size = new Size(width, height);
        Margin = Padding.Empty;
        Controls.AddRange(left);
        if (right is not null) Controls.Add(right);
        if (stretch is Label l) { l.AutoSize = false; l.AutoEllipsis = true; l.TextAlign = ContentAlignment.MiddleLeft; l.Height = height; }
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var rightW = _right is null ? 0 : _right.Width + _right.Margin.Horizontal;
        var x = 0;
        foreach (var c in _left)
        {
            if (!c.Visible) continue;
            x += c.Margin.Left;
            if (c == _stretch)
            {
                var fixedAfter = _left.SkipWhile(o => o != c).Skip(1).Where(o => o.Visible).Sum(o => o.Width + o.Margin.Horizontal);
                c.Width = Math.Max(0, Width - x - c.Margin.Right - fixedAfter - rightW);
            }
            c.Location = new Point(x, (Height - c.Height) / 2);
            x += c.Width + c.Margin.Right;
        }
        if (_right is { Visible: true })
            _right.Location = new Point(Width - _right.Width - _right.Margin.Right, (Height - _right.Height) / 2);
    }
}

/// <summary>Pallino di stato; quello pieno ha un piccolo alone.</summary>
internal sealed class Dot : Control
{
    private readonly Color _color;
    private readonly bool _hollow;

    public Dot(Color color, bool hollow = false, int size = 14)
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = Color.Transparent;
        _color = color;
        _hollow = hollow;
        Size = new Size(Ui.S(size), Ui.S(size));
        Margin = Padding.Empty;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var d = Ui.S(8);
        var r = new RectangleF((Width - d) / 2f, (Height - d) / 2f, d, d);
        if (_hollow)
        {
            using var p = new Pen(_color, Ui.S(2) * 0.8f);
            g.DrawEllipse(p, RectangleF.Inflate(r, -0.8f, -0.8f));
            return;
        }
        using (var halo = new SolidBrush(Color.FromArgb(50, _color)))
            g.FillEllipse(halo, RectangleF.Inflate(r, Ui.S(3), Ui.S(3)));
        using var b = new SolidBrush(_color);
        g.FillEllipse(b, r);
    }
}

/// <summary>Piccola icona di un PC in un quadrato viola scuro, davanti al nome nei PC trovati.</summary>
internal sealed class PcBadge : Control
{
    public PcBadge(int size = 34)
    {
        SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
        BackColor = Color.Transparent;
        Size = new Size(Ui.S(size), Ui.S(size));
        Margin = Padding.Empty;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(Ui.AccentSoft))
        using (var path = Ui.Rounded(new RectangleF(0, 0, Width - 1, Height - 1), Width * 0.26f))
            g.FillPath(bg, path);
        float u = Width / 32f;
        using var pen = new Pen(Ui.Accent, 1.8f * u) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
        using (var screen = Ui.Rounded(new RectangleF(8 * u, 9 * u, 16 * u, 11 * u), 2 * u))
            g.DrawPath(pen, screen);
        g.DrawLine(pen, 16 * u, 20 * u, 16 * u, 23 * u);
        g.DrawLine(pen, 12 * u, 23.5f * u, 20 * u, 23.5f * u);
    }
}

/// <summary>Pulsante con angoli arrotondati: sfumato viola (azione principale) o in rilievo scuro (secondaria).</summary>
internal sealed class VButton : Button
{
    private bool _hover, _down;
    private readonly Bitmap? _icon;
    public bool Primary { get; }

    /// <param name="shield">Mostra lo scudo dell'amministratore (azioni che chiedono conferma a Windows).</param>
    public VButton(string text, bool primary = false, int minWidth = 88, bool shield = false)
    {
        Primary = primary;
        Text = text;
        UseMnemonic = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Font = primary ? Ui.BodyStrong : Ui.Body;
        Cursor = Cursors.Hand;
        if (shield)
        {
            try { _icon = new Icon(SystemIcons.Shield, Ui.S(16), Ui.S(16)).ToBitmap(); } catch { }
        }
        var t = TextRenderer.MeasureText(text, Font);
        var iconW = _icon is null ? 0 : Ui.S(22);
        Size = new Size(Math.Max(Ui.S(minWidth), t.Width + iconW + Ui.S(32)), Math.Max(Ui.S(34), t.Height + Ui.S(16)));
        Margin = Padding.Empty;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _icon?.Dispose();
        base.Dispose(disposing);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs mevent) { _down = true; Invalidate(); base.OnMouseDown(mevent); }
    protected override void OnMouseUp(MouseEventArgs mevent) { _down = false; Invalidate(); base.OnMouseUp(mevent); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        Color text;
        using (var path = Ui.Rounded(r, Ui.S(8)))
        {
            if (Primary && Enabled)
            {
                using var b = Ui.BrandGradient(r, _hover, _down);
                g.FillPath(b, path);
                using var hi = new Pen(Color.FromArgb(_down ? 0 : 40, Color.White));
                using var top = Ui.Rounded(RectangleF.Inflate(r, -0.5f, -0.5f), Ui.S(8));
                g.DrawPath(hi, top);
                text = Color.White;
            }
            else
            {
                var fill = !Enabled ? Ui.Card : _down ? Ui.Border : _hover ? Ui.RaisedHover : Ui.Raised;
                using var b = new SolidBrush(fill);
                using var p = new Pen(_hover && Enabled ? Ui.Faint : Ui.Line);
                g.FillPath(b, path);
                g.DrawPath(p, path);
                text = Enabled ? Ui.Text : Ui.Faint;
            }
        }
        if (Focused && ShowFocusCues)
        {
            using var focus = Ui.Rounded(RectangleF.Inflate(r, -Ui.S(3), -Ui.S(3)), Ui.S(6));
            using var fp = new Pen(Color.FromArgb(170, Primary ? Color.White : Ui.Accent), 1f) { DashStyle = DashStyle.Dot };
            g.DrawPath(fp, focus);
        }
        var content = ClientRectangle;
        if (_icon is not null)
        {
            var tw = TextRenderer.MeasureText(Text, Font).Width;
            var total = _icon.Width + Ui.S(6) + tw;
            var x0 = (Width - total) / 2;
            g.DrawImage(_icon, x0, (Height - _icon.Height) / 2);
            content = new Rectangle(x0 + _icon.Width + Ui.S(6), 0, tw + Ui.S(4), Height);
        }
        TextRenderer.DrawText(g, Text, Font, content, text,
            (_icon is null ? TextFormatFlags.HorizontalCenter : TextFormatFlags.Left) | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }
}

/// <summary>Etichetta "a pillola" con pallino colorato: lo stato in breve.</summary>
internal sealed class StatusChip : Control
{
    private Color _fg = Ui.Idle, _bg = Ui.IdleSoft;

    public StatusChip()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        Font = Ui.BodyStrong;
        Margin = Padding.Empty;
    }

    public void Set(string text, Color fg, Color bg)
    {
        if (Text == text && _fg == fg && _bg == bg) return;
        Text = text;
        _fg = fg;
        _bg = bg;
        var t = TextRenderer.MeasureText(text, Font, Size.Empty, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
        Size = new Size(t.Width + Ui.S(34), t.Height + Ui.S(8));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Parent?.BackColor ?? Ui.Bg);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        using (var path = Ui.Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Height / 2f))
        using (var b = new SolidBrush(_bg))
        using (var p = new Pen(Color.FromArgb(60, _fg)))
        {
            g.FillPath(b, path);
            g.DrawPath(p, path);
        }
        var d = Ui.S(8);
        var cy = (Height - d) / 2f;
        using (var halo = new SolidBrush(Color.FromArgb(60, _fg)))
            g.FillEllipse(halo, Ui.S(10) - Ui.S(2), cy - Ui.S(2), d + Ui.S(4), d + Ui.S(4));
        using (var dot = new SolidBrush(_fg))
            g.FillEllipse(dot, Ui.S(10), cy, d, d);
        TextRenderer.DrawText(g, Text, Font, new Rectangle(Ui.S(25), 0, Width - Ui.S(25), Height), _fg, _bg,
            TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }
}

/// <summary>Menu della tray col tema scuro.</summary>
internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new Colors()) { RoundedEdges = true; }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Ui.Text : Ui.Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
    {
        e.ArrowColor = Ui.Muted;
        base.OnRenderArrow(e);
    }

    private sealed class Colors : ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground => Ui.Card;
        public override Color ImageMarginGradientBegin => Ui.Card;
        public override Color ImageMarginGradientMiddle => Ui.Card;
        public override Color ImageMarginGradientEnd => Ui.Card;
        public override Color MenuBorder => Ui.Line;
        public override Color MenuItemBorder => Ui.AccentSoft;
        public override Color MenuItemSelected => Ui.AccentSoft;
        public override Color MenuItemSelectedGradientBegin => Ui.AccentSoft;
        public override Color MenuItemSelectedGradientEnd => Ui.AccentSoft;
        public override Color MenuItemPressedGradientBegin => Ui.AccentSoft;
        public override Color MenuItemPressedGradientEnd => Ui.AccentSoft;
        public override Color SeparatorDark => Ui.Border;
        public override Color SeparatorLight => Ui.Card;
    }
}
