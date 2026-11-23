using Varco.Core.Input;

namespace Varco.Tests;

public class ControlRoleTests
{
    private static readonly Guid A = Guid.NewGuid(), C = Guid.NewGuid();

    [Fact]
    public void Master_until_someone_takes_control()
    {
        var r = new ControlRole();
        Assert.Null(r.Controller(0));
        r.OnModeChanged(InputMode.Local, null, 100);
        Assert.Null(r.Controller(0));
    }

    [Fact]
    public void Stays_slave_after_the_mouse_goes_back_until_touched_here()
    {
        var r = new ControlRole();
        r.OnModeChanged(InputMode.Controlled, A, 1000);
        Assert.Equal(A, r.Controller(500));
        r.OnModeChanged(InputMode.Local, null, 2000); // il mouse torna sul master
        Assert.Equal(A, r.Controller(500));
        Assert.Null(r.Controller(2500)); // qualcuno tocca questo PC
    }

    [Fact]
    public void Physical_input_while_controlled_does_not_count_until_control_ends()
    {
        var r = new ControlRole();
        r.OnModeChanged(InputMode.Controlled, A, 1000);
        Assert.Equal(A, r.Controller(1500));
    }

    [Fact]
    public void Controlling_another_pc_makes_it_master()
    {
        var r = new ControlRole();
        r.OnModeChanged(InputMode.Controlled, A, 1000);
        r.OnModeChanged(InputMode.Local, null, 2000);
        r.OnModeChanged(InputMode.Controlling, C, 3000);
        Assert.Null(r.Controller(0));
        r.OnModeChanged(InputMode.Local, null, 4000);
        Assert.Null(r.Controller(0));
    }

    [Fact]
    public void Follows_the_latest_controller()
    {
        var r = new ControlRole();
        r.OnModeChanged(InputMode.Controlled, A, 1000);
        r.OnModeChanged(InputMode.Controlled, C, 2000);
        r.OnModeChanged(InputMode.Local, null, 3000);
        Assert.Equal(C, r.Controller(1500));
    }
}
