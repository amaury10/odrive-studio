using FluentAssertions;
using ODrive.Model.Simulation;
using ODrive.Protocol;
using ODrive.Protocol.Simulation;
using Xunit;

namespace ODrive.Model.Tests;

public class SimulatedMotorTests
{
    private TimeSpan _now = TimeSpan.Zero;

    private async Task<(SimulatedODrive Board, ODrive3 Odrv, ODriveDevice Device)> CreateAsync(
        SimulatedMotorOptions? options = null)
    {
        SimulatedODrive board = SimulatedODrive.CreateReference();
        SimulatedMotorModel.Attach(board, options, () => _now);
        ODriveDevice device = await ODriveDevice.ConnectAsync(new SimulatedTransport(board));
        return (board, new ODrive3(device), device);
    }

    /// <summary>Avance l'horloge, puis fait une lecture pour que le modèle calcule.</summary>
    private async Task<AxisState> AdvanceAsync(ODrive3 odrv, double seconds)
    {
        // Par pas de 100 ms : le modèle borne chaque pas à 1 s.
        for (double done = 0; done < seconds; done += 0.1)
        {
            _now += TimeSpan.FromSeconds(0.1);
            await odrv.Axis0.CurrentState.GetAsync();
        }
        return await odrv.Axis0.CurrentState.GetAsync();
    }

    private static async Task ConfigureHoverboardAsync(ODrive3 odrv)
    {
        await odrv.Axis0.Motor.Config.PolePairs.SetAsync(15);
        await odrv.Axis0.Motor.Config.ResistanceCalibMaxVoltage.SetAsync(4f);
        await odrv.Axis0.Encoder.Config.Mode.SetAsync(EncoderMode.Hall);
        await odrv.Axis0.Encoder.Config.Cpr.SetAsync(90);
        await odrv.Axis0.Controller.Config.ControlMode.SetAsync(ControlMode.VelocityControl);
        await odrv.Axis0.Controller.Config.VelLimit.SetAsync(10f);
    }

    private async Task CalibrateAsync(ODrive3 odrv)
    {
        await ConfigureHoverboardAsync(odrv);
        await odrv.Axis0.RequestedState.SetAsync(AxisState.FullCalibrationSequence);
        (await AdvanceAsync(odrv, 20)).Should().Be(AxisState.Idle);
        (await odrv.Axis0.Error.GetAsync()).Should().Be(AxisError.None);
    }

    [Fact]
    public async Task Closed_loop_is_refused_before_calibration()
    {
        var (_, odrv, _) = await CreateAsync();

        await odrv.Axis0.RequestedState.SetAsync(AxisState.ClosedLoopControl);

        (await odrv.Axis0.CurrentState.GetAsync()).Should().Be(AxisState.Idle);
        (await odrv.Axis0.Error.GetAsync()).Should().Be(AxisError.InvalidState);
    }

    [Fact]
    public async Task Motor_calibration_takes_time_and_measures_resistance_and_inductance()
    {
        var (_, odrv, _) = await CreateAsync();
        await ConfigureHoverboardAsync(odrv);

        await odrv.Axis0.RequestedState.SetAsync(AxisState.MotorCalibration);

        (await AdvanceAsync(odrv, 1)).Should().Be(AxisState.MotorCalibration);
        (await odrv.Axis0.Motor.IsArmed.GetAsync()).Should().BeTrue();
        (await AdvanceAsync(odrv, 5)).Should().Be(AxisState.Idle);
        (await odrv.Axis0.Motor.IsCalibrated.GetAsync()).Should().BeTrue();
        (await odrv.Axis0.Motor.Config.PhaseResistance.GetAsync()).Should().BeApproximately(0.18f, 1e-4f);
        (await odrv.Axis0.Motor.Config.PhaseInductance.GetAsync()).Should().BeApproximately(0.00034f, 1e-6f);
    }

