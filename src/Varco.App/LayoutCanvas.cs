using System.Drawing.Drawing2D;
using Varco.Core.Input;

namespace Varco.App;

/// <summary>
/// Disposizione dei PC come in Impostazioni → Schermo di Windows: ogni PC è il gruppo dei suoi monitor, disposti come su
/// quel PC e proporzionali alla risoluzione. Il gruppo si trascina in blocco e al rilascio si aggancia ai monitor degli altri.
/// Il mouse passa da un PC all'altro solo dove i monitor si toccano, alla stessa altezza.
/// </summary>
internal sealed class LayoutCanvas : Control
{
    /// <summary>Un PC; <paramref name="Monitors"/> sono relativi al suo angolo in alto a sinistra (vuota: un solo monitor).</summary>
    public sealed record Item(Guid Id, string Name, bool IsSelf, bool Connected, int X, int Y, int Width, int Height,
        IReadOnlyList<LayoutMonitor> Monitors)
    {
        public bool Equals(Item? o) => o is not null && Id == o.Id && Name == o.Name && IsSelf == o.IsSelf && Connected == o.Connected
            && X == o.X && Y == o.Y && Width == o.Width && Height == o.Height && Monitors.SequenceEqual(o.Monitors);

        public override int GetHashCode() => HashCode.Combine(Id, X, Y, Width, Height, Monitors.Count);

        /// <summary>I monitor relativi all'angolo del PC (almeno uno).</summary>
        public List<Rect> Screens() => Monitors.Count == 0
            ? [new Rect(0, 0, Width, Height)]
            : Monitors.Select(m => new Rect(m.X, m.Y, m.X + m.Width, m.Y + m.Height)).ToList();
    }

    private static readonly int Pad = Ui.S(10);
    /// <summary>Distanza (pixel sullo schermo) entro cui i bordi si allineano; con Alt premuto non si allineano.</summary>
    private static readonly int AlignPx = Ui.S(4);
    /// <summary>Spazio (pixel sullo schermo) fra un monitor e l'altro dello stesso PC, perché si distinguano.</summary>
    private const float MonitorGap = 1.5f;

    private List<Item> _items = [];
    private readonly Font _small;

    // vista: schermo = offset + virtuale × scala (bloccata durante il trascinamento, così niente salti)
    private float _scale = 1, _offX, _offY;

    private int _drag = -1;
    private int _grabX, _grabY;   // punto afferrato, rispetto all'angolo del PC (pixel virtuali)
    private int _rawX, _rawY;     // posizione seguita dal mouse
    private int _snapX, _snapY;   // posizione agganciata (dove finirà al rilascio)

    /// <summary>Un PC è stato spostato: nuove posizioni (pixel virtuali).</summary>
    public event Action<IReadOnlyList<(Guid Id, int X, int Y)>>? PositionsChanged;

