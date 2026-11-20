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

    [Fact]
    public void AltGr_fake_ctrl_is_recognized()
    {
        // Come lo segnala l'hook (misurato) dopo AltGr iniettato: vk A2, scancode 21D
        Assert.True(KeyCodes.IsAltGrFakeCtrl(0xA2, 0x21D));
        // Ctrl sinistro vero, Ctrl destro e AltGr stesso: no
        Assert.False(KeyCodes.IsAltGrFakeCtrl(0xA2, 0x1D));
        Assert.False(KeyCodes.IsAltGrFakeCtrl(0xA3, 0x1D));
        Assert.False(KeyCodes.IsAltGrFakeCtrl(0xA5, 0x38));
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

    /// <summary>Fisso con due monitor uno sopra l'altro (3440×1440 e, sotto a sinistra, 1920×1080); portatile 1366×768.</summary>
    private static LayoutEntry TwoMonitorDesk() => new()
    {
        Id = A, Name = "Fisso", X = 0, Y = 0, Width = 3440, Height = 2520,
        Monitors = [new LayoutMonitor(0, 0, 3440, 1440), new LayoutMonitor(0, 1440, 1920, 1080)],
    };

    [Fact]
    public void Crossing_uses_the_monitors_not_the_bounding_box()
    {
        // Portatile a destra del monitor in basso (x = 1920), dentro il rettangolo complessivo del fisso.
        var l = new Layout { Entries = [TwoMonitorDesk(), new LayoutEntry { Id = B, X = 1920, Y = 1600, Width = 1366, Height = 768 }] };
        var hop = l.Cross(A, Edge.Right, RatioOfPixel(1700, 2520));
        Assert.Equal(B, hop?.Id);
        Assert.Equal(1700 - 1600, (int)(hop!.Value.Ratio * 768));
        Assert.Equal(A, l.Cross(B, Edge.Left, RatioOfPixel(100, 768))?.Id);
        // dal monitor in alto verso destra non c'è nessuno (il portatile tocca solo quello in basso)
        Assert.Null(l.Cross(A, Edge.Right, RatioOfPixel(700, 2520)));
        // il portatile all'estrema destra del rettangolo, in basso: non tocca nessun monitor
        var far = new Layout { Entries = [TwoMonitorDesk(), new LayoutEntry { Id = B, X = 3440, Y = 1600, Width = 1366, Height = 768 }] };
        Assert.Null(far.Cross(A, Edge.Right, RatioOfPixel(1700, 2520)));
        Assert.Null(far.Cross(B, Edge.Left, RatioOfPixel(100, 768)));
    }

    [Fact]
    public void Snap_attaches_to_the_monitors_of_the_other_pc()
    {
        var others = TwoMonitorDesk().Screens();
        // lasciato nel vuoto in basso a destra (sotto il 3440, a destra del 1920) → si attacca a uno dei due monitor
        var (x, y) = LayoutSnap.Snap(others, 1366, 768, 2000, 1600, 0);
        Assert.Equal((1920, 1600), (x, y));
        // un PC con due monitor affiancati si aggancia con quello giusto senza sovrapporsi
        IReadOnlyList<Rect> mine = [new Rect(0, 0, 1000, 800), new Rect(1000, 0, 2000, 800)];
        var (px, py) = LayoutSnap.Snap(others, mine, 3500, 100, 0);
        Assert.Equal((3440, 100), (px, py));
    }

    [Fact]
    public void Self_monitors_are_published_relative_to_the_desktop_corner()
    {
        var lm = new LayoutManager(A, "A", new Layout());
        // primario a destra: Windows dà coordinate negative al monitor di sinistra
        lm.SetSelfMonitors([new Rect(0, 0, 2560, 1440), new Rect(-1920, 200, 0, 1280)]);
        var me = lm.Current.Find(A)!;
        Assert.Equal((4480, 1440), (me.Width, me.Height));
        Assert.Equal([new LayoutMonitor(1920, 0, 2560, 1440), new LayoutMonitor(0, 200, 1920, 1080)], me.Monitors);
        var v = lm.Current.Version;
        lm.SetSelfMonitors([new Rect(0, 0, 2560, 1440), new Rect(-1920, 200, 0, 1280)]);
        Assert.Equal(v, lm.Current.Version); // invariati: niente propagazione
        lm.SetSelfMonitors([new Rect(0, 0, 2560, 1440), new Rect(-1920, 0, 0, 1080)]); // spostato in Impostazioni
        Assert.Equal(new LayoutMonitor(0, 0, 1920, 1080), lm.Current.Find(A)!.Monitors[1]);
    }

    [Fact]
    public void Monitors_survive_the_wire_format()
    {
        var l = new Layout { Entries = [TwoMonitorDesk()] };
        var back = (LayoutMsg)MessageCodec.Decode(MessageCodec.Encode(new LayoutMsg(l)))!;
        Assert.Equal(l.Entries[0].Monitors, back.Layout.Entries[0].Monitors);
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
        public LockKeys Locks = LockKeys.Num;
        public LockKeys? GetLockKeys() => Locks;
        public void SetLockKeys(LockKeys state) { Locks = state; Log.Add($"locks {state}"); }
        public void Post(Action action) => action();
        public IReadOnlyList<Rect> GetMonitors() => [new Rect(0, 0, 1920, 1080)];
        public (int X, int Y) GetCursorPos() => Cursor;
        public void SetCursorPos(int x, int y) { Cursor = (x, y); Log.Add($"set {x},{y}"); }
        public void InjectMoveTo(int x, int y) { Cursor = (x, y); Log.Add($"move {x},{y}"); }
        public void InjectButton(MouseButton b, bool down) => Log.Add($"btn {b} {down}");
        public void InjectWheel(int d, bool h) => Log.Add($"wheel {d}");
        public void InjectKey(ushort vk, ushort scan, KeyFlags f) => Log.Add($"key {vk:X2} {f}");
        public void SendSecureAttention() => Log.Add("sas");
        public void CancelLocalSecureScreen() => Log.Add("cancel secure screen");
        public bool Fullscreen;
        public bool IsFullscreenAppAt(int x, int y) => Fullscreen;
        public void CancelLocalDrag() => Log.Add("cancel drag");
        public void HideIdleCursor() => Log.Add("idle hidden");
        public bool MovingWindow;
        public bool IsMovingWindow() => MovingWindow;
        /// <summary>Come Windows con la finestra che nasconde il cursore: il cursore resta dov'è fino a CompletePark.</summary>
        public bool DeferPark;
        public (int X, int Y) ParkCursor(int x, int y)
        {
            if (!DeferPark) { SetCursorPos(x, y); return (x, y); }
            Log.Add("park pending");
            return Cursor;
        }
    }

    private sealed class FakeDrag : IFileDrag
    {
        public readonly List<string> Log = [];
        public bool Can = true;
        public bool CanDragTo(Guid target) => Can;
        public void Probe(Guid target) => Log.Add("probe");
        public void EndProbe() => Log.Add("end");
        public void Incoming(Guid from, byte[] offer) => Log.Add($"incoming {offer.Length}");
        public void CancelIncoming() => Log.Add("cancel");
    }

    private static (InputCoordinator c, FakeNet net, FakePlatform p, Guid other) Setup(bool otherOnRight = true, int moveIntervalMs = 4,
        int windowMoveIntervalMs = 16)
    {
        var self = Guid.NewGuid();
        var other = Guid.NewGuid();
        var net = new FakeNet(self);
        net.Connected.Add(other);
        var layout = new LayoutManager(self, "me", new Layout());
        layout.SetRow(otherOnRight ? [(self, "me"), (other, "other")] : [(other, "other"), (self, "me")]);
        var p = new FakePlatform();
        return (new InputCoordinator(net, p, layout) { MoveIntervalMs = moveIntervalMs, WindowMoveIntervalMs = windowMoveIntervalMs },
            net, p, other);
    }

    [Fact]
    public void Moving_a_window_on_the_target_injects_only_the_latest_position()
    {
        var (c, _, p, other) = Setup(windowMoveIntervalMs: 300);
        c.OnMessage(other, new Enter(Edge.Left, 0.5f), () => []);
        var (x, y) = p.Cursor;
        c.OnMessage(other, new MouseButtonMsg(MouseButton.Left, true), () => []);
        p.MovingWindow = true;
        p.Log.Clear();
        c.OnMessage(other, new MouseMove(10, 0), () => []);
        c.OnMessage(other, new MouseMove(10, 0), () => []);
        c.OnMessage(other, new MouseMove(10, 5), () => []);
        Assert.Empty(p.Log); // appena entrati: tutto rimandato
        // Il rilascio arriva dopo l'ultima posizione, non dove era rimasto il cursore.
        c.OnMessage(other, new MouseButtonMsg(MouseButton.Left, false), () => []);
        Assert.Equal([$"move {x + 30},{y + 5}", "btn Left False"], p.Log);
    }

    [Fact]
    public void Held_back_window_move_is_injected_when_the_mouse_stops()
    {
        var (c, _, p, other) = Setup(windowMoveIntervalMs: 50);
        c.OnMessage(other, new Enter(Edge.Left, 0.5f), () => []);
        var (x, y) = p.Cursor;
        p.MovingWindow = true;
        p.Log.Clear();
        c.OnMessage(other, new MouseMove(10, 0), () => []);
        c.OnMessage(other, new MouseMove(10, 0), () => []);
        Thread.Sleep(400);
        Assert.Equal([$"move {x + 20},{y}"], p.Log);
    }

    [Fact]
    public void Without_a_window_move_every_position_is_injected()
    {
        var (c, _, p, other) = Setup(windowMoveIntervalMs: 300);
        c.OnMessage(other, new Enter(Edge.Left, 0.5f), () => []);
        var (x, y) = p.Cursor;
        p.Log.Clear();
        c.OnMessage(other, new MouseMove(10, 0), () => []);
        c.OnMessage(other, new MouseMove(10, 0), () => []);
        Assert.Equal([$"move {x + 10},{y}", $"move {x + 20},{y}"], p.Log);
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
    public void Desktop_switch_while_controlling_reparks_the_cursor()
    {
        var (c, net, p, _) = Setup(moveIntervalMs: 0);
        c.OnLocalMouseMove(1919, 540);
        Assert.Equal(InputMode.Controlling, c.Mode);

        // Durante il passaggio a Winlogon (UAC) gli hook non c'erano: il mouse fisico ha spostato davvero il cursore.
        p.Cursor = (1200, 700);
        c.OnInputDesktopChanged();
        Assert.Equal((960, 540), p.Cursor);

        net.Sent.Clear();
        c.OnLocalMouseMove(963, 541);
        Assert.Contains(net.Sent, s => s.Msg is MouseMove { Dx: 3, Dy: 1 });
    }

    [Fact]
    public void Desktop_switch_on_the_target_rereads_the_real_cursor()
    {
        var (c, _, p, other) = Setup();
        c.OnMessage(other, new Enter(Edge.Left, 0.5f), () => []);
        p.Cursor = (500, 400); // il cursore vero non è dove pensava l'agent (iniezioni perse durante il cambio di desktop)
        c.OnInputDesktopChanged();
        c.OnMessage(other, new MouseMove(10, 0), () => []);
        Assert.Equal((510, 400), p.Cursor);
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
        public LockKeys? GetLockKeys() => inner.GetLockKeys();
        public void SetLockKeys(LockKeys state) => inner.SetLockKeys(state);
    }

    [Fact]
    public void Lock_keys_message_survives_the_codec()
    {
        var m = new LockKeysMsg(LockKeys.Caps | LockKeys.Scroll);
        Assert.Equal(m, MessageCodec.Decode(MessageCodec.Encode(m)));
    }

    [Fact]
    public void Crossing_sends_the_local_lock_keys_right_after_enter()
    {
        var (c, net, p, other) = Setup();
        p.Locks = LockKeys.Caps | LockKeys.Num;
        c.OnLocalMouseMove(1919, 540);
        var toOther = net.Sent.Where(s => s.To == other).Select(s => s.Msg).ToList();
        Assert.IsType<Enter>(toOther[^2]);
        Assert.Equal(new LockKeysMsg(LockKeys.Caps | LockKeys.Num), toOther[^1]);
    }

    [Fact]
    public void Lock_key_while_controlling_toggles_both_pcs()
    {
        var (c, net, _, other) = Setup();
        c.OnLocalMouseMove(1919, 540);
        Assert.False(c.OnLocalKey(KeyCodes.VK_CAPITAL, 0x3A, KeyFlags.None)); // passa anche al PC locale…
        Assert.Contains(net.Sent, s => s.To == other && s.Msg is KeyMsg { Vk: KeyCodes.VK_CAPITAL, Flags: KeyFlags.None }); // …e va al target

        // Il controllo torna qui prima del rilascio: il PC locale, che l'ha visto premere, deve vederlo rilasciare.
        c.OnMessage(other, new EdgeHit(Edge.Left, 0.5f), () => []);
        Assert.Equal(InputMode.Local, c.Mode);
        Assert.False(c.OnLocalKey(KeyCodes.VK_CAPITAL, 0x3A, KeyFlags.Up));
    }

    [Fact]
    public void Target_copies_lock_keys_only_from_its_controller()
    {
        var (c, _, p, other) = Setup();
        c.OnMessage(other, new LockKeysMsg(LockKeys.Caps), () => []); // non controllato: ignorato
        Assert.Equal(LockKeys.Num, p.Locks);

        c.OnMessage(other, new Enter(Edge.Right, 0.5f), () => []);
        c.OnMessage(Guid.NewGuid(), new LockKeysMsg(LockKeys.Scroll), () => []); // da un altro PC: ignorato
        c.OnMessage(other, new LockKeysMsg(LockKeys.Caps), () => []);
        Assert.Equal(LockKeys.Caps, p.Locks);
    }

    [Fact]
    public void Lock_keys_follow_the_cursor_to_the_next_pc()
    {
        var self = Guid.NewGuid();
        var mid = Guid.NewGuid();
        var far = Guid.NewGuid();
        var net = new FakeNet(self);
        net.Connected.UnionWith([mid, far]);
        var layout = new LayoutManager(self, "me", new Layout());
        layout.SetRow([(self, "me"), (mid, "mid"), (far, "far")]);
        var p = new FakePlatform { Locks = LockKeys.Caps };
        var c = new InputCoordinator(net, p, layout);

        c.OnLocalMouseMove(1919, 540);
        c.OnMessage(mid, new EdgeHit(Edge.Right, 0.5f), () => []);
        Assert.Equal(far, c.ActivePeer);
        Assert.Contains(net.Sent, s => s.To == far && s.Msg is LockKeysMsg { State: LockKeys.Caps });
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
    public void Returning_home_while_the_mouse_moves_does_not_restart_from_the_center()
    {
        var p = new FakePlatform();
        var queued = new List<Action>();
        var self = Guid.NewGuid();
        var other = Guid.NewGuid();
        var net = new FakeNet(self);
        net.Connected.Add(other);
        var layout = new LayoutManager(self, "me", new Layout());
        layout.SetRow([(self, "me"), (other, "other")]);
        var c = new InputCoordinator(net, new DeferredPlatform(p, queued), layout);

        c.OnLocalMouseMove(1919, 540); // parcheggiato al centro (dal thread degli hook, subito)
        Assert.Equal((960, 540), p.Cursor);
        c.OnMessage(other, new EdgeHit(Edge.Left, 0.25f), () => []); // il controllo torna qui
        Assert.Equal(InputMode.Local, c.Mode);

        // Il mouse si stava muovendo: Windows aveva già calcolato il prossimo evento partendo dal centro.
        Assert.True(c.OnLocalMouseMove(955, 540)); // scartato…
        var home = (1919 - ScreenGeometry.EntryInset, 270);
        Assert.Equal(home, p.Cursor);              // …e il cursore va sul bordo
        foreach (var a in queued) a();             // lo spostamento accodato non serve più
        Assert.Equal(home, p.Cursor);

        Assert.False(c.OnLocalMouseMove(home.Item1 - 3, 270)); // i movimenti dopo passano normalmente
    }

    [Fact]
    public void Returning_home_with_the_mouse_still_moves_the_cursor()
    {
        var (c, _, p, other) = Setup();
        c.OnLocalMouseMove(1919, 540);
        c.OnMessage(other, new EdgeHit(Edge.Left, 0.25f), () => []);
        Assert.Equal((1919 - ScreenGeometry.EntryInset, 270), p.Cursor);
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

    [Fact]
    public void Corners_of_the_monitor_do_not_cross()
    {
        var (c, _, _, _) = Setup();
        Assert.False(c.OnLocalMouseMove(1919, 5)); // la X di una finestra massimizzata
        Assert.False(c.OnLocalMouseMove(1919, 1075)); // angolo in basso
        Assert.Equal(InputMode.Local, c.Mode);
        Assert.True(c.OnLocalMouseMove(1919, 60));
        Assert.Equal(InputMode.Controlling, c.Mode);

        var (c2, _, _, _) = Setup();
        c2.EdgeSettings = () => new EdgeSettings(CornerGuard: false);
        Assert.True(c2.OnLocalMouseMove(1919, 5));
    }

    [Fact]
    public void Scroll_lock_and_fullscreen_apps_keep_the_cursor_here()
    {
        var (c, _, p, _) = Setup();
        p.Locks = LockKeys.Scroll;
        Assert.False(c.OnLocalMouseMove(1919, 540));
        p.Locks = LockKeys.None;
        p.Fullscreen = true;
        Assert.False(c.OnLocalMouseMove(1919, 540));
        Assert.Equal(InputMode.Local, c.Mode);

        c.EdgeSettings = () => new EdgeSettings(BlockFullscreen: false);
        Assert.True(c.OnLocalMouseMove(1919, 540));
    }

    [Fact]
    public void With_a_delay_the_cursor_must_rest_against_the_edge()
    {
        var (c, _, _, _) = Setup();
        c.EdgeSettings = () => new EdgeSettings(DelayMs: 150);
        Assert.False(c.OnLocalMouseMove(1919, 540)); // primo contatto
        Assert.False(c.OnLocalMouseMove(1919, 541)); // spinta subito dopo: ancora no
        Thread.Sleep(200);
        Assert.True(c.OnLocalMouseMove(1919, 542));
        Assert.Equal(InputMode.Controlling, c.Mode);

        // Lasciare il bordo azzera l'attesa.
        var (c2, _, _, _) = Setup();
        c2.EdgeSettings = () => new EdgeSettings(DelayMs: 150);
        c2.OnLocalMouseMove(1919, 540);
        Thread.Sleep(200);
        c2.OnLocalMouseMove(1800, 540);
        Assert.False(c2.OnLocalMouseMove(1919, 540));
    }

    [Fact]
    public void The_controlled_pc_applies_its_own_edge_rules()
    {
        var (t, net, p, controller) = Setup(otherOnRight: false);
        t.OnMessage(controller, new Enter(Edge.Left, 0.5f), () => []);
        p.Locks = LockKeys.Scroll; // Bloc Scorr acceso (sincronizzato dal PC che comanda)
        t.OnMessage(controller, new MouseMove(-5000, 0), () => []);
        Assert.DoesNotContain(net.Sent, s => s.Msg is EdgeHit);

        p.Locks = LockKeys.None;
        t.OnMessage(controller, new MouseMove(10, 0), () => []);
        t.OnMessage(controller, new MouseMove(-5000, 0), () => []);
        Assert.Contains(net.Sent, s => s.To == controller && s.Msg is EdgeHit { Edge: Edge.Left });
    }

    [Fact]
    public void Real_ctrl_alt_del_while_controlling_goes_to_the_target_and_closes_the_local_screen()
    {
        var (c, net, p, other) = Setup();
        c.OnLocalMouseMove(1919, 540);
        c.OnLocalKey(0xA2, 0x1D, KeyFlags.None);
        c.OnLocalKey(0xA4, 0x38, KeyFlags.None);
        net.Sent.Clear();

        // Canc non arriva agli hook: Windows apre la schermata di sicurezza qui, il desktop passa a Winlogon.
        c.OnInputDesktopChanged("Default", "Winlogon");
        Assert.Contains(net.Sent, s => s.To == other && s.Msg is KeyMsg { Vk: 0xA2, Scan: 0x1D } k && k.Flags.HasFlag(KeyFlags.Up));
        Assert.Contains(net.Sent, s => s.To == other && s.Msg is KeyMsg { Vk: 0xA4, Scan: 0x38 } k && k.Flags.HasFlag(KeyFlags.Up));
        Assert.Single(net.Sent, s => s.To == other && s.Msg is SecureAttention);
        Assert.Contains("cancel secure screen", p.Log);
        Assert.Equal(InputMode.Controlling, c.Mode);

        // Esc ha chiuso la schermata: il ritorno al desktop normale non fa altro.
        net.Sent.Clear();
        c.OnInputDesktopChanged("Winlogon", "Default");
        Assert.DoesNotContain(net.Sent, s => s.Msg is SecureAttention);
    }

    [Fact]
    public void Desktop_switch_without_ctrl_alt_is_not_a_ctrl_alt_del()
    {
        var (c, net, p, _) = Setup();
        c.OnLocalMouseMove(1919, 540);
        c.OnInputDesktopChanged("Default", "Winlogon"); // UAC sul PC che comanda
        Assert.DoesNotContain(net.Sent, s => s.Msg is SecureAttention);

        // Win+Ctrl+Alt: non è Ctrl+Alt+Canc (Win+L blocca il PC, anche quello non passa dagli hook).
        c.OnInputDesktopChanged("Winlogon", "Default");
        c.OnLocalKey(0x5B, 0x5B, KeyFlags.Extended);
        c.OnLocalKey(0xA2, 0x1D, KeyFlags.None);
        c.OnLocalKey(0xA4, 0x38, KeyFlags.None);
        c.OnInputDesktopChanged("Default", "Winlogon");
        Assert.DoesNotContain(net.Sent, s => s.Msg is SecureAttention);
        Assert.DoesNotContain("cancel secure screen", p.Log);

        // Ctrl+Alt rilasciati da un pezzo: nemmeno.
        var (c2, net2, p2, _) = Setup();
        c2.OnLocalMouseMove(1919, 540);
        c2.OnLocalKey(0xA2, 0x1D, KeyFlags.None);
        c2.OnLocalKey(0xA4, 0x38, KeyFlags.None);
        c2.OnLocalKey(0xA4, 0x38, KeyFlags.Up);
        c2.OnLocalKey(0xA2, 0x1D, KeyFlags.Up);
        Thread.Sleep(600);
        c2.OnInputDesktopChanged("Default", "Winlogon");
        Assert.DoesNotContain(net2.Sent, s => s.Msg is SecureAttention);
        Assert.DoesNotContain("cancel secure screen", p2.Log);
    }

    [Fact]
    public void AltGr_is_not_ctrl_alt()
    {
        // AltGr (tastiera italiana): Windows lo manda come Ctrl sinistro finto (scancode 0x21D) più Alt destro.
        var (c, net, p, _) = Setup();
        c.OnLocalMouseMove(1919, 540);
        c.OnLocalKey(0xA2, 0x21D, KeyFlags.None);
        c.OnLocalKey(0xA5, 0x38, KeyFlags.Extended);
        c.OnInputDesktopChanged("Default", "Winlogon"); // per esempio una richiesta UAC mentre si scrive @
        Assert.DoesNotContain(net.Sent, s => s.Msg is SecureAttention);
        Assert.DoesNotContain("cancel secure screen", p.Log);

        // Su un layout senza AltGr l'Alt destro è un Alt: Ctrl+Alt destro vale.
        var (c2, net2, p2, _) = Setup();
        c2.OnLocalMouseMove(1919, 540);
        c2.OnLocalKey(0xA2, 0x1D, KeyFlags.None);
        c2.OnLocalKey(0xA5, 0x38, KeyFlags.Extended);
        c2.OnInputDesktopChanged("Default", "Winlogon");
        Assert.Single(net2.Sent, s => s.Msg is SecureAttention);
        Assert.Contains("cancel secure screen", p2.Log);

        // Ctrl vero più Alt sinistro mentre AltGr è appena stato rilasciato: vale.
        var (c3, net3, _, _) = Setup();
        c3.OnLocalMouseMove(1919, 540);
        c3.OnLocalKey(0xA2, 0x21D, KeyFlags.None);
        c3.OnLocalKey(0xA5, 0x38, KeyFlags.Extended);
        c3.OnLocalKey(0xA5, 0x38, KeyFlags.Extended | KeyFlags.Up);
        c3.OnLocalKey(0xA2, 0x21D, KeyFlags.Up);
        c3.OnLocalKey(0xA3, 0x1D, KeyFlags.Extended);
        c3.OnLocalKey(0xA4, 0x38, KeyFlags.None);
        c3.OnInputDesktopChanged("Default", "Winlogon");
        Assert.Single(net3.Sent, s => s.Msg is SecureAttention);
    }

    [Fact]
    public void Ctrl_alt_del_on_the_local_pc_is_left_alone_when_not_controlling()
    {
        var (c, net, p, _) = Setup();
        c.OnLocalKey(0xA2, 0x1D, KeyFlags.None);
        c.OnLocalKey(0xA4, 0x38, KeyFlags.None);
        c.OnInputDesktopChanged("Default", "Winlogon");
        Assert.DoesNotContain(net.Sent, s => s.Msg is SecureAttention);
        Assert.DoesNotContain("cancel secure screen", p.Log);
    }

    [Fact]
    public void Ctrl_Alt_End_sends_ctrl_alt_del_once_and_only_the_controller_can_ask()
    {
        var (c, net, _, other) = Setup();
        c.OnLocalMouseMove(1919, 540);
        c.OnLocalKey(0xA2, 0x1D, KeyFlags.None);
        c.OnLocalKey(0xA4, 0x38, KeyFlags.None);
        net.Sent.Clear();
        Assert.True(c.OnLocalKey(0x23, 0x4F, KeyFlags.Extended));
        Assert.True(c.OnLocalKey(0x23, 0x4F, KeyFlags.Extended)); // ripetizione del tasto tenuto premuto
        Assert.True(c.OnLocalKey(0x23, 0x4F, KeyFlags.Extended | KeyFlags.Up));
        Assert.Single(net.Sent, s => s.To == other && s.Msg is SecureAttention);
        Assert.DoesNotContain(net.Sent, s => s.Msg is KeyMsg { Vk: 0x23 }); // Fine non arriva all'altro PC
        Assert.Equal(InputMode.Controlling, c.Mode);

        // Lato target: eseguito solo se arriva dal PC che sta comandando.
        var (t, _, p, controller) = Setup();
        t.OnMessage(controller, new SecureAttention(), () => []);
        Assert.DoesNotContain("sas", p.Log);
        t.OnMessage(controller, new Enter(Edge.Left, 0.5f), () => []);
        t.OnMessage(Guid.NewGuid(), new SecureAttention(), () => []);
        Assert.DoesNotContain("sas", p.Log);
        t.OnMessage(controller, new SecureAttention(), () => []);
        Assert.Contains("sas", p.Log);
    }

    [Fact]
    public void Cursor_left_on_the_target_is_hidden_when_control_goes_elsewhere()
    {
        var (c, _, p, controller) = Setup();
        c.OnMessage(controller, new Enter(Edge.Left, 0.5f), () => []);
        c.OnMessage(controller, new MouseMove(10, 0), () => []);
        c.OnMessage(controller, new Leave(), () => []);
        Assert.Equal("idle hidden", p.Log[^1]); // dopo l'ultimo movimento: la finestra va dove il cursore è rimasto

        // Il PC che comandava si scollega: stessa cosa.
        p.Log.Clear();
        c.OnMessage(controller, new Enter(Edge.Left, 0.5f), () => []);
        c.OnPeerDisconnected(controller);
        Assert.Contains("idle hidden", p.Log);

        // Qualcuno usa questo PC mentre è comandato: il cursore serve, non si nasconde.
        p.Log.Clear();
        c.OnMessage(controller, new Enter(Edge.Left, 0.5f), () => []);
        c.OnLocalMouseMove(500, 500);
        Assert.Equal(InputMode.Local, c.Mode);
        Assert.DoesNotContain("idle hidden", p.Log);
    }

    [Fact]
    public void Deferred_park_measures_moves_from_where_the_cursor_really_is()
    {
        var (c, net, p, other) = Setup(moveIntervalMs: 0);
        p.DeferPark = true;
        p.Cursor = (1918, 540); // l'hook scarta l'evento sul bordo: il cursore resta dov'era
        Assert.True(c.OnLocalMouseMove(1919, 540));
        Assert.Equal(InputMode.Controlling, c.Mode);
        Assert.Contains("park pending", p.Log);
        Assert.DoesNotContain("set 960,540", p.Log); // non ancora al centro: prima deve sparire

        Assert.True(c.OnLocalMouseMove(1918, 545));
        Assert.Equal(new MouseMove(0, 5), net.Sent.Last(s => s.Msg is MouseMove).Msg);

        c.CompletePark();
        Assert.Equal("set 960,540", p.Log[^1]);
        Assert.True(c.OnLocalMouseMove(965, 540));
        Assert.Equal(new MouseMove(5, 0), net.Sent.Last(s => s.Msg is MouseMove).Msg);

        p.Log.Clear();
        c.CompletePark(); // già fatto: niente
        Assert.Empty(p.Log);
    }

    [Fact]
    public void Dragging_files_against_the_edge_carries_them_to_the_other_pc()
    {
        var (c, net, p, other) = Setup(moveIntervalMs: 0);
        var drag = new FakeDrag();
        c.FileDrag = drag;
        Assert.False(c.OnLocalButton(MouseButton.Left, true));
        Assert.False(c.OnLocalMouseMove(1919, 540)); // contro il bordo col pulsante premuto: si prova, non si passa ancora
        Assert.Equal(InputMode.Local, c.Mode);
        Assert.Equal(["probe"], drag.Log);
        Assert.False(c.OnLocalMouseMove(1919, 540)); // una prova sola
        Assert.Equal(["probe"], drag.Log);

        c.OnDragProbeResult(other, [1, 2, 3]);
        Assert.True(c.OnLocalMouseMove(1919, 541));
        Assert.Equal(InputMode.Controlling, c.Mode);
        Assert.Contains(net.Sent, s => s.To == other && s.Msg is Enter);
        Assert.Contains(net.Sent, s => s.To == other && s.Msg is DragFiles { Offer.Length: 3 });
        Assert.Contains("cancel drag", p.Log); // qui il trascinamento viene annullato
        Assert.Contains("end", drag.Log);

        // Il rilascio del pulsante ora va all'altro PC (lascia cadere i file lì).
        net.Sent.Clear();
        Assert.True(c.OnLocalButton(MouseButton.Left, false));
        Assert.Contains(net.Sent, s => s.Msg is MouseButtonMsg { Button: MouseButton.Left, Down: false });
    }

    [Fact]
    public void Dragging_something_that_is_not_files_stays_on_this_pc()
    {
        var (c, _, _, other) = Setup();
        var drag = new FakeDrag();
        c.FileDrag = drag;
        c.OnLocalButton(MouseButton.Left, true);
        c.OnLocalMouseMove(1919, 540);
        c.OnDragProbeResult(other, null);
        Assert.False(c.OnLocalMouseMove(1919, 540));
        Assert.Equal(InputMode.Local, c.Mode);
        Assert.Equal(["probe"], drag.Log); // niente nuova prova finché il pulsante resta premuto

        c.OnLocalButton(MouseButton.Left, false);
        c.OnLocalButton(MouseButton.Left, true);
        c.OnLocalMouseMove(1919, 540);
        Assert.Equal(["probe", "probe"], drag.Log); // nuovo clic, nuova prova

        drag.Can = false; // l'altro PC non sa ricevere file trascinati
        c.OnLocalButton(MouseButton.Left, false);
        c.OnLocalButton(MouseButton.Left, true);
        c.OnLocalMouseMove(1919, 540);
        Assert.Equal(2, drag.Log.Count(l => l == "probe"));
    }

    [Fact]
    public void Target_starts_the_drag_only_when_the_tray_is_ready_and_keeps_the_moves()
    {
        var (c, net, p, controller) = Setup();
        var drag = new FakeDrag();
        c.FileDrag = drag;
        c.OnMessage(controller, new Enter(Edge.Left, 0.5f), () => []);
        c.OnMessage(controller, new DragFiles([9, 9]), () => []);
        Assert.Contains("incoming 2", drag.Log);
        p.Log.Clear();
        c.OnMessage(controller, new MouseMove(5, 0), () => []);
        c.OnMessage(controller, new MouseMove(5, 0), () => []);
        Assert.Empty(p.Log); // la finestrella dell'icona non è ancora sotto il cursore: niente movimenti

        c.OnDragSourceReady();
        Assert.Equal("btn Left True", p.Log[0]); // prima il pulsante, sopra la finestrella
        Assert.Contains(p.Log, l => l.StartsWith("move "));
        c.OnMessage(controller, new MouseButtonMsg(MouseButton.Left, false), () => []);
        Assert.Equal("btn Left False", p.Log[^1]); // il rilascio lascia cadere i file
        Assert.DoesNotContain("cancel", drag.Log);
    }

    [Fact]
    public void Target_release_before_the_drag_starts_cancels_without_clicking()
    {
        var (c, _, p, controller) = Setup();
        var drag = new FakeDrag();
        c.FileDrag = drag;
        c.OnMessage(controller, new Enter(Edge.Left, 0.5f), () => []);
        c.OnMessage(controller, new DragFiles([1]), () => []);
        p.Log.Clear();
        c.OnMessage(controller, new MouseButtonMsg(MouseButton.Left, false), () => []);
        Assert.Contains("cancel", drag.Log);
        c.OnDragSourceReady(); // arriva tardi: ignorato
        Assert.DoesNotContain(p.Log, l => l.StartsWith("btn"));
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

public class MonitorStandbyPolicyTests
{
    [Fact]
    public void Standby_after_the_windows_timeout_while_controlling()
    {
        var p = new MonitorStandbyPolicy();
        p.OnModeChanged(InputMode.Controlling, 1_000);
        Assert.Equal(StandbyAction.None, p.Decide(1_000 + 299_000, 300, displayRequired: false, enabled: true));
        Assert.Equal(StandbyAction.Standby, p.Decide(1_000 + 300_000, 300, displayRequired: false, enabled: true));
        p.Applied(StandbyAction.Standby, anyAsleep: true);
        Assert.True(p.Asleep);
        Assert.Equal(StandbyAction.None, p.Decide(1_000 + 400_000, 300, false, true)); // già in standby
    }

    [Fact]
    public void Never_without_timeout_or_when_disabled_or_display_required()
    {
        var p = new MonitorStandbyPolicy();
        p.OnModeChanged(InputMode.Controlling, 0);
        Assert.Equal(StandbyAction.None, p.Decide(10_000_000, 0, false, true)); // "mai" in Windows
        Assert.Equal(StandbyAction.None, p.Decide(10_000_000, 300, false, enabled: false));
        Assert.Equal(StandbyAction.None, p.Decide(10_000_000, 300, displayRequired: true, enabled: true)); // video
        Assert.Equal(StandbyAction.Standby, p.Decide(10_000_000, 300, false, true)); // finito il video
    }

    [Fact]
    public void Not_while_local_or_controlled()
    {
        var p = new MonitorStandbyPolicy();
        Assert.Equal(StandbyAction.None, p.Decide(10_000_000, 300, false, true));
        p.OnModeChanged(InputMode.Controlled, 0); // questo PC è quello usato: ci pensa Windows
        Assert.Equal(StandbyAction.None, p.Decide(10_000_000, 300, false, true));
    }

    [Fact]
    public void Wakes_when_control_comes_back_even_if_it_leaves_again_at_once()
    {
        var p = new MonitorStandbyPolicy();
        p.OnModeChanged(InputMode.Controlling, 0);
        p.Applied(p.Decide(300_000, 300, false, true), anyAsleep: true);
        p.OnModeChanged(InputMode.Local, 400_000);
        p.OnModeChanged(InputMode.Controlling, 400_100); // ripassato all'altro PC prima del controllo
        Assert.Equal(StandbyAction.Wake, p.Decide(400_200, 300, false, true));
        p.Applied(StandbyAction.Wake);
        Assert.False(p.Asleep);
        // il conto riparte da capo
        Assert.Equal(StandbyAction.None, p.Decide(400_100 + 299_000, 300, false, true));
        Assert.Equal(StandbyAction.Standby, p.Decide(400_100 + 300_000, 300, false, true));
    }

    [Fact]
    public void Switching_between_controlled_pcs_keeps_counting()
    {
        var p = new MonitorStandbyPolicy();
        p.OnModeChanged(InputMode.Controlling, 0);
        p.OnModeChanged(InputMode.Controlling, 200_000); // passato a un terzo PC
        Assert.Equal(StandbyAction.Standby, p.Decide(300_000, 300, false, true));
    }

    [Fact]
    public void No_retry_when_no_monitor_supports_ddc()
    {
        var p = new MonitorStandbyPolicy();
        p.OnModeChanged(InputMode.Controlling, 0);
        p.Applied(p.Decide(300_000, 300, false, true), anyAsleep: false);
        Assert.False(p.Asleep);
        Assert.Equal(StandbyAction.None, p.Decide(400_000, 300, false, true));
        p.OnModeChanged(InputMode.Local, 500_000);
        Assert.Equal(StandbyAction.None, p.Decide(500_100, 300, false, true)); // niente da riaccendere
    }
}
