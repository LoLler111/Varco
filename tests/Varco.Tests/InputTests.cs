using Varco.Core.Input;
using Varco.Core.Net;
using Varco.Core.Protocol;

namespace Varco.Tests;

public class GeometryTests
{
    private static readonly IReadOnlyList<Rect> Two = [new Rect(0, 0, 1920, 1080), new Rect(1920, 0, 3840, 1080)];

    [Fact]
    public void Edge_only_on_outer_border()
    {
        Assert.Equal(Edge.Right, ScreenGeometry.DetectEdge(Two, 3839, 500));
        Assert.Equal(Edge.Left, ScreenGeometry.DetectEdge(Two, 0, 500));
        Assert.Null(ScreenGeometry.DetectEdge(Two, 1919, 500)); // confine fra i due monitor
        Assert.Null(ScreenGeometry.DetectEdge(Two, 1000, 500));
    }

    [Fact]
    public void MoveBy_crosses_monitors_and_reports_outer_edge()
    {
        Assert.Equal((1925, 10, (Edge?)null), ScreenGeometry.MoveBy(Two, 1915, 10, 10, 0));
        var (x, y, hit) = ScreenGeometry.MoveBy(Two, 3835, 10, 50, 0);
        Assert.Equal((3839, 10, (Edge?)Edge.Right), (x, y, hit));
    }

    [Fact]
    public void Entry_point_respects_ratio()
    {
        var (x, y) = ScreenGeometry.EntryPoint(Two, Edge.Left, 0.5f);
        Assert.Equal(ScreenGeometry.EntryInset, x);
        Assert.InRange(y, 538, 541);
        var (x2, _) = ScreenGeometry.EntryPoint(Two, Edge.Right, 0f);
        Assert.Equal(3839 - ScreenGeometry.EntryInset, x2);
    }
}

public class KeyCodeTests
{
    [Fact]
    public void Right_shift_is_never_injected_as_extended()
    {
        // Come lo segnala l'hook (misurato): vk A1, scancode 36, esteso
        Assert.Equal(KeyFlags.None, KeyCodes.ForInjection(0xA1, 0x36, KeyFlags.Extended));
        Assert.Equal(KeyFlags.Up, KeyCodes.ForInjection(0xA1, 0x36, KeyFlags.Extended | KeyFlags.Up));
        // Maiusc finti inoltrati (senza VK_RSHIFT): restano finti
        Assert.Equal(KeyFlags.Extended, KeyCodes.ForInjection(0xFF, 0x36, KeyFlags.Extended));
        Assert.Equal(KeyFlags.Extended, KeyCodes.ForInjection(0xA0, 0x2A, KeyFlags.Extended));
        // gli altri tasti estesi restano estesi: Ctrl destro, AltGr, frecce
        Assert.Equal(KeyFlags.Extended, KeyCodes.ForInjection(0xA3, 0x1D, KeyFlags.Extended));
        Assert.Equal(KeyFlags.Extended | KeyFlags.Up, KeyCodes.ForInjection(0xA5, 0x38, KeyFlags.Extended | KeyFlags.Up));
        Assert.Equal(KeyFlags.Extended, KeyCodes.ForInjection(0x26, 0x48, KeyFlags.Extended));
    }
}

public class LayoutTests
{
    private static readonly Guid A = Guid.NewGuid(), B = Guid.NewGuid(), C = Guid.NewGuid();

    /// <summary>Fisso 1920×1080 a sinistra, portatile 1366×768 a destra più in basso di 156 pixel.</summary>
    private static Layout DeskAndLaptop(int laptopY = 156) => new()
    {
        Entries =
        [
            new LayoutEntry { Id = A, Name = "Fisso", X = 0, Y = 0, Width = 1920, Height = 1080 },
            new LayoutEntry { Id = B, Name = "Portatile", X = 1920, Y = laptopY, Width = 1366, Height = 768 },
        ],
    };

    private static float RatioOfPixel(int pixel, int size) => (pixel + 0.5f) / size;

