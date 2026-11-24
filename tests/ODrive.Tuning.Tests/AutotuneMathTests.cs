using FluentAssertions;
using ODrive.Tuning.Autotune;
using Xunit;

namespace ODrive.Tuning.Tests;

/// <summary>Système mécanique de référence pour fabriquer des signaux synthétiques (unités firmware : tours).</summary>
internal static class SyntheticPlant
{
    /// <summary>
    /// Paliers de couple appliqués depuis l'arrêt, chacun suivi d'une roue libre, échantillonnés avec une gigue
    /// d'horloge et du bruit de vitesse (comme une lecture USB).
    /// </summary>
    public static List<MotionSample> TorqueSteps(double inertia, double viscous, double coulomb, double[] torques,
        double stepDuration = 0.25, double coastDuration = 0.3, double noise = 0.002, int seed = 1, double samplePeriod = 0.0004)
    {
        var random = new Random(seed);
        var samples = new List<MotionSample>();
        double h = Math.Min(1e-4, samplePeriod / 2);
        double t = 0, velocity = 0, nextSample = 0;
        foreach (double torque in torques)
        {
            foreach ((double applied, double duration) in new[] { (torque, stepDuration), (0d, coastDuration) })
            {
                double end = t + duration;
                while (t < end)
                {
                    double friction = viscous * velocity + coulomb * Math.Sign(velocity);
                    if (velocity == 0 && Math.Abs(applied) <= coulomb)
                    {
                        friction = applied; // adhérence : le frottement statique équilibre le couple
                    }
                    double next = velocity + (applied - friction) / inertia * h;
                    // La roue libre s'arrête au lieu de repartir en arrière sous l'effet du frottement sec.
                    velocity = applied == 0 && Math.Sign(next) != Math.Sign(velocity) ? 0 : next;
                    t += h;
                    if (t >= nextSample)
                    {
                        samples.Add(new MotionSample(t, applied, velocity + noise * Gaussian(random)));
                        nextSample = t + samplePeriod * (1 + random.NextDouble()); // lecture USB irrégulière
                    }
                }
            }
        }
        return samples;
    }

    /// <summary>Réponse indicielle d'un second ordre (pulsation propre, amortissement), échantillonnée à 1 kHz.</summary>
    public static List<(double Time, double Value)> SecondOrderStep(double naturalFrequency, double damping,
        double duration = 1.0, double from = 0, double to = 1)
    {
        var samples = new List<(double, double)>();
        double y = from, dy = 0;
        const double h = 1e-5;
        int every = 100;
        for (int i = 0; i * h < duration; i++)
        {
            double ddy = naturalFrequency * naturalFrequency * (to - y) - 2 * damping * naturalFrequency * dy;
            dy += ddy * h;
            y += dy * h;
            if (i % every == 0)
            {
                samples.Add((i * h, y));
            }
        }
        return samples;
    }

    private static double Gaussian(Random random) =>
        Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}

public class PlantIdentificationTests
{
    [Theory]
    [InlineData(0.05, 0.02, 0.10)]
    [InlineData(0.20, 0.00, 0.30)]
    [InlineData(0.01, 0.05, 0.02)]
    public void Recovers_inertia_and_friction_from_torque_steps(double inertia, double viscous, double coulomb)
    {
        double peak = 6 * coulomb + 0.3;
        List<MotionSample> samples = SyntheticPlant.TorqueSteps(inertia, viscous, coulomb,
            [peak / 2, -peak / 2, peak, -peak]);

        PlantEstimate estimate = PlantIdentification.Fit(samples);

        estimate.Inertia.Should().BeApproximately(inertia, inertia * 0.05);
        estimate.Coulomb.Should().BeApproximately(coulomb, 0.02 + coulomb * 0.1);
        estimate.Viscous.Should().BeApproximately(viscous, 0.02);
        estimate.RSquared.Should().BeGreaterThan(0.9);
    }

    // Déroulé réel de l'autotune : palier court (coupé vers 2 tours/s), longue roue libre. Relevé en temps réel sur le
    // simulateur : intervalle médian de 8,7 µs. Une fenêtre de dérivation de 21
    // lectures ne couvre alors qu'une fraction de milliseconde et le bruit de vitesse écrasait l'inertie.
    [Theory]
    [InlineData(0.000005)]
    [InlineData(0.0002)]
    public void Fast_sampling_does_not_bias_the_inertia(double samplePeriod)
    {
        List<MotionSample> samples = SyntheticPlant.TorqueSteps(0.02, 0.01, 0.05, [0.5, -0.5, 0.25, -0.25], stepDuration: 0.08, coastDuration: 0.8,
            samplePeriod: samplePeriod);

        PlantEstimate estimate = PlantIdentification.Fit(samples);

        estimate.Inertia.Should().BeApproximately(0.02, 0.002);
        estimate.RSquared.Should().BeGreaterThan(0.9);
    }

