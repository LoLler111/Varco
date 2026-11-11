using System.Drawing.Drawing2D;
using Varco.Core.Input;

namespace Varco.App;

/// <summary>
/// Disposizione dei PC come in Impostazioni → Schermo di Windows: ogni PC è un rettangolo proporzionale alla sua risoluzione,
/// si trascina in orizzontale e in verticale e al rilascio si aggancia ai bordi degli altri.
/// Il mouse passa da un PC all'altro solo dove i rettangoli si toccano, alla stessa altezza.
/// </summary>
internal sealed class LayoutCanvas : Control
{
    public sealed record Item(Guid Id, string Name, bool IsSelf, bool Connected, int X, int Y, int Width, int Height);

    private static readonly int Pad = Ui.S(10);
    /// <summary>Distanza (pixel sullo schermo) entro cui i bordi si allineano.</summary>
    private static readonly int AlignPx = Ui.S(10);

    private List<Item> _items = [];
    private readonly Font _small;

    // vista: schermo = offset + virtuale × scala (bloccata durante il trascinamento, così niente salti)
    private float _scale = 1, _offX, _offY;

    private int _drag = -1;
    private int _grabX, _grabY;   // punto afferrato, rispetto all'angolo del rettangolo (pixel virtuali)
    private int _rawX, _rawY;     // posizione seguita dal mouse
    private int _snapX, _snapY;   // posizione agganciata (dove finirà al rilascio)

    /// <summary>Un PC è stato spostato: nuove posizioni (pixel virtuali).</summary>
    public event Action<IReadOnlyList<(Guid Id, int X, int Y)>>? PositionsChanged;