    [Fact]
    public void Crossing_keeps_the_same_height_with_offset_and_different_resolution()
    {
        var l = DeskAndLaptop();
        // Fisso, bordo destro, y=540 → virtuale 540.5 → sul portatile 540.5-156 = 384.5 → pixel 384
        var hop = l.Cross(A, Edge.Right, RatioOfPixel(540, 1080));
        Assert.NotNull(hop);
        Assert.Equal(B, hop.Value.Id);
        Assert.Equal(384, (int)(hop.Value.Ratio * 768));

        // e al ritorno si torna allo stesso pixel
        var back = l.Cross(B, Edge.Left, RatioOfPixel(384, 768));
        Assert.Equal(A, back!.Value.Id);
        Assert.Equal(540, (int)(back.Value.Ratio * 1080));
    }

    [Fact]
    public void No_crossing_where_nothing_is_adjacent()
    {
        var l = DeskAndLaptop();
        Assert.Null(l.Cross(A, Edge.Right, RatioOfPixel(100, 1080)));   // sopra il portatile
        Assert.Null(l.Cross(A, Edge.Right, RatioOfPixel(1000, 1080)));  // sotto il portatile (156+768=924)
        Assert.NotNull(l.Cross(A, Edge.Right, RatioOfPixel(156, 1080)));
        Assert.Null(l.Cross(A, Edge.Left, 0.5f));
        Assert.Null(l.Cross(A, Edge.Top, 0.5f));
        Assert.Null(l.Cross(B, Edge.Right, 0.5f));
        Assert.Null(l.Cross(Guid.NewGuid(), Edge.Right, 0.5f));
    }

    [Fact]
    public void Vertical_arrangement_and_gaps_between_rectangles()
    {
        var l = new Layout
        {
            Entries =
            [
                new LayoutEntry { Id = A, X = 0, Y = 0, Width = 1920, Height = 1080 },
                new LayoutEntry { Id = B, X = 960, Y = 1080, Width = 1366, Height = 768 }, // sotto, spostato a destra
                new LayoutEntry { Id = C, X = 1920 + 50, Y = 0, Width = 1920, Height = 1080 }, // staccato di 50: non tocca
            ],
        };
        var hop = l.Cross(A, Edge.Bottom, RatioOfPixel(1500, 1920));
        Assert.Equal(B, hop!.Value.Id);
        Assert.Equal(1500 - 960, (int)(hop.Value.Ratio * 1366));
        Assert.Null(l.Cross(A, Edge.Bottom, RatioOfPixel(500, 1920)));
        Assert.Null(l.Cross(A, Edge.Right, 0.5f));
    }

    [Fact]
    public void Old_grid_layout_is_converted()
    {
        var old = new Layout
        {
            Version = 7,
            Entries = [new LayoutEntry { Id = A, X = 0, Y = 0 }, new LayoutEntry { Id = B, X = 1, Y = 0 }],
        };
        var lm = new LayoutManager(A, "A", old);
        Assert.Equal(B, lm.Cross(A, Edge.Right, 0.5f)?.Id);
        Assert.Equal(A, lm.Cross(B, Edge.Left, 0.5f)?.Id);
        Assert.All(lm.Current.Entries, e => Assert.True(e.Width > 0 && e.Height > 0));
    }

    [Fact]
    public void Own_desktop_size_is_shared_and_keeps_neighbors_touching()
    {
        var lm = new LayoutManager(A, "A", new Layout());
        lm.EnsureMembers([(B, "B")]);
        var before = lm.Current.Version;
        lm.SetSelfSize(2560, 1440);
        var cur = lm.Current;
        Assert.True(cur.Version > before);
        Assert.Equal((2560, 1440), (cur.Find(A)!.Width, cur.Find(A)!.Height));
        Assert.Equal(2560, cur.Find(B)!.X); // spostato: tocca ancora il bordo destro
        Assert.Equal(B, lm.Cross(A, Edge.Right, 0.5f)?.Id);

        // stessa dimensione: niente nuova versione
        var v = lm.Current.Version;
        lm.SetSelfSize(2560, 1440);
        Assert.Equal(v, lm.Current.Version);
    }