    [Fact]
    public void Refuses_a_recording_where_the_motor_never_moved()
    {
        List<MotionSample> samples = SyntheticPlant.TorqueSteps(0.05, 0.02, 0.5, [0.2, -0.2]);

        Action fit = () => PlantIdentification.Fit(samples);

        fit.Should().Throw<InvalidOperationException>().WithMessage("*pas assez*");
    }
}

public class GainDesignTests
{
    // docs/AUTOTUNE.md, étape 2 : vel_gain = J·ωc, vel_integrator_gain = vel_gain·ωc/4, pos_gain = ωc/4.
    [Fact]
    public void Gains_follow_the_inertia_and_the_chosen_bandwidth()
    {
        LoopGains gains = GainDesign.Compute(inertia: 0.05, bandwidth: 100);

        gains.VelGain.Should().BeApproximately(5, 1e-9);
        gains.VelIntegratorGain.Should().BeApproximately(125, 1e-9);
        gains.PosGain.Should().BeApproximately(25, 1e-9);
        gains.Bandwidth.Should().Be(100);
    }

    [Fact]
    public void Bandwidth_is_capped_by_the_encoder_and_the_current_loop()
    {
        GainDesign.MaxBandwidth(encoderBandwidth: 1000, currentBandwidth: 1000).Should().Be(250);
        GainDesign.MaxBandwidth(encoderBandwidth: 100, currentBandwidth: 1000).Should().Be(25);
        GainDesign.MaxBandwidth(encoderBandwidth: 1000, currentBandwidth: 300).Should().Be(75);
    }

    [Theory]
    [InlineData(Stiffness.Soft, 0.25)]
    [InlineData(Stiffness.Normal, 0.5)]
    [InlineData(Stiffness.Stiff, 1.0)]
    public void Stiffness_presets_are_fractions_of_the_maximum_bandwidth(Stiffness stiffness, double fraction)
    {
        GainDesign.Bandwidth(stiffness, maxBandwidth: 200).Should().BeApproximately(200 * fraction, 1e-9);
    }
}

public class StepAnalysisTests
{
    [Fact]
    public void A_well_damped_response_has_little_overshoot_and_settles()
    {
        var samples = SyntheticPlant.SecondOrderStep(naturalFrequency: 60, damping: 0.8);

        StepMetrics metrics = StepAnalysis.Analyze(samples, from: 0, to: 1);

        metrics.Overshoot.Should().BeLessThan(0.03);
        metrics.RiseTime.Should().NotBeNull().And.BeInRange(0.02, 0.06);
        metrics.SettlingTime.Should().NotBeNull().And.BeLessThan(0.15);
        metrics.Oscillating.Should().BeFalse();
        metrics.SteadyStateError.Should().BeLessThan(0.01);
    }

    [Fact]
    public void A_lightly_damped_response_shows_its_overshoot()
    {
        var samples = SyntheticPlant.SecondOrderStep(naturalFrequency: 60, damping: 0.2);

        StepMetrics metrics = StepAnalysis.Analyze(samples, from: 0, to: 1);

        // Dépassement théorique d'un second ordre : exp(−ζπ/√(1−ζ²)) = 52,7 % pour ζ = 0,2.
        metrics.Overshoot.Should().BeApproximately(0.527, 0.02);
        metrics.Oscillating.Should().BeFalse("l'oscillation s'amortit dans la fenêtre");
    }

    [Fact]
    public void A_sustained_oscillation_is_detected()
    {
        var samples = SyntheticPlant.SecondOrderStep(naturalFrequency: 60, damping: 0.01);

        StepAnalysis.Analyze(samples, from: 0, to: 1).Oscillating.Should().BeTrue();
    }

    [Fact]
    public void Works_for_a_negative_step_and_an_offset_start()
    {
        var samples = SyntheticPlant.SecondOrderStep(naturalFrequency: 60, damping: 0.5, from: 2, to: 1.5);

        StepMetrics metrics = StepAnalysis.Analyze(samples, from: 2, to: 1.5);

        metrics.Overshoot.Should().BeApproximately(0.163, 0.02); // ζ = 0,5
        metrics.SteadyStateError.Should().BeLessThan(0.01);
    }
}
