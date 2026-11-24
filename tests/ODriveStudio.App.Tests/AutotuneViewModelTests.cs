using FluentAssertions;
using ODrive.Model.Simulation;
using ODriveStudio.App.ViewModels;
using Xunit;

namespace ODriveStudio.App.Tests;

public class AutotuneViewModelTests
{
    private static readonly MechanicalLoad Wheel = new(Inertia: 0.02, Viscous: 0.01, Coulomb: 0.05, VelocityNoise: 0.002);

    private static AutotuneViewModel Create(MotorBench bench) => new(bench.Session)
    {
        Delay = bench.DelayAsync,
        Clock = () => bench.Now,
        SamplePeriod = TimeSpan.FromMilliseconds(0.25),
    };

    [Fact]
    public async Task Run_proposes_gains_without_writing_them_then_apply_writes_them()
    {
        await using MotorBench bench = await MotorBench.CreateAsync(new SimulatedMotorOptions { Mechanics = Wheel });
        await bench.CalibratedAsync();
        float velGainBefore = await bench.Odrv.Axis0.Controller.Config.VelGain.GetAsync();
        AutotuneViewModel autotune = Create(bench);

        await autotune.RunCommand.ExecuteAsync(null);

        autotune.HasResult.Should().BeTrue();
        autotune.Inertia.Should().EndWith("Nm/(tour/s²)");
        autotune.Gains.Select(row => row.Name).Should().Equal("vel_gain", "vel_integrator_gain", "pos_gain");
        autotune.Validation.Should().Contain("vitesse").And.Contain("position");
        autotune.Log.Should().Contain(line => line.Contains("Identification", StringComparison.Ordinal));
        (await bench.Odrv.Axis0.Controller.Config.VelGain.GetAsync()).Should().Be(velGainBefore, "rien n'est appliqué sans demande");
        autotune.ApplyCommand.CanExecute(null).Should().BeTrue();

        await autotune.ApplyCommand.ExecuteAsync(null);

        float proposed = float.Parse(autotune.Gains[0].Proposed, System.Globalization.CultureInfo.InvariantCulture);
        (await bench.Odrv.Axis0.Controller.Config.VelGain.GetAsync()).Should().BeApproximately(proposed, proposed * 1e-3f);
        autotune.Gains[0].Current.Should().Be(autotune.Gains[0].Proposed);
        bench.Status.Last().Should().Contain("appliqués");
    }

    [Fact]
    public async Task An_uncalibrated_axis_is_refused_with_an_explanation()
    {
        await using MotorBench bench = await MotorBench.CreateAsync(new SimulatedMotorOptions { Mechanics = Wheel });
        AutotuneViewModel autotune = Create(bench);

        await autotune.RunCommand.ExecuteAsync(null);

        autotune.HasResult.Should().BeFalse();
        autotune.Message.Should().Contain("calibrés");
        autotune.ApplyCommand.CanExecute(null).Should().BeFalse();
    }
}