    [Fact]
    public void Merge_newer_wins_keeps_members_and_repairs_own_size()
    {
        var la = new LayoutManager(A, "A", new Layout());
        la.SetSelfSize(1920, 1080);
        la.EnsureMembers([(B, "B")]);                    // A: [A, B (dimensione predefinita)]
        var lb = new LayoutManager(B, "B", new Layout());
        Assert.True(lb.Merge(la.Current, [(A, "A")]));   // B adotta
        lb.SetSelfSize(1366, 768);                       // e comunica la sua dimensione
        Assert.True(la.Merge(lb.Current, [(B, "B")]));
        Assert.Equal((1366, 768), (la.Current.Find(B)!.Width, la.Current.Find(B)!.Height));

        // layout più recente con la mia dimensione sbagliata: adottato e corretto subito, con una nuova versione
        var wrong = DeskAndLaptop();
        wrong.Version = 100;
        wrong.Entries[1].Width = 1920;
        wrong.Entries[1].Height = 1080;
        Assert.True(lb.Merge(wrong, [(A, "A")]));
        Assert.Equal((1366, 768), (lb.Current.Find(B)!.Width, lb.Current.Find(B)!.Height));
        Assert.Equal((101L, B), (lb.Current.Version, lb.Current.Author));
        Assert.True(la.Merge(lb.Current, [(B, "B")]));

        // l'utente su B abbassa il portatile
        lb.SetPositions([(B, 1920, 200)]);
        la.Merge(lb.Current, [(B, "B")]);
        Assert.Equal(200, la.Current.Find(B)!.Y);
        Assert.Null(la.Cross(A, Edge.Right, RatioOfPixel(100, 1080)));
        Assert.Equal(B, la.Cross(A, Edge.Right, RatioOfPixel(300, 1080))?.Id);

        // un layout vecchio non vince
        Assert.False(la.Merge(DeskAndLaptop(0), [(B, "B")]));

        // arriva C: in coda a destra, e nessuno sparisce
        la.EnsureMembers([(C, "C")]);
        Assert.Equal(3, la.Current.Entries.Count);
        Assert.Equal(C, la.Cross(B, Edge.Right, 0.5f)?.Id);
        Assert.False(lb.Current.IsNewerThan(la.Current));
    }

    [Fact]
    public void Set_positions_moves_to_origin()
    {
        var lm = new LayoutManager(A, "A", DeskAndLaptop());
        lm.SetPositions([(B, -1366, -100)]); // portatile a sinistra e più in alto
        var cur = lm.Current;
        Assert.Equal((1366, 100), (cur.Find(A)!.X, cur.Find(A)!.Y));
        Assert.Equal((0, 0), (cur.Find(B)!.X, cur.Find(B)!.Y));
        Assert.Equal(B, cur.Cross(A, Edge.Left, RatioOfPixel(500, 1080))?.Id);
    }

    [Fact]
    public void Snap_attaches_to_the_nearest_edge_without_overlap()
    {
        IReadOnlyList<Rect> desk = [new Rect(0, 0, 1920, 1080)];
        // lasciato un po' staccato a destra e in basso → si attacca al bordo destro, all'altezza scelta
        Assert.Equal((1920, 250), LayoutSnap.Snap(desk, 1366, 768, 2000, 250, 20));
        // vicino all'allineamento in alto → allineato
        Assert.Equal((1920, 0), LayoutSnap.Snap(desk, 1366, 768, 1950, 15, 20));
        // sovrapposto quasi tutto a sinistra → spinto fuori a sinistra
        Assert.Equal((-1366, 100), LayoutSnap.Snap(desk, 1366, 768, -900, 100, 20));
        // troppo in basso: resta un tratto di bordo in comune
        var (_, y) = LayoutSnap.Snap(desk, 1366, 768, 2300, 1300, 0);
        Assert.Equal(1080 - LayoutSnap.MinShared, y);
        // sotto, centrato
        Assert.Equal((300, 1080), LayoutSnap.Snap(desk, 1366, 768, 300, 1150, 0));
    }
}

