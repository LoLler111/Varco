using Varco.Core.Input;
using Varco.Core.Protocol;

namespace Varco.Tests;

public class ControlRoleTests
{
    private static readonly Guid Self = Guid.NewGuid(), A = Guid.NewGuid(), C = Guid.NewGuid();
    private static bool All(Guid _) => true;

    [Fact]
    public void Master_when_alone_or_peers_never_said_anything()
    {
        var r = new ControlRole(Self);
        Assert.Null(r.Master(0, All));
        Assert.Null(r.Master(5000, All));
    }

    [Fact]
    public void Slave_as_soon_as_connected_if_the_other_pc_was_touched_more_recently()
    {
        var r = new ControlRole(Self);
        r.OnPeerActivity(A, idleMs: 200, nowMs: 10_000); // A toccato a 9800
        Assert.Equal(A, r.Master(5000, All));            // questo PC toccato a 5000
        Assert.Equal(A, r.Master(0, All));               // questo PC mai toccato
    }

    [Fact]
    public void Touching_this_pc_makes_it_master_again()
    {
        var r = new ControlRole(Self);
        r.OnPeerActivity(A, 200, 10_000);
        Assert.Null(r.Master(10_500, All));
    }

    [Fact]
    public void The_most_recently_touched_of_many_wins()
    {
        var r = new ControlRole(Self);
        r.OnPeerActivity(A, 5000, 10_000); // 5000
        r.OnPeerActivity(C, 1000, 10_000); // 9000
        Assert.Equal(C, r.Master(7000, All));
        Assert.Equal(A, r.Master(4000, id => id != C)); // C scollegato non conta
        Assert.Null(r.Master(7000, id => id != C));
        r.Forget(C);
        Assert.Equal(A, r.Master(4000, All));
    }

    [Fact]
    public void Never_touched_anywhere_every_pc_picks_the_same_master()
    {
        var x = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var y = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var onX = new ControlRole(x);
        var onY = new ControlRole(y);
        onX.OnPeerActivity(y, uint.MaxValue, 1000);
        onY.OnPeerActivity(x, uint.MaxValue, 1000);
        Assert.Null(onX.Master(0, All));
        Assert.Equal(x, onY.Master(0, All));
    }

    [Fact]
    public void Idle_is_sent_as_ms_since_last_input()
    {
        Assert.Equal(uint.MaxValue, ControlRole.IdleMs(0, 1000));
        Assert.Equal(300u, ControlRole.IdleMs(700, 1000));
        Assert.Equal(0u, ControlRole.IdleMs(1200, 1000));
    }

    [Fact]
    public void Input_activity_roundtrips()
    {
        var m = Assert.IsType<InputActivity>(MessageCodec.Decode(MessageCodec.Encode(new InputActivity(1234))));
        Assert.Equal(1234u, m.IdleMs);
    }
}
