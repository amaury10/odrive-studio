using FluentAssertions;
using ODrive.Model;
using ODrive.Model.Simulation;
using ODrive.Tuning.Autotune;
using ODrive.Tuning.Profiles;
using Xunit;

namespace ODrive.Tuning.Tests;

public class AutotunerTests
{
    // Une roue d'hoverboard soulevée, à peu près : inertie, frottements et un estimateur de vitesse bruité.
    private static readonly MechanicalLoad Wheel = new(Inertia: 0.02, Viscous: 0.01, Coulomb: 0.05, VelocityNoise: 0.002);

    private static async Task<Bench> CalibratedAsync(MechanicalLoad? load = null)
    {
        Bench bench = await Bench.CreateAsync(new SimulatedMotorOptions { Mechanics = load ?? Wheel });
        await bench.CalibrateAsync();
        return bench;
    }

    private static AutotuneOptions Options(Bench bench) => new()
    {
        Axis = 0,
        SamplePeriod = TimeSpan.FromMilliseconds(0.25),
        Delay = bench.DelayAsync,
        Clock = () => bench.Now,
    };

    // ROADMAP phase 6 : sur le simulateur, gains retrouvés à ±15 % des valeurs théoriques.
    [Theory]
    [InlineData(0.02, 0.01, 0.05)]
    [InlineData(0.2, 0.05, 0.15)]
    public async Task Finds_the_inertia_and_gains_within_15_percent_and_validates_them(double inertia, double viscous, double coulomb)
    {
        var load = new MechanicalLoad(inertia, viscous, coulomb, VelocityNoise: 0.002);
        Bench bench = await CalibratedAsync(load);

        (AutotuneResult? result, string? refusal) = await Autotuner.RunAsync(bench.Device, Options(bench), bench.Guard, bench.Confirm);

        refusal.Should().BeNull();
        result!.Plant.Inertia.Should().BeApproximately(inertia, inertia * 0.15);
        double theoretical = inertia * result.Proposed.Bandwidth;
        result.Proposed.VelGain.Should().BeApproximately(theoretical, theoretical * 0.15);
        result.Proposed.PosGain.Should().BeApproximately(result.Proposed.Bandwidth / 4, result.Proposed.Bandwidth / 4 * 0.31);
        result.VelocityStep.Overshoot.Should().BeLessThan(0.2);
        result.VelocityStep.Oscillating.Should().BeFalse();
        result.PositionStep.Overshoot.Should().BeLessThan(0.2);
        result.PositionStep.Oscillating.Should().BeFalse();
    }

    [Fact]
    public async Task Leaves_the_board_idle_with_its_configuration_untouched()
    {
        Bench bench = await CalibratedAsync();
        ConfigProfile before = await ConfigProfile.CaptureAsync(bench.Device);

        await Autotuner.RunAsync(bench.Device, Options(bench), bench.Guard, bench.Confirm);

        (await bench.Odrv.Axis0.CurrentState.GetAsync()).Should().Be(AxisState.Idle);
        (await before.CompareToDeviceAsync(bench.Device)).Should().BeEmpty("l'autotune propose, il n'applique rien");
    }

    [Fact]
    public async Task Proposed_gains_are_written_only_on_apply()
    {
        Bench bench = await CalibratedAsync();
        (AutotuneResult? result, _) = await Autotuner.RunAsync(bench.Device, Options(bench), bench.Guard, bench.Confirm);

        await Autotuner.ApplyAsync(bench.Device, 0, result!.Proposed);

        (await bench.Odrv.Axis0.Controller.Config.VelGain.GetAsync()).Should().BeApproximately((float)result.Proposed.VelGain, 1e-4f);
        (await bench.Odrv.Axis0.Controller.Config.VelIntegratorGain.GetAsync())
            .Should().BeApproximately((float)result.Proposed.VelIntegratorGain, 1e-3f);
        (await bench.Odrv.Axis0.Controller.Config.PosGain.GetAsync()).Should().BeApproximately((float)result.Proposed.PosGain, 1e-4f);
    }

    [Fact]
    public async Task Nothing_moves_when_the_user_declines()
    {
        Bench bench = await CalibratedAsync();

        // Garde-fou neuf : celui du banc a déjà reçu la confirmation pendant la calibration (une fois par session).
        (AutotuneResult? result, string? refusal) = await Autotuner.RunAsync(bench.Device, Options(bench), new SafetyGuard(), _ => false);

        result.Should().BeNull();
        refusal.Should().NotBeNullOrEmpty();
        (await bench.Odrv.Axis0.CurrentState.GetAsync()).Should().Be(AxisState.Idle);
    }

    [Fact]
    public async Task A_motor_that_does_not_move_is_reported_and_the_board_restored()
    {
        Bench bench = await CalibratedAsync(new MechanicalLoad(Inertia: 0.02, Coulomb: 50));
        ConfigProfile before = await ConfigProfile.CaptureAsync(bench.Device);

        (AutotuneResult? result, string? refusal) = await Autotuner.RunAsync(bench.Device, Options(bench), bench.Guard, bench.Confirm);

        result.Should().BeNull();
        refusal.Should().Contain("mouvement");
        (await bench.Odrv.Axis0.CurrentState.GetAsync()).Should().Be(AxisState.Idle);
        (await before.CompareToDeviceAsync(bench.Device)).Should().BeEmpty();
    }

    [Fact]
    public async Task Cancelling_stops_the_motor_and_restores_the_configuration()
    {
        Bench bench = await CalibratedAsync();
        ConfigProfile before = await ConfigProfile.CaptureAsync(bench.Device);
        using var cancel = new CancellationTokenSource();
        var progress = new SyncProgress<string>(message =>
        {
            if (message.Contains("Validation", StringComparison.Ordinal))
            {
                cancel.Cancel();
            }
        });

        Func<Task> run = () => Autotuner.RunAsync(bench.Device, Options(bench) with { Progress = progress }, bench.Guard,
            bench.Confirm, cancel.Token);

        await run.Should().ThrowAsync<OperationCanceledException>();
        (await bench.Odrv.Axis0.CurrentState.GetAsync()).Should().Be(AxisState.Idle);
        (await before.CompareToDeviceAsync(bench.Device)).Should().BeEmpty();
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
