using System.Drawing.Drawing2D;

namespace Varco.App;

/// <summary>Disposizione dei PC come schermi affiancati: si riordinano trascinandoli.</summary>
internal sealed class LayoutStrip : Control
{
    public sealed record Item(Guid Id, string Name, bool IsSelf, bool Connected);

    private List<Item> _items = [];
    private int _drag = -1;
    private int _dragOffsetX;
    private int _dragX;
    private bool _moved;

    public event Action<IReadOnlyList<Guid>>? OrderChanged;

    public LayoutStrip()
    {
        DoubleBuffered = true;
        ResizeRedraw = true;
        Height = 96;
        Cursor = Cursors.Hand;
    }

    public void SetItems(IReadOnlyList<Item> items)
    {
        if (_drag >= 0 || items.SequenceEqual(_items)) return;
        _items = items.ToList();
        Invalidate();
    }

    private int TileWidth => Math.Min(110, (Width - 16 - Gap * Math.Max(0, _items.Count - 1)) / Math.Max(1, _items.Count));
    private const int Gap = 14;
    private const int TileHeight = 60;

    private int StartX => (Width - (_items.Count * TileWidth + Gap * Math.Max(0, _items.Count - 1))) / 2;
    private int SlotX(int i) => StartX + i * (TileWidth + Gap);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        for (int i = 0; i < _items.Count; i++)
        {
            if (i == _drag) continue;
            DrawTile(g, _items[i], SlotX(i), dragging: false);
        }
        if (_drag >= 0) DrawTile(g, _items[_drag], _dragX, dragging: true);
    }

    private void DrawTile(Graphics g, Item item, int x, bool dragging)
    {
        var y = 6;
        var rect = new Rectangle(x, y, TileWidth, TileHeight);
        var fill = item.IsSelf ? VarcoIcon.Brand : item.Connected ? Color.FromArgb(0xEE, 0xED, 0xFE) : Color.FromArgb(0xF1, 0xEF, 0xE8);
        var border = item.IsSelf ? VarcoIcon.Brand : item.Connected ? Color.FromArgb(0x7F, 0x77, 0xDD) : Color.FromArgb(0xB4, 0xB2, 0xA9);
        var text = item.IsSelf ? Color.White : item.Connected ? Color.FromArgb(0x3C, 0x34, 0x89) : Color.FromArgb(0x5F, 0x5E, 0x5A);

        using (var path = Rounded(rect, 8))
        using (var b = new SolidBrush(fill))
        using (var p = new Pen(border, dragging ? 2.5f : 1.2f))
        {
            g.FillPath(b, path);
            g.DrawPath(p, path);
        }
        // piedistallo dello schermo
        using (var stand = new SolidBrush(border))
        {
            g.FillRectangle(stand, x + TileWidth / 2 - 3, y + TileHeight, 6, 8);
            using var foot = Rounded(new Rectangle(x + TileWidth / 2 - 16, y + TileHeight + 8, 32, 5), 2);
            g.FillPath(stand, foot);
        }
        var label = item.IsSelf ? $"{item.Name}\n(questo PC)" : item.Connected ? item.Name : $"{item.Name}\n(spento)";
        TextRenderer.DrawText(g, label, Font, Rectangle.Inflate(rect, -4, -2), text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis);
    }

    private static GraphicsPath Rounded(Rectangle r, int radius)
    {
        var p = new GraphicsPath();
        var d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || _items.Count < 2) return;
        for (int i = 0; i < _items.Count; i++)
        {
            if (e.X >= SlotX(i) && e.X < SlotX(i) + TileWidth)
            {
                _drag = i;
                _dragOffsetX = e.X - SlotX(i);
                _dragX = SlotX(i);
                _moved = false;
                Capture = true;
                return;
            }
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_drag < 0) return;
        _dragX = Math.Clamp(e.X - _dragOffsetX, 0, Math.Max(0, Width - TileWidth));
        // Sposto l'elemento nello slot più vicino al centro della tessera trascinata.
        var center = _dragX + TileWidth / 2;
        var target = Math.Clamp((center - StartX + Gap / 2) / (TileWidth + Gap), 0, _items.Count - 1);
        if (target != _drag)
        {
            var it = _items[_drag];
            _items.RemoveAt(_drag);
            _items.Insert(target, it);
            _drag = target;
            _moved = true;
        }
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_drag < 0) return;
        _drag = -1;
        Capture = false;
        Invalidate();
        if (_moved) OrderChanged?.Invoke(_items.Select(i => i.Id).ToList());
    }
}