    [Fact]
    public async Task Motor_calibration_fails_when_the_calibration_voltage_is_too_low()
    {
        // 10 A × 0,5 Ω = 5 V, au-dessus des 2 V autorisés par défaut.
        var (_, odrv, _) = await CreateAsync(new SimulatedMotorOptions(PhaseResistance: 0.5f));

        await odrv.Axis0.RequestedState.SetAsync(AxisState.MotorCalibration);
        await AdvanceAsync(odrv, 6);

        (await odrv.Axis0.Motor.Error.GetAsync()).Should().Be(MotorError.PhaseResistanceOutOfRange);
        (await odrv.Axis0.Error.GetAsync()).Should().Be(AxisError.MotorFailed);
        (await odrv.Axis0.Motor.IsCalibrated.GetAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Offset_calibration_is_refused_before_motor_calibration()
    {
        var (_, odrv, _) = await CreateAsync();

        await odrv.Axis0.RequestedState.SetAsync(AxisState.EncoderOffsetCalibration);

        (await odrv.Axis0.Error.GetAsync()).Should().Be(AxisError.InvalidState);
    }

    [Fact]
    public async Task Offset_calibration_detects_a_wrong_pole_pair_count()
    {
        var (_, odrv, _) = await CreateAsync();
        await ConfigureHoverboardAsync(odrv);
        await odrv.Axis0.Motor.Config.PolePairs.SetAsync(7);
        await odrv.Axis0.Encoder.Config.Cpr.SetAsync(42);

        await odrv.Axis0.RequestedState.SetAsync(AxisState.FullCalibrationSequence);
        await AdvanceAsync(odrv, 20);

        (await odrv.Axis0.Encoder.Error.GetAsync()).Should().Be(EncoderError.CprPolepairsMismatch);
        (await odrv.Axis0.Error.GetAsync()).Should().Be(AxisError.EncoderFailed);
        (await odrv.Axis0.Encoder.IsReady.GetAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Hall_offset_calibration_needs_the_polarity_calibration_first()
    {
        var (_, odrv, _) = await CreateAsync();
        await ConfigureHoverboardAsync(odrv);
        await odrv.Axis0.RequestedState.SetAsync(AxisState.MotorCalibration);
        await AdvanceAsync(odrv, 6);

        await odrv.Axis0.RequestedState.SetAsync(AxisState.EncoderOffsetCalibration);

        (await odrv.Axis0.Encoder.Error.GetAsync()).Should().Be(EncoderError.HallNotCalibratedYet);
    }

    [Fact]
    public async Task Full_calibration_of_a_hoverboard_motor_ends_ready_with_a_centred_phase_offset()
    {
        var (_, odrv, _) = await CreateAsync();

        await CalibrateAsync(odrv);

        (await odrv.Axis0.Motor.IsCalibrated.GetAsync()).Should().BeTrue();
        (await odrv.Axis0.Encoder.IsReady.GetAsync()).Should().BeTrue();
        (await odrv.Axis0.Encoder.Config.HallPolarityCalibrated.GetAsync()).Should().BeTrue();
        (await odrv.Axis0.Encoder.Config.PhaseOffsetFloat.GetAsync()).Should().BeApproximately(0.5f, 1e-4f);
    }

    [Fact]
    public async Task Closed_loop_velocity_follows_the_setpoint_and_the_position_advances()
    {
        var (_, odrv, _) = await CreateAsync();
        await CalibrateAsync(odrv);
        await odrv.Axis0.RequestedState.SetAsync(AxisState.ClosedLoopControl);
        (await odrv.Axis0.CurrentState.GetAsync()).Should().Be(AxisState.ClosedLoopControl);
        float start = await odrv.Axis0.Encoder.PosEstimate.GetAsync();

        await odrv.Axis0.Controller.InputVel.SetAsync(2f);
        await AdvanceAsync(odrv, 2);

        (await odrv.Axis0.Encoder.VelEstimate.GetAsync()).Should().BeApproximately(2f, 0.05f);
        (await odrv.Axis0.Encoder.PosEstimate.GetAsync()).Should().BeGreaterThan(start + 3f);
    }

    [Fact]
    public async Task Velocity_is_clamped_to_the_velocity_limit()
    {
        var (_, odrv, _) = await CreateAsync();
        await CalibrateAsync(odrv);
        await odrv.Axis0.Controller.Config.VelLimit.SetAsync(1f);
        await odrv.Axis0.RequestedState.SetAsync(AxisState.ClosedLoopControl);

        await odrv.Axis0.Controller.InputVel.SetAsync(8f);
        await AdvanceAsync(odrv, 2);

        (await odrv.Axis0.Encoder.VelEstimate.GetAsync()).Should().BeApproximately(1f, 0.05f);
    }

    [Fact]
    public async Task Velocity_ramp_limits_the_acceleration()
    {
        var (_, odrv, _) = await CreateAsync();
        await CalibrateAsync(odrv);
        await odrv.Axis0.Controller.Config.InputMode.SetAsync(InputMode.VelRamp);
        await odrv.Axis0.Controller.Config.VelRampRate.SetAsync(1f); // 1 tour/s²
        await odrv.Axis0.RequestedState.SetAsync(AxisState.ClosedLoopControl);

        await odrv.Axis0.Controller.InputVel.SetAsync(5f);
        await AdvanceAsync(odrv, 2);

        (await odrv.Axis0.Controller.VelSetpoint.GetAsync()).Should().BeApproximately(2f, 0.2f);
    }

    [Fact]
    public async Task Idle_releases_the_motor_which_coasts_to_a_stop()
    {
        var (_, odrv, _) = await CreateAsync();
        await CalibrateAsync(odrv);
        await odrv.Axis0.RequestedState.SetAsync(AxisState.ClosedLoopControl);
        await odrv.Axis0.Controller.InputVel.SetAsync(2f);
        await AdvanceAsync(odrv, 2);

        await odrv.Axis0.RequestedState.SetAsync(AxisState.Idle);

        (await odrv.Axis0.Motor.IsArmed.GetAsync()).Should().BeFalse();
        await AdvanceAsync(odrv, 3);
        (await odrv.Axis0.Encoder.VelEstimate.GetAsync()).Should().BeApproximately(0f, 0.01f);
    }

    [Fact]
    public async Task Watchdog_stops_the_axis_when_it_is_not_fed()
    {
        var (_, odrv, _) = await CreateAsync();
        await CalibrateAsync(odrv);
        await odrv.Axis0.Config.WatchdogTimeout.SetAsync(0.5f);
        await odrv.Axis0.Config.EnableWatchdog.SetAsync(true);
        await odrv.Axis0.RequestedState.SetAsync(AxisState.ClosedLoopControl);

        // Nourri : l'axe tient. (Les lectures USB ne nourrissent pas le watchdog, docs/SAFETY.md.)
        for (int i = 0; i < 10; i++)
        {
            await odrv.Axis0.WatchdogFeedAsync();
            (await AdvanceAsync(odrv, 0.2)).Should().Be(AxisState.ClosedLoopControl);
        }

        (await AdvanceAsync(odrv, 1)).Should().Be(AxisState.Idle);
        (await odrv.Axis0.Error.GetAsync()).Should().Be(AxisError.WatchdogTimerExpired);
    }

    [Fact]
    public async Task Pre_calibrated_flags_survive_a_save_and_reboot()
    {
        var (board, odrv, device) = await CreateAsync();
        await CalibrateAsync(odrv);
        await odrv.Axis0.Motor.Config.PreCalibrated.SetAsync(true);
        await odrv.Axis0.Encoder.Config.PreCalibrated.SetAsync(true);

        (await device.SaveConfigurationAsync()).Should().BeTrue();
        await device.DisposeAsync();
        ODriveDevice again = await ODriveDevice.ConnectAsync(new SimulatedTransport(board));
        var rebooted = new ODrive3(again);

        (await rebooted.Axis0.Motor.IsCalibrated.GetAsync()).Should().BeTrue();
        await rebooted.Axis0.RequestedState.SetAsync(AxisState.ClosedLoopControl);
        (await rebooted.Axis0.CurrentState.GetAsync()).Should().Be(AxisState.ClosedLoopControl);
    }

    [Fact]
    public async Task Without_pre_calibrated_a_reboot_forgets_the_calibration()
    {
        var (board, odrv, device) = await CreateAsync();
        await CalibrateAsync(odrv);

        await device.RebootAsync();
        await device.DisposeAsync();
        var rebooted = new ODrive3(await ODriveDevice.ConnectAsync(new SimulatedTransport(board)));

        (await rebooted.Axis0.Motor.IsCalibrated.GetAsync()).Should().BeFalse();
        (await rebooted.Axis0.Encoder.IsReady.GetAsync()).Should().BeFalse();
    }
}
