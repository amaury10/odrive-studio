using FluentAssertions;
using ODrive.Protocol;
using ODrive.Protocol.Simulation;
using Xunit;

namespace ODrive.Model.Tests;

public class SafetyGuardTests
{
    private readonly SimulatedODrive _board = SimulatedODrive.CreateReference();
    private readonly SafetyGuard _guard = new();

    private Task<ODriveDevice> ConnectAsync() => ODriveDevice.ConnectAsync(new SimulatedTransport(_board));

    [Fact]
    public async Task Motion_is_authorized_on_a_healthy_board_after_confirmation()
    {
        ODriveDevice device = await ConnectAsync();
        var questions = new List<string>();

        SafetyDecision decision = await _guard.AuthorizeMotionAsync(device, q => { questions.Add(q); return true; });

        decision.IsAllowed.Should().BeTrue();
        questions.Should().ContainSingle().Which.Should().Contain("libre");
    }

    [Fact]
    public async Task Confirmation_is_asked_only_once_per_session()
    {
        ODriveDevice device = await ConnectAsync();
        int asked = 0;

        await _guard.AuthorizeMotionAsync(device, _ => { asked++; return true; });
        SafetyDecision second = await _guard.AuthorizeMotionAsync(device, _ => { asked++; return true; });

        asked.Should().Be(1);
        second.IsAllowed.Should().BeTrue();
    }

    [Fact]
    public async Task Refused_confirmation_blocks_the_motion_and_is_asked_again_next_time()
    {
        ODriveDevice device = await ConnectAsync();
        int asked = 0;

        SafetyDecision refused = await _guard.AuthorizeMotionAsync(device, _ => { asked++; return false; });
        await _guard.AuthorizeMotionAsync(device, _ => { asked++; return true; });

        refused.IsAllowed.Should().BeFalse();
        asked.Should().Be(2);
    }

    [Fact]
    public async Task Motion_is_refused_without_asking_while_the_board_has_errors()
    {
        ODriveDevice device = await ConnectAsync();
        _board.Set("axis1.encoder.error", (ushort)0x10);
        bool asked = false;

        SafetyDecision decision = await _guard.AuthorizeMotionAsync(device, _ => { asked = true; return true; });

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Contain("ENCODER_ERROR_ILLEGAL_HALL_STATE");
        asked.Should().BeFalse();
    }

    [Theory]
    [InlineData(6.0f)]   // sous dc_bus_undervoltage_trip_level (8 V)
    [InlineData(61.0f)]  // au-dessus de dc_bus_overvoltage_trip_level (59,92 V)
    public async Task Motion_is_refused_when_vbus_is_out_of_range(float vbus)
    {
        ODriveDevice device = await ConnectAsync();
        _board.Set("vbus_voltage", vbus);

        SafetyDecision decision = await _guard.AuthorizeMotionAsync(device, _ => true);

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Contain("Vbus");
    }

    [Fact]
    public async Task Save_erase_and_reboot_are_refused_while_an_axis_is_not_idle()
    {
        ODriveDevice device = await ConnectAsync();
        (await SafetyGuard.AuthorizeConfigurationChangeAsync(device)).IsAllowed.Should().BeTrue();

        await new ODrive3(device).Axis1.RequestedState.SetAsync(AxisState.ClosedLoopControl);
        SafetyDecision decision = await SafetyGuard.AuthorizeConfigurationChangeAsync(device);

        decision.IsAllowed.Should().BeFalse();
        decision.Reason.Should().Contain("axis1").And.Contain("CLOSED_LOOP_CONTROL");
    }

    [Theory]
    [InlineData("axis0.requested_state", (byte)8, true)]
    [InlineData("axis0.requested_state", (byte)3, true)]   // séquence de calibration : le moteur bouge
    [InlineData("axis0.requested_state", (byte)1, false)]  // IDLE
    [InlineData("axis0.controller.config.vel_limit", 5f, false)]
    public void Only_a_request_for_an_active_state_needs_the_motion_authorization(string path, object value, bool expected)
    {
        SafetyGuard.IsMotionRequest(path, value).Should().Be(expected);
    }
}