    public LayoutCanvas()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        Height = Ui.S(220);
        _small = new Font(Font.FontFamily, Math.Max(6.5f, Font.Size - 1.5f));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _small.Dispose();
        base.Dispose(disposing);
    }

    public void SetItems(IReadOnlyList<Item> items)
    {
        // Dimensione sconosciuta (agent di una versione precedente): come quella predefinita del layout.
        items = items.Select(i => i.Width > 0 && i.Height > 0 ? i : i with { Width = Varco.Core.Input.Layout.DefaultWidth, Height = Varco.Core.Input.Layout.DefaultHeight, Monitors = [] }).ToList();
        if (_drag >= 0 || items.SequenceEqual(_items)) return;
        _items = items.ToList();
        UpdateView();
        Invalidate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_drag < 0) UpdateView();
    }

    private void UpdateView()
    {
        if (_items.Count == 0) return;
        // Margine attorno ai PC: c'è spazio per trascinarli sopra, sotto e ai lati.
        int mx = _items.Max(i => i.Width) / 3, my = _items.Max(i => i.Height) / 3;
        int left = _items.Min(i => i.X) - mx, top = _items.Min(i => i.Y) - my;
        int right = _items.Max(i => i.X + i.Width) + mx, bottom = _items.Max(i => i.Y + i.Height) + my;
        _scale = Math.Min((Width - 2f * Pad) / Math.Max(1, right - left), (Height - 2f * Pad) / Math.Max(1, bottom - top));
        if (_scale <= 0) _scale = 0.01f;
        _offX = (Width - (right - left) * _scale) / 2 - left * _scale;
        _offY = (Height - (bottom - top) * _scale) / 2 - top * _scale;
    }

    private RectangleF ToScreen(int x, int y, int w, int h) =>
        new(_offX + x * _scale, _offY + y * _scale, w * _scale, h * _scale);

    /// <summary>Un monitor (relativo al PC in <paramref name="x"/>,<paramref name="y"/>) sullo schermo, staccato dai vicini.</summary>
    private RectangleF ToScreen(int x, int y, Rect m)
    {
        var r = ToScreen(x + m.Left, y + m.Top, m.Width, m.Height);
        return RectangleF.Inflate(r, -MonitorGap / 2, -MonitorGap / 2);
    }

    private (int X, int Y) ToVirtual(Point p) =>
        ((int)Math.Round((p.X - _offX) / _scale), (int)Math.Round((p.Y - _offY) / _scale));

    private int HitTest(Point p)
    {
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var it = _items[i];
            if (it.Screens().Any(m => ToScreen(it.X + m.Left, it.Y + m.Top, m.Width, m.Height).Contains(p))) return i;
        }
        return -1;
    }

    // ───────────── disegno ─────────────

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        g.Clear(Parent?.BackColor ?? Ui.Card);
        using (var bg = new SolidBrush(Ui.Well))
        using (var frame = Rounded(new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f), Ui.S(10)))
        using (var border = new Pen(Ui.Border))
        {
            g.FillPath(bg, frame);
            // griglia di puntini leggera: dà profondità alla "scrivania" senza costare nulla
            using (var dots = new SolidBrush(Color.FromArgb(0x22, 0x20, 0x2C)))
            {
                var step = Ui.S(16);
                for (int y = step; y < Height - 4; y += step)
                    for (int x = step; x < Width - 4; x += step)
                        g.FillRectangle(dots, x, y, 2, 2);
            }
            g.DrawPath(border, frame);
        }

        for (int i = 0; i < _items.Count; i++)
        {
            if (i == _drag) continue;
            var it = _items[i];
            DrawPc(g, it, it.X, it.Y, dragging: false);
        }
        if (_drag >= 0)
        {
            var it = _items[_drag];
            // dove si aggancerà al rilascio
            using (var ghost = new Pen(Ui.Accent, 1.5f) { DashStyle = DashStyle.Dash })
                foreach (var m in it.Screens())
                {
                    using var path = Rounded(ToScreen(_snapX, _snapY, m), 5);
                    g.DrawPath(ghost, path);
                }
            DrawPc(g, it, _rawX, _rawY, dragging: true);
        }
    }

    private void DrawPc(Graphics g, Item item, int x, int y, bool dragging)
    {
        var screens = item.Screens();
        var rects = screens.Select(m => ToScreen(x, y, m)).ToList();
        var radius = Ui.S(rects.Count > 1 ? 5 : 7);
        var text = item.IsSelf ? Color.White : item.Connected ? Ui.AccentText : Ui.Idle;

        if (item.IsSelf)
            foreach (var r in rects) Ui.Glow(g, r, radius, Ui.BrandLight, Ui.S(dragging ? 10 : 7));
        foreach (var r in rects)
        {
            using var path = Rounded(r, radius);
            if (item.IsSelf)
            {
                // il PC attuale "si accende": sfumatura viola con un alone
                using var b = Ui.BrandGradient(r);
                using var hi = new Pen(Color.FromArgb(60, Color.White), dragging ? 2f : 1f);
                g.FillPath(b, path);
                g.DrawPath(hi, path);
            }
            else
            {
                var fill = item.Connected ? Ui.AccentSoft : Color.FromArgb(0x22, 0x20, 0x2C);
                var border = item.Connected ? Color.FromArgb(0x7F, 0x77, 0xDD) : Color.FromArgb(0x3A, 0x37, 0x48);
                if (dragging) fill = Color.FromArgb(235, fill);
                using var b = new SolidBrush(fill);
                using var p = new Pen(border, dragging ? 2.5f : 1.2f);
                if (!item.Connected) p.DashStyle = DashStyle.Dash;
                g.FillPath(b, path);
                g.DrawPath(p, path);
            }
        }

        // Nome sul monitor principale (il primo, come lo elenca Windows), risoluzione su ognuno se c'è spazio.
        var sizeH = TextRenderer.MeasureText("0", _small).Height;
        for (int i = 0; i < rects.Count; i++)
        {
            var inner = Rectangle.Round(RectangleF.Inflate(rects[i], -4, -3));
            var size = $"{screens[i].Width} × {screens[i].Height}";
            var showSize = inner.Height >= sizeH * (i == 0 ? 2 : 1) + 4 && TextRenderer.MeasureText(size, _small).Width <= inner.Width + 8;
            if (i == 0)
            {
                var name = item.IsSelf ? $"{item.Name}\n(questo PC)" : item.Connected ? item.Name : $"{item.Name}\n(spento)";
                var nameRect = showSize ? new Rectangle(inner.X, inner.Y, inner.Width, inner.Height - sizeH) : inner;
                var longest = name.Split('\n').Max(l => TextRenderer.MeasureText(l, Font).Width);
                TextRenderer.DrawText(g, name, longest > nameRect.Width ? _small : Font, nameRect, text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
                if (showSize)
                    TextRenderer.DrawText(g, size, _small, new Rectangle(inner.X, inner.Bottom - sizeH, inner.Width, sizeH), Color.FromArgb(200, text),
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis);
            }
            else if (showSize)
                TextRenderer.DrawText(g, size, _small, inner, Color.FromArgb(170, text),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        }
    }

    private static GraphicsPath Rounded(RectangleF r, float radius)
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

    // ───────────── trascinamento ─────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _items.Count < 2) return;
        var i = HitTest(e.Location);
        if (i < 0) return;
        var it = _items[i];
        var (vx, vy) = ToVirtual(e.Location);
        _drag = i;
        (_grabX, _grabY) = (vx - it.X, vy - it.Y);
        (_rawX, _rawY) = (_snapX, _snapY) = (it.X, it.Y);
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag < 0)
        {
            Cursor = _items.Count > 1 && HitTest(e.Location) >= 0 ? Cursors.SizeAll : Cursors.Default;
            return;
        }
        UpdateDrag(e.Location);
    }

    private void UpdateDrag(Point mouse)
    {
        var item = _items[_drag];
        // Il centro del PC resta dentro la tela.
        var p = new Point(Math.Clamp(mouse.X, 0, Width - 1), Math.Clamp(mouse.Y, 0, Height - 1));
        var (vx, vy) = ToVirtual(p);
        _rawX = vx - _grabX;
        _rawY = vy - _grabY;
        var (minX, minY) = ToVirtual(Point.Empty);
        var (maxX, maxY) = ToVirtual(new Point(Width, Height));
        _rawX = Math.Clamp(_rawX, minX - item.Width / 2, Math.Max(minX - item.Width / 2, maxX - item.Width / 2));
        _rawY = Math.Clamp(_rawY, minY - item.Height / 2, Math.Max(minY - item.Height / 2, maxY - item.Height / 2));

        var others = _items.Where((_, i) => i != _drag)
            .SelectMany(o => o.Screens().Select(m => new Rect(o.X + m.Left, o.Y + m.Top, o.X + m.Right, o.Y + m.Bottom)))
            .ToList();
        var align = (ModifierKeys & Keys.Alt) != 0 ? 0 : (int)Math.Ceiling(AlignPx / _scale);
        (_snapX, _snapY) = LayoutSnap.Snap(others, item.Screens(), _rawX, _rawY, align);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag < 0) return;
        var item = _items[_drag];
        _drag = -1;
        Capture = false;
        if (item.X != _snapX || item.Y != _snapY)
        {
            var moved = item with { X = _snapX, Y = _snapY };
            _items[_items.IndexOf(item)] = moved;
            PositionsChanged?.Invoke([(moved.Id, moved.X, moved.Y)]);
        }
        UpdateView();
        Invalidate();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (_drag >= 0 && !Capture)
        {
            // trascinamento interrotto (es. Alt+Tab): torna dov'era
            _drag = -1;
            UpdateView();
            Invalidate();
        }
    }
}