public class CoordinatorTests
{
    private sealed class FakeNet(Guid self) : IPeerNetwork
    {
        public readonly List<(Guid To, Message Msg)> Sent = [];
        public readonly HashSet<Guid> Connected = [];
        public Guid SelfId { get; } = self;
        public bool IsConnected(Guid id) => Connected.Contains(id);
        public bool Send(Guid id, Message m) { Sent.Add((id, m)); return Connected.Contains(id); }
        public void Broadcast(Message m) { foreach (var c in Connected) Sent.Add((c, m)); }
    }

    private sealed class FakePlatform : IInputPlatform
    {
        public readonly List<string> Log = [];
        public (int X, int Y) Cursor = (100, 100);
        public void Post(Action action) => action();
        public IReadOnlyList<Rect> GetMonitors() => [new Rect(0, 0, 1920, 1080)];
        public (int X, int Y) GetCursorPos() => Cursor;
        public void SetCursorPos(int x, int y) { Cursor = (x, y); Log.Add($"set {x},{y}"); }
        public void InjectMoveTo(int x, int y) { Cursor = (x, y); Log.Add($"move {x},{y}"); }
        public void InjectButton(MouseButton b, bool down) => Log.Add($"btn {b} {down}");
        public void InjectWheel(int d, bool h) => Log.Add($"wheel {d}");
        public void InjectKey(ushort vk, ushort scan, KeyFlags f) => Log.Add($"key {vk:X2} {f}");
    }

    private static (InputCoordinator c, FakeNet net, FakePlatform p, Guid other) Setup(bool otherOnRight = true, int moveIntervalMs = 4)
    {
        var self = Guid.NewGuid();
        var other = Guid.NewGuid();
        var net = new FakeNet(self);
        net.Connected.Add(other);
        var layout = new LayoutManager(self, "me", new Layout());
        layout.SetRow(otherOnRight ? [(self, "me"), (other, "other")] : [(other, "other"), (self, "me")]);
        var p = new FakePlatform();
        return (new InputCoordinator(net, p, layout) { MoveIntervalMs = moveIntervalMs }, net, p, other);
    }

    [Fact]
    public void Crossing_right_edge_starts_controlling_and_forwards_input()
    {
        var (c, net, p, other) = Setup();
        Assert.False(c.OnLocalMouseMove(1000, 500));
        Assert.True(c.OnLocalMouseMove(1919, 540));
        Assert.Equal(InputMode.Controlling, c.Mode);
        Assert.Contains(net.Sent, s => s.To == other && s.Msg is Enter { From: Edge.Left });
        Assert.Equal((960, 540), p.Cursor); // cursore locale parcheggiato al centro

        Assert.True(c.OnLocalMouseMove(965, 538));
        Assert.Contains(net.Sent, s => s.Msg is MouseMove { Dx: 5, Dy: -2 });
        Assert.True(c.OnLocalKey(0x41, 0x1E, KeyFlags.None));
        Assert.Contains(net.Sent, s => s.Msg is KeyMsg { Vk: 0x41 });
    }

    [Fact]
    public void Rapid_moves_are_coalesced_and_flushed_before_clicks_and_when_the_mouse_stops()
    {
        var (c, net, _, other) = Setup(moveIntervalMs: 50); // intervallo largo: il test non dipende dalla velocità della macchina
        c.OnLocalMouseMove(1919, 540); // passa al vicino, cursore parcheggiato a 960,540
        int Moves() => net.Sent.Count(s => s.Msg is MouseMove);

        c.OnLocalMouseMove(965, 540);  // primo movimento: subito
        Assert.Equal(1, Moves());
        c.OnLocalMouseMove(963, 541);  // entro l'intervallo: sommati
        c.OnLocalMouseMove(964, 541);
        Assert.Equal(1, Moves());

        c.OnLocalButton(MouseButton.Left, true); // il clic porta con sé gli spostamenti in attesa, prima del clic
        var tail = net.Sent.SkipWhile(s => s.Msg is not MouseMove).Skip(1).Select(s => s.Msg).ToList();
        Assert.Equal(new MouseMove(7, 2), tail[0]);
        Assert.Equal(new MouseButtonMsg(MouseButton.Left, true), tail[1]);

        c.OnLocalMouseMove(961, 540);  // l'ultimo movimento prima di fermarsi arriva comunque
        Thread.Sleep(100);
        Assert.Contains(net.Sent, s => s.To == other && s.Msg is MouseMove { Dx: 1, Dy: 0 });
    }

