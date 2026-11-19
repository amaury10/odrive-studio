using FluentAssertions;
using ODrive.Model;
using ODrive.Model.Simulation;
using ODrive.Protocol;
using ODrive.Protocol.Simulation;
using ODrive.Tuning.Commissioning;
using ODrive.Tuning.Jog;
using ODrive.Tuning.Profiles;
using Xunit;

namespace ODrive.Tuning.Tests;

/// <summary>Carte simulée avec moteur d'hoverboard, sur une horloge que les tests font avancer.</summary>
internal sealed class Bench
{
    private readonly long[] _ticks;

    private Bench(SimulatedODrive board, ODriveDevice device, long[] ticks)
    {
        _ticks = ticks;
        Board = board;
        Device = device;
        Odrv = new ODrive3(device);
    }

    public SimulatedODrive Board { get; }
    public ODriveDevice Device { get; }
    public ODrive3 Odrv { get; }
    public SafetyGuard Guard { get; } = new();
    public List<string> Questions { get; } = [];
    public bool Answer { get; set; } = true;

    public static async Task<Bench> CreateAsync(SimulatedMotorOptions? options = null)
    {
        SimulatedODrive board = SimulatedODrive.CreateReference();
        long[] ticks = [0];
        SimulatedMotorModel.Attach(board, options, () => new TimeSpan(Interlocked.Read(ref ticks[0])));
        return new Bench(board, await ODriveDevice.ConnectAsync(new SimulatedTransport(board)), ticks);
    }

    public bool Confirm(string question)
    {
        Questions.Add(question);
        return Answer;
    }

    /// <summary>Attente simulée : avance l'horloge de la carte et rend la main.</summary>
    public async Task DelayAsync(TimeSpan duration, CancellationToken ct)
    {
        Interlocked.Add(ref _ticks[0], duration.Ticks);
        await Task.Yield();
        ct.ThrowIfCancellationRequested();
    }

    public Task<AxisRunResult> RunAsync(AxisState state, double timeoutSeconds = 30) =>
        AxisStateRunner.RunAsync(Device, 0, state, TimeSpan.FromSeconds(timeoutSeconds), Guard, Confirm, null, DelayAsync);

    public async Task CalibrateAsync()
    {
        (await HoverboardPreset.ApplyAsync(Device, HoverboardPreset.Build(new HoverboardOptions()))).Should().BeEmpty();
        (await RunAsync(AxisState.FullCalibrationSequence)).Success.Should().BeTrue();
    }

    public static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (int i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5);
        }
        condition().Should().BeTrue("la condition attendue n'est pas arrivée en 2 s");
    }
}

public class HoverboardPresetTests
{
    [Fact]
    public void Uses_the_formulas_of_the_hoverboard_guide()
    {
        // docs/HOVERBOARD.md : Kv 16 → 0,517 Nm/A ; cpr 90 ; gains ≈ 0,93 et ≈ 4,65.
        HoverboardPreset.TorqueConstant(16).Should().BeApproximately(0.517f, 0.001f);
        HoverboardPreset.HallCpr(15).Should().Be(90);
        HoverboardPreset.VelGain(16, 15).Should().BeApproximately(0.93f, 0.01f);
        HoverboardPreset.VelIntegratorGain(16, 15).Should().BeApproximately(4.65f, 0.01f);
    }

    [Fact]
    public void Every_setting_exists_on_the_board_with_a_compatible_type()
    {
        EndpointSchema schema = SimulatedODrive.CreateReference().Schema;

        foreach (int axis in new[] { 0, 1 })
        {
            foreach (CommissioningSetting setting in HoverboardPreset.Build(new HoverboardOptions { Axis = axis }))
            {
                PropertyNode? property = schema.FindProperty(setting.Path);
                property.Should().NotBeNull($"{setting.Path} doit exister en 0.5.6");
                property!.IsWritable.Should().BeTrue(setting.Path);
                Action convert = () => HoverboardPreset.ToEndpointValue(property.Type, setting.Value);
                convert.Should().NotThrow(setting.Path);
            }
        }
    }