    public LayoutCanvas()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        Height = Ui.S(190);
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
        items = items.Select(i => i.Width > 0 && i.Height > 0 ? i : i with { Width = Varco.Core.Input.Layout.DefaultWidth, Height = Varco.Core.Input.Layout.DefaultHeight }).ToList();
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
        int mx = _items.Max(i => i.Width) * 2 / 5, my = _items.Max(i => i.Height) * 2 / 5;
        int left = _items.Min(i => i.X) - mx, top = _items.Min(i => i.Y) - my;
        int right = _items.Max(i => i.X + i.Width) + mx, bottom = _items.Max(i => i.Y + i.Height) + my;
        _scale = Math.Min((Width - 2f * Pad) / Math.Max(1, right - left), (Height - 2f * Pad) / Math.Max(1, bottom - top));
        if (_scale <= 0) _scale = 0.01f;
        _offX = (Width - (right - left) * _scale) / 2 - left * _scale;
        _offY = (Height - (bottom - top) * _scale) / 2 - top * _scale;
    }

    private RectangleF ToScreen(int x, int y, int w, int h) =>
        new(_offX + x * _scale, _offY + y * _scale, w * _scale, h * _scale);

    private (int X, int Y) ToVirtual(Point p) =>
        ((int)Math.Round((p.X - _offX) / _scale), (int)Math.Round((p.Y - _offY) / _scale));

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
            DrawTile(g, it, ToScreen(it.X, it.Y, it.Width, it.Height), dragging: false);
        }
        if (_drag >= 0)
        {
            var it = _items[_drag];
            // dove si aggancerà al rilascio
            var target = ToScreen(_snapX, _snapY, it.Width, it.Height);
            using (var ghost = new Pen(Ui.Accent, 1.5f) { DashStyle = DashStyle.Dash })
            using (var path = Rounded(target, 6))
                g.DrawPath(ghost, path);
            DrawTile(g, it, ToScreen(_rawX, _rawY, it.Width, it.Height), dragging: true);
        }
    }

    private void DrawTile(Graphics g, Item item, RectangleF rect, bool dragging)
    {
        var radius = Ui.S(7);
        var text = item.IsSelf ? Color.White : item.Connected ? Ui.AccentText : Ui.Idle;
        if (item.IsSelf)
        {
            // il PC attuale "si accende": sfumatura viola con un alone
            Ui.Glow(g, rect, radius, Ui.BrandLight, Ui.S(dragging ? 10 : 7));
            using var path = Rounded(rect, radius);
            using var b = Ui.BrandGradient(rect);
            using var hi = new Pen(Color.FromArgb(60, Color.White), dragging ? 2f : 1f);
            g.FillPath(b, path);
            g.DrawPath(hi, path);
        }
        else
        {
            var fill = item.Connected ? Ui.AccentSoft : Color.FromArgb(0x22, 0x20, 0x2C);
            var border = item.Connected ? Color.FromArgb(0x7F, 0x77, 0xDD) : Color.FromArgb(0x3A, 0x37, 0x48);
            if (dragging) fill = Color.FromArgb(235, fill);
            using var path = Rounded(rect, radius);
            using var b = new SolidBrush(fill);
            using var p = new Pen(border, dragging ? 2.5f : 1.2f);
            if (!item.Connected) p.DashStyle = DashStyle.Dash;
            g.FillPath(b, path);
            g.DrawPath(p, path);
        }

        var inner = Rectangle.Round(RectangleF.Inflate(rect, -4, -3));
        var name = item.IsSelf ? $"{item.Name}\n(questo PC)" : item.Connected ? item.Name : $"{item.Name}\n(spento)";
        var size = $"{item.Width} × {item.Height}";
        var sizeH = TextRenderer.MeasureText(size, _small).Height;
        var showSize = inner.Height >= sizeH * 2 + 4;
        var nameRect = showSize ? new Rectangle(inner.X, inner.Y, inner.Width, inner.Height - sizeH) : inner;
        var longest = name.Split('\n').Max(l => TextRenderer.MeasureText(l, Font).Width);
        TextRenderer.DrawText(g, name, longest > nameRect.Width ? _small : Font, nameRect, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
        if (showSize)
            TextRenderer.DrawText(g, size, _small, new Rectangle(inner.X, inner.Bottom - sizeH, inner.Width, sizeH), Color.FromArgb(200, text),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis);
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
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            var it = _items[i];
            if (!ToScreen(it.X, it.Y, it.Width, it.Height).Contains(e.Location)) continue;
            var (vx, vy) = ToVirtual(e.Location);
            _drag = i;
            (_grabX, _grabY) = (vx - it.X, vy - it.Y);
            (_rawX, _rawY) = (_snapX, _snapY) = (it.X, it.Y);
            Capture = true;
            Invalidate();
            return;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag < 0)
        {
            Cursor = _items.Count > 1 && _items.Any(it => ToScreen(it.X, it.Y, it.Width, it.Height).Contains(e.Location)) ? Cursors.SizeAll : Cursors.Default;
            return;
        }
        var item = _items[_drag];
        // Il centro del rettangolo resta dentro la tela.
        var p = new Point(Math.Clamp(e.X, 0, Width - 1), Math.Clamp(e.Y, 0, Height - 1));
        var (vx, vy) = ToVirtual(p);
        _rawX = vx - _grabX;
        _rawY = vy - _grabY;
        var (minX, minY) = ToVirtual(Point.Empty);
        var (maxX, maxY) = ToVirtual(new Point(Width, Height));
        _rawX = Math.Clamp(_rawX, minX - item.Width / 2, Math.Max(minX - item.Width / 2, maxX - item.Width / 2));
        _rawY = Math.Clamp(_rawY, minY - item.Height / 2, Math.Max(minY - item.Height / 2, maxY - item.Height / 2));

        var others = _items.Where((_, i) => i != _drag).Select(o => new Rect(o.X, o.Y, o.X + o.Width, o.Y + o.Height)).ToList();
        (_snapX, _snapY) = LayoutSnap.Snap(others, item.Width, item.Height, _rawX, _rawY, (int)Math.Ceiling(AlignPx / _scale));
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