    [Fact]
    public void Wheel_spam_is_coalesced_and_flushed_before_clicks_and_when_it_stops()
    {
        var self = Guid.NewGuid();
        var other = Guid.NewGuid();
        var net = new FakeNet(self);
        net.Connected.Add(other);
        var layout = new LayoutManager(self, "me", new Layout());
        layout.SetRow([(self, "me"), (other, "other")]);
        var c = new InputCoordinator(net, new FakePlatform(), layout) { WheelIntervalMs = 50 };
        c.OnLocalMouseMove(1919, 540);
        List<MouseWheel> Wheels() => net.Sent.Select(s => s.Msg).OfType<MouseWheel>().ToList();

        Assert.True(c.OnLocalWheel(120, false));        // primo scatto: subito
        Assert.Equal([new MouseWheel(120, false)], Wheels());
        for (int i = 0; i < 10; i++) c.OnLocalWheel(-120, false); // raffica: sommata
        c.OnLocalWheel(30, true);
        Assert.Single(Wheels());

        c.OnLocalButton(MouseButton.Middle, true);     // il clic arriva dopo lo scorrimento che lo precede
        var tail = net.Sent.Select(s => s.Msg).SkipWhile(m => m is not MouseWheel).Skip(1).ToList();
        Assert.Equal(new MouseWheel(-1200, false), tail[0]);
        Assert.Equal(new MouseWheel(30, true), tail[1]);
        Assert.Equal(new MouseButtonMsg(MouseButton.Middle, true), tail[2]);

        c.OnLocalWheel(-120, false);                   // l'ultimo scatto prima di fermarsi arriva comunque
        c.OnLocalWheel(-120, false);
        Thread.Sleep(150);
        Assert.Equal(new MouseWheel(-240, false), Wheels()[^1]);
    }

    [Fact]
    public void Target_merges_wheel_events_still_waiting_to_be_injected()
    {
        var p = new FakePlatform();
        var queued = new List<Action>();
        var self = Guid.NewGuid();
        var other = Guid.NewGuid();
        var net = new FakeNet(self);
        net.Connected.Add(other);
        var layout = new LayoutManager(self, "me", new Layout());
        layout.SetRow([(other, "other"), (self, "me")]);
        var c = new InputCoordinator(net, new DeferredPlatform(p, queued), layout);

        c.OnMessage(other, new Enter(Edge.Left, 0.5f), () => []);
        for (int i = 0; i < 20; i++) c.OnMessage(other, new MouseWheel(-120, false), () => []);
        c.OnMessage(other, new MouseMove(5, 0), () => []);
        c.OnMessage(other, new MouseWheel(120, false), () => []); // dopo il movimento: non va anticipato
        foreach (var a in queued) a();

        var log = p.Log.SkipWhile(l => !l.StartsWith("move")).Skip(1).ToList();
        Assert.Equal(["wheel -2400", $"move {ScreenGeometry.EntryInset + 5},540", "wheel 120"], log);
    }