    [Fact]
    public void Hall_inputs_follow_the_axis()
    {
        string[] Gpio(int axis) => [.. HoverboardPreset.Build(new HoverboardOptions { Axis = axis })
            .Where(s => s.Path.Contains("gpio", StringComparison.Ordinal)).Select(s => s.Path)];

        Gpio(0).Should().Equal("config.gpio9_mode", "config.gpio10_mode", "config.gpio11_mode");
        Gpio(1).Should().Equal("config.gpio12_mode", "config.gpio13_mode", "config.gpio14_mode");
    }

    [Fact]
    public void Settings_that_need_a_reboot_are_flagged()
    {
        IReadOnlyList<CommissioningSetting> settings = HoverboardPreset.Build(new HoverboardOptions());

        settings.Where(s => s.NeedsReboot).Select(s => s.Path).Should().Contain(
        [
            "config.enable_brake_resistor", "config.gpio9_mode",
        ]);
    }

    // Relevé sur une v3.6-56V non officielle : en plage 25 A l'offset des capteurs de courant frôle le seuil du
    // firmware (UNKNOWN_CURRENT_MEASUREMENT), et à 10 A une roue de 0,46 Ω dépasse les 4 V de calibration
    // (PHASE_RESISTANCE_OUT_OF_RANGE).
    [Fact]
    public void Preset_keeps_the_default_current_range_and_calibrates_at_5_amps()
    {
        IReadOnlyList<CommissioningSetting> settings = HoverboardPreset.Build(new HoverboardOptions());

        settings.Should().NotContain(s => s.Path.EndsWith("requested_current_range", StringComparison.Ordinal));
        settings.Single(s => s.Path == "axis0.motor.config.calibration_current").Value.Should().Be(5f);
    }