    /// <summary>Piattaforma che accoda le azioni invece di eseguirle: simula il thread di input in ritardo.</summary>
    private sealed class DeferredPlatform(FakePlatform inner, List<Action> queue) : IInputPlatform
    {
        public void Post(Action action) => queue.Add(action);
        public IReadOnlyList<Rect> GetMonitors() => inner.GetMonitors();
        public (int X, int Y) GetCursorPos() => inner.GetCursorPos();
        public void SetCursorPos(int x, int y) => inner.SetCursorPos(x, y);
        public void InjectMoveTo(int x, int y) => inner.InjectMoveTo(x, y);
        public void InjectButton(MouseButton b, bool down) => inner.InjectButton(b, down);
        public void InjectWheel(int d, bool h) => inner.InjectWheel(d, h);
        public void InjectKey(ushort vk, ushort scan, KeyFlags f) => inner.InjectKey(vk, scan, f);
    }

    [Fact]
    public void No_crossing_while_dragging_or_when_neighbor_offline()
    {
        var (c, net, _, other) = Setup();
        c.OnLocalButton(MouseButton.Left, true);
        Assert.False(c.OnLocalMouseMove(1919, 540));
        c.OnLocalButton(MouseButton.Left, false);
        net.Connected.Remove(other);
        Assert.False(c.OnLocalMouseMove(1919, 540));
        Assert.Equal(InputMode.Local, c.Mode);
    }

    [Fact]
    public void Disconnect_while_controlling_returns_cursor_home()
    {
        var (c, _, p, other) = Setup();
        c.OnLocalMouseMove(1919, 300);
        c.OnPeerDisconnected(other);
        Assert.Equal(InputMode.Local, c.Mode);
        Assert.Equal((1919, 300), p.Cursor);
    }

    [Fact]
    public void Controlled_side_releases_stuck_keys_on_disconnect()
    {
        var (c, _, p, other) = Setup();
        c.OnMessage(other, new Enter(Edge.Right, 0.5f), () => []);
        Assert.Equal(InputMode.Controlled, c.Mode);
        c.OnMessage(other, new KeyMsg(0xA2, 0x1D, KeyFlags.None), () => []); // Ctrl giù
        c.OnMessage(other, new MouseButtonMsg(MouseButton.Left, true), () => []);
        c.OnPeerDisconnected(other);
        Assert.Equal(InputMode.Local, c.Mode);
        Assert.Contains("key A2 Up", p.Log);
        Assert.Contains("btn Left False", p.Log);
    }

    [Fact]
    public void Edge_hit_on_target_brings_control_back()
    {
        var (c, net, p, other) = Setup();
        c.OnLocalMouseMove(1919, 540);
        c.OnMessage(other, new EdgeHit(Edge.Left, 0.25f), () => []);
        Assert.Equal(InputMode.Local, c.Mode);
        Assert.Contains(net.Sent, s => s.To == other && s.Msg is Leave);
        Assert.Equal(1919 - ScreenGeometry.EntryInset, p.Cursor.X);
    }

    [Fact]
    public void Target_reports_edge_once_and_physical_input_takes_over()
    {
        var (c, net, _, other) = Setup(otherOnRight: false);
        c.OnMessage(other, new Enter(Edge.Left, 0.5f), () => []);
        c.OnMessage(other, new MouseMove(-50, 0), () => []);
        c.OnMessage(other, new MouseMove(-50, 0), () => []);
        Assert.Single(net.Sent, s => s.Msg is EdgeHit { Edge: Edge.Left });

        Assert.False(c.OnLocalMouseMove(10, 10)); // qualcuno muove il mouse fisico del target
        Assert.Equal(InputMode.Local, c.Mode);
        Assert.Contains(net.Sent, s => s.To == other && s.Msg is TakeOver);
    }

    [Fact]
    public void Key_held_before_crossing_is_released_locally()
    {
        var (c, net, _, _) = Setup();
        Assert.False(c.OnLocalKey(0xA0, 0x2A, KeyFlags.None)); // Shift giù in locale
        c.OnLocalMouseMove(1919, 540);
        Assert.True(c.OnLocalKey(0xA0, 0x2A, KeyFlags.None)); // ripetizione: bloccata e non inviata
        Assert.False(c.OnLocalKey(0xA0, 0x2A, KeyFlags.Up));  // rilascio: al PC locale
        Assert.DoesNotContain(net.Sent, s => s.Msg is KeyMsg { Vk: 0xA0 });
    }

    [Fact]
    public void Ctrl_Alt_F12_always_returns_home()
    {
        var (c, net, _, other) = Setup();
        c.OnLocalMouseMove(1919, 540);
        c.OnLocalKey(0xA2, 0x1D, KeyFlags.None);
        c.OnLocalKey(0xA4, 0x38, KeyFlags.None);
        Assert.True(c.OnLocalKey(0x7B, 0x58, KeyFlags.None));
        Assert.Equal(InputMode.Local, c.Mode);
        Assert.Contains(net.Sent, s => s.To == other && s.Msg is Leave);
        // i rilasci di Ctrl/Alt non arrivano al PC locale, che non li aveva visti premere
        Assert.True(c.OnLocalKey(0xA2, 0x1D, KeyFlags.Up));
    }

    /// <summary>Questo PC 1920×1080; l'altro (1366×768) a destra, più in basso di 156 pixel.</summary>
    private static (InputCoordinator c, FakeNet net, FakePlatform p, Guid other) Setup2D()
    {
        var self = Guid.NewGuid();
        var other = Guid.NewGuid();
        var net = new FakeNet(self);
        net.Connected.Add(other);
        var layout = new LayoutManager(self, "me", new Layout
        {
            Version = 1,
            Entries =
            [
                new LayoutEntry { Id = self, Name = "me", X = 0, Y = 0, Width = 1920, Height = 1080 },
                new LayoutEntry { Id = other, Name = "other", X = 1920, Y = 156, Width = 1366, Height = 768 },
            ],
        });
        var p = new FakePlatform();
        return (new InputCoordinator(net, p, layout), net, p, other);
    }

    [Fact]
    public void Crossing_honors_vertical_offset_and_gaps()
    {
        var (c, net, _, other) = Setup2D();
        Assert.False(c.OnLocalMouseMove(1919, 100));  // lì a destra non c'è nessuno
        Assert.False(c.OnLocalMouseMove(1919, 1000));
        Assert.Equal(InputMode.Local, c.Mode);

        Assert.True(c.OnLocalMouseMove(1919, 540));
        var enter = Assert.Single(net.Sent.Where(s => s.To == other).Select(s => s.Msg).OfType<Enter>());
        Assert.Equal(Edge.Left, enter.From);
        Assert.Equal(540 - 156, (int)(enter.Ratio * 768)); // stessa altezza fisica sul portatile
    }

    [Fact]
    public void Edge_hit_returns_at_the_same_height()
    {
        var (c, net, p, other) = Setup2D();
        c.OnLocalMouseMove(1919, 540);
        c.OnMessage(other, new EdgeHit(Edge.Left, (700 + 0.5f) / 768), () => []);
        Assert.Equal(InputMode.Local, c.Mode);
        Assert.Contains(net.Sent, s => s.To == other && s.Msg is Leave);
        Assert.Equal((1919 - ScreenGeometry.EntryInset, 700 + 156), p.Cursor);
    }

    [Fact]
    public void Target_sends_edge_hit_only_where_a_neighbor_touches()
    {
        var (c, net, p, other) = Setup2D();
        c.OnMessage(other, new Enter(Edge.Right, (50 + 0.5f) / 1080), () => []);
        Assert.Equal((1919 - ScreenGeometry.EntryInset, 50), p.Cursor);
        c.OnMessage(other, new MouseMove(50, 0), () => []);   // spinge a destra dove non c'è nessuno
        Assert.DoesNotContain(net.Sent, s => s.Msg is EdgeHit);
        c.OnMessage(other, new MouseMove(50, 490), () => []); // scorre lungo il bordo fino al portatile
        var hit = Assert.Single(net.Sent.Select(s => s.Msg).OfType<EdgeHit>());
        Assert.Equal(Edge.Right, hit.Edge);
        Assert.Equal(540, (int)(hit.Ratio * 1080));
    }
}