    [Fact]
    public void Gains_follow_kv_and_pole_pairs()
    {
        CommissioningSetting gain = HoverboardPreset.Build(new HoverboardOptions { Kv = 8, PolePairs = 10 })
            .Single(s => s.Path == "axis0.controller.config.vel_gain");

        ((float)gain.Value).Should().BeApproximately(0.02f * (8.27f / 8) * 60, 1e-4f);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    public void Rejects_an_axis_that_does_not_exist(int axis)
    {
        Action act = () => HoverboardPreset.Build(new HoverboardOptions { Axis = axis });

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Apply_writes_typed_values_to_the_board()
    {
        Bench bench = await Bench.CreateAsync();

        var failures = await HoverboardPreset.ApplyAsync(bench.Device, HoverboardPreset.Build(new HoverboardOptions()));

        failures.Should().BeEmpty();
        (await bench.Odrv.Axis0.Encoder.Config.Mode.GetAsync()).Should().Be(EncoderMode.Hall);
        (await bench.Odrv.Axis0.Encoder.Config.Cpr.GetAsync()).Should().Be(90);
        (await bench.Odrv.Axis0.Motor.Config.PolePairs.GetAsync()).Should().Be(15);
        (await bench.Odrv.Axis0.Controller.Config.ControlMode.GetAsync()).Should().Be(ControlMode.VelocityControl);
        (await bench.Odrv.Axis0.Motor.Config.TorqueConstant.GetAsync()).Should().BeApproximately(0.517f, 0.001f);
    }

    [Fact]
    public async Task Apply_reports_a_property_missing_from_the_board_and_continues()
    {
        Bench bench = await Bench.CreateAsync();
        CommissioningSetting[] settings =
        [
            new(CommissioningStep.Motor, "axis0.motor.config.absente", 1f, "absente"),
            new(CommissioningStep.Motor, "axis0.motor.config.pole_pairs", 15, "paires de pôles"),
        ];

        var failures = await HoverboardPreset.ApplyAsync(bench.Device, settings);

        failures.Should().ContainSingle().Which.Path.Should().Be("axis0.motor.config.absente");
        (await bench.Odrv.Axis0.Motor.Config.PolePairs.GetAsync()).Should().Be(15);
    }
}

public class AxisStateRunnerTests
{
    [Fact]
    public async Task Full_calibration_with_the_hoverboard_preset_succeeds()
    {
        Bench bench = await Bench.CreateAsync();

        await bench.CalibrateAsync();

        (await bench.Odrv.Axis0.Motor.IsCalibrated.GetAsync()).Should().BeTrue();
        (await bench.Odrv.Axis0.Encoder.IsReady.GetAsync()).Should().BeTrue();
        bench.Questions.Should().ContainSingle("la confirmation n'est demandée qu'une fois par session");
    }

    [Fact]
    public async Task Reports_progress_through_the_calibration_states()
    {
        Bench bench = await Bench.CreateAsync();
        await HoverboardPreset.ApplyAsync(bench.Device, HoverboardPreset.Build(new HoverboardOptions()));
        var seen = new List<AxisState>();

        await AxisStateRunner.RunAsync(bench.Device, 0, AxisState.FullCalibrationSequence, TimeSpan.FromSeconds(30),
            bench.Guard, bench.Confirm, new SyncProgress(seen.Add), bench.DelayAsync);

        seen.Distinct().Should().ContainInOrder(AxisState.MotorCalibration, AxisState.EncoderHallPolarityCalibration,
            AxisState.EncoderOffsetCalibration, AxisState.Idle);
    }

    [Fact]
    public async Task A_failed_calibration_reports_the_decoded_errors()
    {
        // Configuration d'usine : mode incrémental, 7 paires de pôles, sur un moteur qui en a 15.
        Bench bench = await Bench.CreateAsync();

        AxisRunResult result = await bench.RunAsync(AxisState.FullCalibrationSequence);

        result.Success.Should().BeFalse();
        result.Errors.Select(e => e.Name).Should().Contain(name => name.Contains("CPR_POLEPAIRS_MISMATCH"));
        result.Message.Should().Contain("CPR_POLEPAIRS_MISMATCH");
    }

    [Fact]
    public async Task Nothing_is_started_when_the_user_declines()
    {
        Bench bench = await Bench.CreateAsync();
        bench.Answer = false;

        AxisRunResult result = await bench.RunAsync(AxisState.MotorCalibration);

        result.Success.Should().BeFalse();
        (await bench.Odrv.Axis0.CurrentState.GetAsync()).Should().Be(AxisState.Idle);
        (await bench.Odrv.Axis0.Motor.IsCalibrated.GetAsync()).Should().BeFalse();
    }

    [Fact]
    public async Task Nothing_is_started_while_the_board_has_errors()
    {
        Bench bench = await Bench.CreateAsync();
        bench.Board.Set("axis0.error", (uint)AxisError.MotorFailed);

        AxisRunResult result = await bench.RunAsync(AxisState.MotorCalibration);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("MOTOR_FAILED");
        bench.Questions.Should().BeEmpty();
    }

    [Fact]
    public async Task A_state_that_never_ends_is_stopped_at_the_timeout()
    {
        Bench bench = await Bench.CreateAsync();
        await bench.CalibrateAsync();

        AxisRunResult result = await bench.RunAsync(AxisState.ClosedLoopControl, timeoutSeconds: 1);

        result.Success.Should().BeFalse();
        result.Message.Should().Contain("IDLE");
        (await bench.Odrv.Axis0.CurrentState.GetAsync()).Should().Be(AxisState.Idle);
    }

    [Fact]
    public async Task Cancelling_puts_the_axis_back_to_idle()
    {
        Bench bench = await Bench.CreateAsync();
        await HoverboardPreset.ApplyAsync(bench.Device, HoverboardPreset.Build(new HoverboardOptions()));
        using var cancellation = new CancellationTokenSource();
        int polls = 0;
        async Task Delay(TimeSpan duration, CancellationToken ct)
        {
            if (++polls == 5)
            {
                await cancellation.CancelAsync();
            }
            await bench.DelayAsync(duration, ct);
        }

        Func<Task> act = () => AxisStateRunner.RunAsync(bench.Device, 0, AxisState.MotorCalibration,
            TimeSpan.FromSeconds(30), bench.Guard, bench.Confirm, null, Delay, cancellation.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        (await bench.Odrv.Axis0.CurrentState.GetAsync()).Should().Be(AxisState.Idle);
    }

    private sealed class SyncProgress(Action<AxisState> report) : IProgress<AxisState>
    {
        public void Report(AxisState value) => report(value);
    }
}

public class CalibrationChecksTests
{
    [Fact]
    public void The_values_of_the_guide_are_plausible()
    {
        CalibrationChecks.HoverboardMotor(0.18f, 0.00034f).Should().BeNull();
    }

    [Theory]
    [InlineData(0.01f, 0.00034f, "Résistance")]
    [InlineData(2f, 0.00034f, "Résistance")]
    [InlineData(0.18f, 0.00001f, "Inductance")]
    [InlineData(0.18f, 0.01f, "Inductance")]
    public void Out_of_range_measurements_are_reported(float resistance, float inductance, string expected)
    {
        CalibrationChecks.HoverboardMotor(resistance, inductance).Should().Contain(expected);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(-0.5f)]
    [InlineData(1.5f)]
    [InlineData(-1.45f)]
    public void Hall_offset_must_be_one_half_modulo_one(float offset)
    {
        CalibrationChecks.HallPhaseOffset(offset).Should().BeNull();
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.2f)]
    [InlineData(1f)]
    public void Other_hall_offsets_are_reported(float offset)
    {
        CalibrationChecks.HallPhaseOffset(offset).Should().Contain("22 nF");
    }
}

public class ConfigProfileTests
{
    [Fact]
    public async Task Capture_holds_every_writable_configuration_property_and_the_versions()
    {
        Bench bench = await Bench.CreateAsync();

        ConfigProfile profile = await ConfigProfile.CaptureAsync(bench.Device, "banc");

        profile.Firmware.Should().Be("0.5.6");
        profile.Hardware.Should().Be("v3.6-56V");
        profile.JsonCrc.Should().Be(bench.Device.Schema.JsonCrc);
        profile.Note.Should().Be("banc");
        profile.Values.Should().ContainKey("axis0.controller.config.vel_limit").WhoseValue.Should().Be("2");
        profile.Values.Should().ContainKey("axis1.motor.config.pole_pairs");
        profile.Values.Should().ContainKey("config.brake_resistance");
        profile.Values.Keys.Should().NotContain("vbus_voltage").And.NotContain("axis0.requested_state");
        profile.Values.Keys.Should().OnlyContain(path => path.Contains("config.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_saved_profile_reloads_identically_including_infinity()
    {
        Bench bench = await Bench.CreateAsync();
        ConfigProfile profile = await ConfigProfile.CaptureAsync(bench.Device, "note é");
        using var stream = new MemoryStream();

        profile.Save(stream);
        stream.Position = 0;
        ConfigProfile reloaded = ConfigProfile.Load(stream);

        reloaded.Values.Should().Equal(profile.Values);
        reloaded.Firmware.Should().Be(profile.Firmware);
        reloaded.JsonCrc.Should().Be(profile.JsonCrc);
        reloaded.Note.Should().Be("note é");
        // odrive_main.h:110 : pas de limite de courant positif par défaut.
        reloaded.Values["config.dc_max_positive_current"].Should().Be("Infinity");
        (await reloaded.CompareToDeviceAsync(bench.Device)).Should().BeEmpty();
    }

    [Fact]
    public void Loading_something_else_than_a_profile_fails_clearly()
    {
        Action notJson = () => ConfigProfile.Load(new MemoryStream("pas du json"u8.ToArray()));
        Action otherJson = () => ConfigProfile.Load(new MemoryStream("""{"values":{}}"""u8.ToArray()));

        notJson.Should().Throw<InvalidDataException>();
        otherJson.Should().Throw<InvalidDataException>().WithMessage("*profil ODrive Studio*");
    }

    [Fact]
    public async Task Compare_lists_only_what_differs_from_the_board()
    {
        Bench bench = await Bench.CreateAsync();
        ConfigProfile profile = await ConfigProfile.CaptureAsync(bench.Device);
        await bench.Odrv.Axis0.Controller.Config.VelLimit.SetAsync(7.5f);
        await bench.Odrv.Axis1.Motor.Config.PolePairs.SetAsync(15);

        IReadOnlyList<ProfileDifference> differences = await profile.CompareToDeviceAsync(bench.Device);

        differences.Should().BeEquivalentTo(
        [
            new ProfileDifference("axis0.controller.config.vel_limit", "2", "7.5"),
            new ProfileDifference("axis1.motor.config.pole_pairs", "7", "15"),
        ]);
    }

    [Fact]
    public async Task Compare_ignores_formatting_of_hand_edited_values()
    {
        Bench bench = await Bench.CreateAsync();
        ConfigProfile captured = await ConfigProfile.CaptureAsync(bench.Device);
        var values = new Dictionary<string, string>(captured.Values)
        {
            ["axis0.controller.config.vel_limit"] = "2.0",
            ["axis0.config.enable_watchdog"] = "False",
        };
        var edited = new ConfigProfile(captured.Firmware, captured.Hardware, captured.JsonCrc, "", captured.Created, values);

        (await edited.CompareToDeviceAsync(bench.Device)).Should().BeEmpty();
    }

    [Fact]
    public async Task Compare_reports_properties_missing_on_either_side()
    {
        Bench bench = await Bench.CreateAsync();
        ConfigProfile captured = await ConfigProfile.CaptureAsync(bench.Device);
        var values = new Dictionary<string, string>(captured.Values) { ["axis0.config.disparue"] = "1" };
        values.Remove("config.brake_resistance");
        var other = new ConfigProfile("0.5.1", "v3.6-56V", 1, "", captured.Created, values);

        IReadOnlyList<ProfileDifference> differences = await other.CompareToDeviceAsync(bench.Device);

        differences.Should().BeEquivalentTo(
        [
            new ProfileDifference("axis0.config.disparue", "1", null),
            new ProfileDifference("config.brake_resistance", null, "2"),
        ]);
        differences.Should().OnlyContain(d => !d.CanApply);
        other.CompatibilityWarning(bench.Device).Should().Contain("0.5.1");
        captured.CompatibilityWarning(bench.Device).Should().BeNull();
    }

    [Fact]
    public async Task Apply_writes_only_the_selected_differences()
    {
        Bench bench = await Bench.CreateAsync();
        ConfigProfile profile = await ConfigProfile.CaptureAsync(bench.Device);
        await bench.Odrv.Axis0.Controller.Config.VelLimit.SetAsync(7.5f);
        await bench.Odrv.Axis1.Motor.Config.PolePairs.SetAsync(15);
        IReadOnlyList<ProfileDifference> differences = await profile.CompareToDeviceAsync(bench.Device);

        var failures = await ConfigProfile.ApplyAsync(bench.Device,
            differences.Where(d => d.Path == "axis0.controller.config.vel_limit"));

        failures.Should().BeEmpty();
        (await bench.Odrv.Axis0.Controller.Config.VelLimit.GetAsync()).Should().Be(2f);
        (await bench.Odrv.Axis1.Motor.Config.PolePairs.GetAsync()).Should().Be(15);
    }

    [Fact]
    public async Task Apply_refuses_invalid_values_and_non_configuration_paths_without_stopping()
    {
        Bench bench = await Bench.CreateAsync();
        ProfileDifference[] selected =
        [
            new("axis0.controller.config.vel_limit", "rapide", "2"),
            new("axis0.requested_state", "8", "0"),
            new("axis0.config.disparue", "1", null),
            new("axis0.controller.config.vel_gain", "0.5", "0.16666667"),
        ];

        var failures = await ConfigProfile.ApplyAsync(bench.Device, selected);

        failures.Select(f => f.Path).Should().Equal("axis0.controller.config.vel_limit", "axis0.requested_state",
            "axis0.config.disparue");
        (await bench.Odrv.Axis0.Controller.Config.VelGain.GetAsync()).Should().Be(0.5f);
        (await bench.Odrv.Axis0.CurrentState.GetAsync()).Should().Be(AxisState.Idle);
    }

    [Fact]
    public async Task Two_profiles_can_be_compared_without_a_board()
    {
        Bench bench = await Bench.CreateAsync();
        ConfigProfile before = await ConfigProfile.CaptureAsync(bench.Device);
        await bench.Odrv.Axis0.Motor.Config.CurrentLim.SetAsync(20f);
        ConfigProfile after = await ConfigProfile.CaptureAsync(bench.Device);

        after.CompareTo(before).Should().Equal(new ProfileDifference("axis0.motor.config.current_lim", "20", "10"));
    }
}

public class JogSessionTests
{
    private static async Task<(Bench Bench, JogSession Session)> StartAsync(JogLimits? limits = null)
    {
        Bench bench = await Bench.CreateAsync();
        await bench.CalibrateAsync();
        (JogSession? session, string? refusal) = await JogSession.StartAsync(bench.Device, 0, limits ?? new JogLimits(),
            bench.Guard, bench.Confirm, bench.DelayAsync);
        refusal.Should().BeNull();
        return (bench, session!);
    }

    [Fact]
    public async Task Is_refused_on_an_uncalibrated_axis_and_leaves_the_configuration_untouched()
    {
        Bench bench = await Bench.CreateAsync();
        ConfigProfile before = await ConfigProfile.CaptureAsync(bench.Device);

        (JogSession? session, string? refusal) = await JogSession.StartAsync(bench.Device, 0, new JogLimits(),
            bench.Guard, bench.Confirm, bench.DelayAsync);

        session.Should().BeNull();
        refusal.Should().Contain("calibrés");
        (await bench.Odrv.Axis0.CurrentState.GetAsync()).Should().Be(AxisState.Idle);
        (await before.CompareToDeviceAsync(bench.Device)).Should().BeEmpty();
    }

    [Fact]
    public async Task Is_refused_when_the_user_declines()
    {
        Bench bench = await Bench.CreateAsync();
        await bench.CalibrateAsync();
        var guard = new SafetyGuard();

        (JogSession? session, string? refusal) = await JogSession.StartAsync(bench.Device, 0, new JogLimits(),
            guard, _ => false, bench.DelayAsync);

        session.Should().BeNull();
        refusal.Should().NotBeNullOrEmpty();
        (await bench.Odrv.Axis0.CurrentState.GetAsync()).Should().Be(AxisState.Idle);
    }

    [Fact]
    public async Task Starts_in_closed_loop_at_standstill_with_temporary_limits_and_the_watchdog_armed()
    {
        var (bench, session) = await StartAsync(new JogLimits(MaxVelocity: 1.5f, MaxCurrent: 6f));

        (await bench.Odrv.Axis0.CurrentState.GetAsync()).Should().Be(AxisState.ClosedLoopControl);
        (await bench.Odrv.Axis0.Controller.InputVel.GetAsync()).Should().Be(0f);
        // Relevé sur le banc : avec vel_limit = vitesse maximale, le firmware coupe en OVERSPEED dès 1,2 × la
        // limite (controller.cpp:337), ce que le bruit des capteurs Hall atteint à la vitesse maximale.
        (await bench.Odrv.Axis0.Controller.Config.VelLimit.GetAsync()).Should().Be(1.5f);
        (await bench.Odrv.Axis0.Controller.Config.VelLimitTolerance.GetAsync()).Should().BeApproximately(2.75f / 1.5f, 1e-5f);
        (await bench.Odrv.Axis0.Motor.Config.CurrentLim.GetAsync()).Should().Be(6f);
        (await bench.Odrv.Axis0.Config.EnableWatchdog.GetAsync()).Should().BeTrue();
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Velocity_commands_are_clamped_to_the_limit_and_the_motor_follows()
    {
        var (bench, session) = await StartAsync(new JogLimits(MaxVelocity: 1.5f, RampRate: 50f));
        JogReading? last = null;
        session.ReadingAvailable += (_, reading) => last = reading;

        await session.SetVelocityAsync(9f);

        (await bench.Odrv.Axis0.Controller.InputVel.GetAsync()).Should().Be(1.5f);
        await Bench.WaitUntilAsync(() => last is { Velocity: > 1.4f and < 1.6f });
        session.IsRunning.Should().BeTrue();
        await session.SetVelocityAsync(-9f);
        await Bench.WaitUntilAsync(() => last is { Velocity: < -1.4f });
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Torque_mode_starts_at_zero_torque_with_the_velocity_limiter_on()
    {
        var (bench, session) = await StartAsync(new JogLimits(MaxVelocity: 3f) { Mode = JogMode.Torque, MaxTorque = 0.8f, TorqueRampRate = 4f });

        (await bench.Odrv.Axis0.CurrentState.GetAsync()).Should().Be(AxisState.ClosedLoopControl);
        (await bench.Odrv.Axis0.Controller.Config.ControlMode.GetAsync()).Should().Be(ControlMode.TorqueControl);
        (await bench.Odrv.Axis0.Controller.Config.InputMode.GetAsync()).Should().Be(InputMode.TorqueRamp);
        (await bench.Odrv.Axis0.Controller.Config.TorqueRampRate.GetAsync()).Should().Be(4f);
        (await bench.Odrv.Axis0.Controller.Config.EnableTorqueModeVelLimit.GetAsync()).Should().BeTrue();
        (await bench.Odrv.Axis0.Controller.Config.VelLimit.GetAsync()).Should().Be(3f);
        (await bench.Odrv.Axis0.Controller.InputTorque.GetAsync()).Should().Be(0f);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Torque_commands_are_clamped_and_the_speed_stays_under_the_limit()
    {
        var (bench, session) = await StartAsync(new JogLimits(MaxVelocity: 3f) { Mode = JogMode.Torque, MaxTorque = 0.8f });
        JogReading? last = null;
        session.ReadingAvailable += (_, reading) => last = reading;

        await session.SetVelocityAsync(2f); // ignorée : l'essai est en couple
        (await bench.Odrv.Axis0.Controller.InputVel.GetAsync()).Should().Be(0f);

        await session.SetTorqueAsync(9f);

        (await bench.Odrv.Axis0.Controller.InputTorque.GetAsync()).Should().Be(0.8f);
        await Bench.WaitUntilAsync(() => last is { Velocity: > 2.5f });
        last!.Velocity.Should().BeLessThanOrEqualTo(3.01f);
        session.IsRunning.Should().BeTrue();

        await session.DisposeAsync();
        (await bench.Odrv.Axis0.Controller.InputTorque.GetAsync()).Should().Be(0f);
        (await bench.Odrv.Axis0.CurrentState.GetAsync()).Should().Be(AxisState.Idle);
    }

    [Fact]
    public async Task Torque_commands_are_ignored_in_velocity_mode()
    {
        var (bench, session) = await StartAsync();

        await session.SetTorqueAsync(1f);

        (await bench.Odrv.Axis0.Controller.InputTorque.GetAsync()).Should().Be(0f);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task The_watchdog_is_fed_for_as_long_as_the_session_runs()
    {
        var (bench, session) = await StartAsync(new JogLimits { WatchdogTimeout = 0.5f });
        int readings = 0;
        session.ReadingAvailable += (_, _) => Interlocked.Increment(ref readings);

        // 100 relevés = 10 s de temps simulé, vingt fois le délai du watchdog.
        await Bench.WaitUntilAsync(() => Volatile.Read(ref readings) > 100);

        session.IsRunning.Should().BeTrue();
        (await bench.Odrv.Axis0.Error.GetAsync()).Should().Be(AxisError.None);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Ending_stops_the_motor_and_restores_every_setting()
    {
        Bench bench = await Bench.CreateAsync();
        await bench.CalibrateAsync();
        await bench.Odrv.Axis0.Controller.Config.ControlMode.SetAsync(ControlMode.PositionControl);
        ConfigProfile before = await ConfigProfile.CaptureAsync(bench.Device);
        (JogSession? session, _) = await JogSession.StartAsync(bench.Device, 0, new JogLimits(MaxVelocity: 1f, MaxCurrent: 5f),
            bench.Guard, bench.Confirm, bench.DelayAsync);
        string? reason = null;
        session!.Ended += (_, why) => reason = why;
        await session.SetVelocityAsync(1f);

        await session.DisposeAsync();

        reason.Should().Be("Essai terminé.");
        session.IsRunning.Should().BeFalse();
        (await bench.Odrv.Axis0.CurrentState.GetAsync()).Should().Be(AxisState.Idle);
        (await bench.Odrv.Axis0.Controller.InputVel.GetAsync()).Should().Be(0f);
        (await before.CompareToDeviceAsync(bench.Device)).Should().BeEmpty();
    }

    [Fact]
    public async Task An_error_on_the_axis_ends_the_session_and_restores_the_settings()
    {
        var (bench, session) = await StartAsync();
        string? reason = null;
        session.Ended += (_, why) => reason = why;

        bench.Board.Set("axis0.error", (uint)AxisError.MotorFailed);

        await Bench.WaitUntilAsync(() => reason is not null);
        reason.Should().Contain("abandonné").And.Contain("MOTOR_FAILED");
        session.IsRunning.Should().BeFalse();
        (await bench.Odrv.Axis0.Config.EnableWatchdog.GetAsync()).Should().BeFalse();
        (await bench.Odrv.Axis0.Controller.Config.VelLimit.GetAsync()).Should().Be(10f);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Overheating_ends_the_session()
    {
        var (bench, session) = await StartAsync();
        string? reason = null;
        session.Ended += (_, why) => reason = why;

        bench.Board.Set("axis0.motor.fet_thermistor.temperature", 95f);

        await Bench.WaitUntilAsync(() => reason is not null);
        reason.Should().Contain("95 °C");
        (await bench.Odrv.Axis0.CurrentState.GetAsync()).Should().Be(AxisState.Idle);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task Commands_after_the_end_are_ignored()
    {
        var (bench, session) = await StartAsync();
        await session.DisposeAsync();

        await session.SetVelocityAsync(1f);

        (await bench.Odrv.Axis0.Controller.InputVel.GetAsync()).Should().Be(0f);
    }

    [Theory]
    [InlineData(0f, 10f, 2f)]
    [InlineData(2f, 0f, 2f)]
    [InlineData(2f, 10f, 0f)]
    public async Task Refuses_limits_that_are_not_positive(float velocity, float current, float ramp)
    {
        Bench bench = await Bench.CreateAsync();
        await bench.CalibrateAsync();

        (JogSession? session, string? refusal) = await JogSession.StartAsync(bench.Device, 0,
            new JogLimits(velocity, current, ramp), bench.Guard, bench.Confirm, bench.DelayAsync);

        session.Should().BeNull();
        refusal.Should().Contain("positives");
    }
}
