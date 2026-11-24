using System.Diagnostics;
using System.Globalization;
using System.IO;
using ODrive.Model;
using ODrive.Protocol;
using ODrive.Tuning.Commissioning;

namespace ODrive.Tuning.Autotune;

/// <summary>Paramètres d'un autotune. Les valeurs par défaut conviennent à une roue soulevée.</summary>
public sealed record AutotuneOptions
{
    public int Axis { get; init; }

    public Stiffness Stiffness { get; init; } = Stiffness.Normal;

    /// <summary>Couple des paliers d'identification, en fraction du couple disponible (ajusté ensuite si besoin).</summary>
    public double TestTorqueFraction { get; init; } = 0.1;

    /// <summary>Vitesse à laquelle un palier de couple est coupé, en tours/s.</summary>
    public double MaxSpeed { get; init; } = 2;

    /// <summary>Amplitude de l'échelon de validation en vitesse, en tours/s (bornée à la moitié de <see cref="MaxSpeed"/>).</summary>
    public double VelocityStep { get; init; } = 0.5;

    /// <summary>Amplitude de l'échelon de validation en position, en tours.</summary>
    public double PositionStep { get; init; } = 0.1;

    /// <summary>Dépassement au-delà duquel la bande passante est réduite de 30 % (docs/AUTOTUNE.md, étape 3).</summary>
    public double MaxOvershoot { get; init; } = 0.2;

    public int MaxIterations { get; init; } = 3;

    /// <summary>Délai du watchdog de l'axe pendant l'autotune, en secondes ; l'outil le nourrit.</summary>
    public float WatchdogTimeout { get; init; } = 1f;

    /// <summary>Attente entre deux lectures ; zéro sur une vraie carte (lecture aussi rapide que l'USB le permet).</summary>
    public TimeSpan SamplePeriod { get; init; } = TimeSpan.Zero;

    /// <summary>Attente injectable (le simulateur avance son horloge avec).</summary>
    public DelayAsync Delay { get; init; } = Task.Delay;

    /// <summary>Horloge des relevés ; par défaut le temps réel.</summary>
    public Func<TimeSpan>? Clock { get; init; }

    /// <summary>Étapes en cours, en français.</summary>
    public IProgress<string>? Progress { get; init; }
}

/// <summary>Résultat d'un autotune : modèle identifié, gains proposés et réponses de validation.</summary>
/// <param name="Initial">Gains de la carte avant l'autotune ; leur bande passante est calculée avec l'inertie identifiée.</param>
/// <param name="Validated">Vrai si les deux échelons sont sans dépassement excessif ni oscillation.</param>
public sealed record AutotuneResult(
    PlantEstimate Plant,
    LoopGains Initial,
    LoopGains Proposed,
    StepMetrics VelocityStep,
    StepMetrics PositionStep,
    bool Validated,
    IReadOnlyList<MotionSample> Identification,
    IReadOnlyList<(double Time, double Value)> VelocityResponse,
    IReadOnlyList<(double Time, double Value)> PositionResponse);

/// <summary>
/// Autotune des boucles de position et de vitesse (docs/AUTOTUNE.md) : identification de l'inertie et des
/// frottements par paliers de couple, calcul des gains pour une bande passante choisie, validation par échelons
/// de vitesse puis de position. Sécurité (docs/SAFETY.md) : <see cref="SafetyGuard"/>, réglages temporaires
/// restaurés à la fin quoi qu'il arrive, axe remis en IDLE, watchdog armé et nourri. Rien n'est appliqué :
/// <see cref="ApplyAsync"/> écrit les gains proposés à la demande de l'utilisateur.
/// </summary>
public static class Autotuner
{
    public static async Task<(AutotuneResult? Result, string? Refusal)> RunAsync(ODriveDevice device, AutotuneOptions options,
        SafetyGuard guard, Func<string, bool> confirm, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(guard);
        var run = new Run(device, options);
        if (!await run.ReadAsync<bool>("motor.is_calibrated", ct).ConfigureAwait(false)
            || !await run.ReadAsync<bool>("encoder.is_ready", ct).ConfigureAwait(false))
        {
            return (null, "Le moteur et le capteur doivent être calibrés avant l'autotune.");
        }

        SafetyDecision decision = await guard.AuthorizeMotionAsync(device, confirm, ct).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return (null, decision.Reason ?? "Autotune refusé.");
        }

        try
        {
            return (await run.ExecuteAsync(ct).ConfigureAwait(false), null);
        }
        catch (InvalidOperationException e)
        {
            return (null, e.Message);
        }
        finally
        {
            await run.EndAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Écrit des gains dans la carte, sans sauvegarder.</summary>
    public static async Task ApplyAsync(ODriveDevice device, int axis, LoopGains gains, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(gains);
        string prefix = string.Create(CultureInfo.InvariantCulture, $"axis{axis}.controller.config.");
        await Write(device, prefix + "vel_gain", gains.VelGain, ct).ConfigureAwait(false);
        await Write(device, prefix + "vel_integrator_gain", gains.VelIntegratorGain, ct).ConfigureAwait(false);
        await Write(device, prefix + "pos_gain", gains.PosGain, ct).ConfigureAwait(false);
    }

    private static Task Write(ODriveDevice device, string path, object value, CancellationToken ct)
    {
        PropertyNode property = device.Schema.GetProperty(path);
        return device.WriteAsync(property, HoverboardPreset.ToEndpointValue(property.Type, value), ct);
    }

    private sealed class Run
    {
        private const double StepDuration = 0.5;
        private const double CoastDuration = 1.5;
        private const int MinAccelerationSamples = 150;
        private static readonly TimeSpan Settle = TimeSpan.FromSeconds(0.3);

        private readonly ODriveDevice _device;
        private readonly AutotuneOptions _options;
        private readonly string _prefix;
        private readonly Func<TimeSpan> _clock;
        private readonly List<(PropertyNode Property, object Value)> _saved = [];
        private TimeSpan _lastFeed;
        private int _sampleCount;
        private bool _armed;

        public Run(ODriveDevice device, AutotuneOptions options)
        {
            _device = device;
            _options = options;
            _prefix = string.Create(CultureInfo.InvariantCulture, $"axis{options.Axis}.");
            if (options.Clock is { } clock)
            {
                _clock = clock;
            }
            else
            {
                var watch = Stopwatch.StartNew();
                _clock = () => watch.Elapsed;
            }
        }

        public async Task<AutotuneResult> ExecuteAsync(CancellationToken ct)
        {
            float torqueConstant = await ReadAsync<float>("motor.config.torque_constant", ct).ConfigureAwait(false);
            double available = Math.Min(await ReadAsync<float>("motor.config.current_lim", ct).ConfigureAwait(false) * torqueConstant,
                await ReadAsync<float>("motor.config.torque_lim", ct).ConfigureAwait(false));
            var initialGains = new LoopGains(await ReadAsync<float>("controller.config.pos_gain", ct).ConfigureAwait(false),
                await ReadAsync<float>("controller.config.vel_gain", ct).ConfigureAwait(false),
                await ReadAsync<float>("controller.config.vel_integrator_gain", ct).ConfigureAwait(false), 0);
            double maxBandwidth = GainDesign.MaxBandwidth(
                await ReadAsync<float>("encoder.config.bandwidth", ct).ConfigureAwait(false),
                await ReadAsync<float>("motor.config.current_control_bandwidth", ct).ConfigureAwait(false));

            // Réglages temporaires : tout ce qui est écrit ici est restauré par EndAsync.
            await OverrideAsync("controller.config.control_mode", ControlMode.TorqueControl, ct).ConfigureAwait(false);
            await OverrideAsync("controller.config.input_mode", InputMode.Passthrough, ct).ConfigureAwait(false);
            // Le limiteur de vitesse du mode couple écrête le couple d'essai (controller.cpp:137-141, 388-394).
            await OverrideAsync("controller.config.enable_torque_mode_vel_limit", false, ct).ConfigureAwait(false);
            await OverrideAsync("controller.config.enable_vel_limit", true, ct).ConfigureAwait(false);
            await OverrideAsync("controller.config.vel_limit", (float)(2 * _options.MaxSpeed), ct).ConfigureAwait(false);
            await OverrideAsync("controller.config.pos_gain", (float)initialGains.PosGain, ct).ConfigureAwait(false);
            await OverrideAsync("controller.config.vel_gain", (float)initialGains.VelGain, ct).ConfigureAwait(false);
            await OverrideAsync("controller.config.vel_integrator_gain", (float)initialGains.VelIntegratorGain, ct).ConfigureAwait(false);
            await OverrideAsync("config.watchdog_timeout", _options.WatchdogTimeout, ct).ConfigureAwait(false);
            await WriteAsync("controller.input_torque", 0f, ct).ConfigureAwait(false);
            await WriteAsync("controller.input_vel", 0f, ct).ConfigureAwait(false);
            await FeedAsync(ct).ConfigureAwait(false);
            await OverrideAsync("config.enable_watchdog", true, ct).ConfigureAwait(false);

            await WriteAsync("requested_state", (byte)AxisState.ClosedLoopControl, ct).ConfigureAwait(false);
            _armed = true;
            await _options.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            await EnsureClosedLoopAsync(ct).ConfigureAwait(false);

            // Étape 1 : identification.
            List<MotionSample> identification = await IdentifyAsync(available, ct).ConfigureAwait(false);
            PlantEstimate plant = PlantIdentification.Fit(identification);
            if (!(plant.Inertia > 0) || plant.RSquared < 0.5)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                    $"Identification peu fiable (J = {plant.Inertia:G3} Nm/(tour/s²), R² = {plant.RSquared:F2}) : rien n'est proposé."));
            }

            // Étapes 2 et 3 : gains, puis validation en vitesse avec réduction de la bande passante si besoin.
            double bandwidth = GainDesign.Bandwidth(_options.Stiffness, maxBandwidth);
            LoopGains gains = GainDesign.Compute(plant.Inertia, bandwidth);
            StepMetrics velocityMetrics = null!;
            List<(double, double)> velocityResponse = [];
            for (int i = 0; i < _options.MaxIterations; i++)
            {
                gains = GainDesign.Compute(plant.Inertia, bandwidth);
                Report(string.Create(CultureInfo.InvariantCulture,
                    $"Validation en vitesse : ωc = {bandwidth:F1} rad/s, vel_gain = {gains.VelGain:G4}, vel_integrator_gain = {gains.VelIntegratorGain:G4}"));
                (velocityMetrics, velocityResponse) = await VelocityStepAsync(gains, ct).ConfigureAwait(false);
                if (Acceptable(velocityMetrics))
                {
                    break;
                }
                bandwidth *= 0.7;
            }

            StepMetrics positionMetrics = null!;
            List<(double, double)> positionResponse = [];
            for (int i = 0; i < _options.MaxIterations; i++)
            {
                Report(string.Create(CultureInfo.InvariantCulture, $"Validation en position : pos_gain = {gains.PosGain:G4}"));
                (positionMetrics, positionResponse) = await PositionStepAsync(gains, ct).ConfigureAwait(false);
                if (Acceptable(positionMetrics))
                {
                    break;
                }
                gains = gains with { PosGain = gains.PosGain * 0.7 };
            }

            return new AutotuneResult(plant, initialGains with { Bandwidth = initialGains.VelGain / plant.Inertia }, gains,
                velocityMetrics, positionMetrics, Acceptable(velocityMetrics) && Acceptable(positionMetrics),
                identification, velocityResponse, positionResponse);
        }

        private bool Acceptable(StepMetrics metrics) => metrics.Overshoot <= _options.MaxOvershoot && !metrics.Oscillating;

        // Paliers de couple ±τ puis ±τ/2, chacun suivi d'une roue libre. Le couple de départ est ajusté pour que la
        // roue bouge, et assez lentement pour donner assez de relevés pendant l'accélération.
        private async Task<List<MotionSample>> IdentifyAsync(double available, CancellationToken ct)
        {
            double torque = _options.TestTorqueFraction * available;
            var samples = new List<MotionSample>();
            for (int attempt = 0; ; attempt++)
            {
                (List<MotionSample> probe, int accelerating, double peak) = await TorqueStepAsync(torque, ct).ConfigureAwait(false);
                if (peak < 0.1)
                {
                    if (2 * torque > 0.8 * available || attempt >= 6)
                    {
                        throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                            $"Identification impossible : pas de mouvement jusqu'à {torque:F2} Nm. Roue bloquée ou frottement trop fort ?"));
                    }
                    torque *= 2;
                    continue;
                }
                if (accelerating < MinAccelerationSamples && attempt < 6)
                {
                    torque /= 2;
                    continue;
                }
                samples.AddRange(probe);
                break;
            }

            foreach (double level in new[] { -torque, torque / 2, -torque / 2 })
            {
                samples.AddRange((await TorqueStepAsync(level, ct).ConfigureAwait(false)).Samples);
            }
            return samples;
        }

        private async Task<(List<MotionSample> Samples, int Accelerating, double Peak)> TorqueStepAsync(double torque, CancellationToken ct)
        {
            Report(string.Create(CultureInfo.InvariantCulture, $"Identification : palier de {torque:+0.###;-0.###} Nm"));
            var samples = new List<MotionSample>();
            await WriteAsync("controller.input_torque", (float)torque, ct).ConfigureAwait(false);
            double start = Now();
            int accelerating = 0;
            double peak = 0;
            while (Now() - start < StepDuration)
            {
                double velocity = await SampleAsync("encoder.vel_estimate", ct).ConfigureAwait(false);
                samples.Add(new MotionSample(Now(), torque, velocity));
                peak = Math.Max(peak, Math.Abs(velocity));
                accelerating++;
                if (Math.Abs(velocity) >= _options.MaxSpeed)
                {
                    break;
                }
            }

            await WriteAsync("controller.input_torque", 0f, ct).ConfigureAwait(false);
            start = Now();
            int still = 0;
            while (Now() - start < CoastDuration && still < 20)
            {
                double velocity = await SampleAsync("encoder.vel_estimate", ct).ConfigureAwait(false);
                samples.Add(new MotionSample(Now(), 0, velocity));
                still = Math.Abs(velocity) < 0.02 ? still + 1 : 0;
            }
            return (samples, accelerating, peak);
        }

        private async Task<(StepMetrics, List<(double, double)>)> VelocityStepAsync(LoopGains gains, CancellationToken ct)
        {
            await WriteGainsAsync(gains, ct).ConfigureAwait(false);
            await WriteAsync("controller.input_vel", 0f, ct).ConfigureAwait(false);
            await WriteAsync("controller.config.control_mode", (byte)ControlMode.VelocityControl, ct).ConfigureAwait(false);
            await HoldAsync(Settle, "encoder.vel_estimate", ct).ConfigureAwait(false);

            double step = Math.Min(_options.VelocityStep, _options.MaxSpeed / 2);
            await WriteAsync("controller.input_vel", (float)step, ct).ConfigureAwait(false);
            List<(double, double)> response = await RecordAsync("encoder.vel_estimate",
                TimeSpan.FromSeconds(Math.Clamp(12 / gains.Bandwidth, 0.3, 3)), ct).ConfigureAwait(false);
            await WriteAsync("controller.input_vel", 0f, ct).ConfigureAwait(false);
            await HoldAsync(Settle, "encoder.vel_estimate", ct).ConfigureAwait(false);
            return (StepAnalysis.Analyze(response, 0, step), response);
        }

        private async Task<(StepMetrics, List<(double, double)>)> PositionStepAsync(LoopGains gains, CancellationToken ct)
        {
            await WriteGainsAsync(gains, ct).ConfigureAwait(false);
            await WriteAsync("controller.input_vel", 0f, ct).ConfigureAwait(false);
            double start = await ReadAsync<float>("encoder.pos_estimate", ct).ConfigureAwait(false);
            await WriteAsync("controller.input_pos", (float)start, ct).ConfigureAwait(false);
            await WriteAsync("controller.config.control_mode", (byte)ControlMode.PositionControl, ct).ConfigureAwait(false);
            await HoldAsync(Settle, "encoder.pos_estimate", ct).ConfigureAwait(false);

            double target = start + _options.PositionStep;
            await WriteAsync("controller.input_pos", (float)target, ct).ConfigureAwait(false);
            List<(double, double)> response = await RecordAsync("encoder.pos_estimate",
                TimeSpan.FromSeconds(Math.Clamp(40 / gains.Bandwidth, 0.5, 5)), ct).ConfigureAwait(false);
            return (StepAnalysis.Analyze(response, start, target), response);
        }

        private async Task WriteGainsAsync(LoopGains gains, CancellationToken ct)
        {
            await WriteAsync("controller.config.vel_gain", (float)gains.VelGain, ct).ConfigureAwait(false);
            await WriteAsync("controller.config.vel_integrator_gain", (float)gains.VelIntegratorGain, ct).ConfigureAwait(false);
            await WriteAsync("controller.config.pos_gain", (float)gains.PosGain, ct).ConfigureAwait(false);
        }

        private async Task HoldAsync(TimeSpan duration, string path, CancellationToken ct) =>
            await RecordAsync(path, duration, ct).ConfigureAwait(false);

        private async Task<List<(double, double)>> RecordAsync(string path, TimeSpan duration, CancellationToken ct)
        {
            var samples = new List<(double, double)>();
            double start = Now();
            while (Now() - start < duration.TotalSeconds)
            {
                double value = await SampleAsync(path, ct).ConfigureAwait(false);
                samples.Add((Now(), value));
            }
            return samples;
        }

        // Une lecture, avec le rythme, le watchdog et la surveillance de l'axe.
        private async Task<double> SampleAsync(string path, CancellationToken ct)
        {
            if (_options.SamplePeriod > TimeSpan.Zero)
            {
                await _options.Delay(_options.SamplePeriod, ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            if (_clock() - _lastFeed > TimeSpan.FromSeconds(_options.WatchdogTimeout / 4))
            {
                await FeedAsync(ct).ConfigureAwait(false);
            }
            if (++_sampleCount % 64 == 0)
            {
                await EnsureClosedLoopAsync(ct).ConfigureAwait(false);
            }

            double value = await ReadAsync<float>(path, ct).ConfigureAwait(false);
            if (path == "encoder.vel_estimate" && Math.Abs(value) > 2.5 * _options.MaxSpeed)
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                    $"Autotune abandonné : vitesse de {value:F2} tours/s, au-delà de la limite de l'essai."));
            }
            return value;
        }

        private async Task EnsureClosedLoopAsync(CancellationToken ct)
        {
            if ((AxisState)await ReadAsync<byte>("current_state", ct).ConfigureAwait(false) == AxisState.ClosedLoopControl)
            {
                return;
            }
            IReadOnlyList<ActiveError> errors = await ErrorDecoder.ReadAllAsync(_device, ct).ConfigureAwait(false);
            throw new InvalidOperationException("Autotune abandonné : l'axe a quitté la boucle fermée"
                + (errors.Count > 0 ? " : " + string.Join(", ", errors.Select(e => $"{e.Name} ({e.Source})")) : "."));
        }

        private double Now() => _clock().TotalSeconds;

        private void Report(string message) => _options.Progress?.Report(message);

        // Seul watchdog_feed() nourrit le watchdog en USB natif (docs/SAFETY.md, axis.cpp:166).
        private async Task FeedAsync(CancellationToken ct)
        {
            await _device.CallAsync(_prefix + "watchdog_feed", [], ct).ConfigureAwait(false);
            _lastFeed = _clock();
        }

        // Arrêt puis restauration, chaque étape tentée même si la précédente échoue (comme JogSession).
        public async Task EndAsync()
        {
            if (_armed)
            {
                await TryAsync(() => _device.WriteAsync(_device.Schema.GetProperty(_prefix + "requested_state"),
                    (byte)AxisState.Idle, RequestPriority.Emergency, CancellationToken.None)).ConfigureAwait(false);
            }
            await TryAsync(() => WriteAsync("controller.input_torque", 0f, CancellationToken.None)).ConfigureAwait(false);
            await TryAsync(() => WriteAsync("controller.input_vel", 0f, CancellationToken.None)).ConfigureAwait(false);
            for (int i = _saved.Count - 1; i >= 0; i--)
            {
                (PropertyNode property, object value) = _saved[i];
                await TryAsync(() => _device.WriteAsync(property, value, CancellationToken.None)).ConfigureAwait(false);
            }
        }

        private static async Task TryAsync(Func<Task> action)
        {
            try
            {
                await action().ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or ODriveProtocolException)
            {
                // Liaison perdue : le watchdog de la carte arrête le moteur.
            }
        }

        private async Task OverrideAsync(string path, object value, CancellationToken ct)
        {
            PropertyNode property = _device.Schema.GetProperty(_prefix + path);
            _saved.Add((property, await _device.ReadAsync(property, ct).ConfigureAwait(false)));
            await _device.WriteAsync(property, HoverboardPreset.ToEndpointValue(property.Type, value), ct).ConfigureAwait(false);
        }

        private Task WriteAsync(string path, object value, CancellationToken ct)
        {
            PropertyNode property = _device.Schema.GetProperty(_prefix + path);
            return _device.WriteAsync(property, HoverboardPreset.ToEndpointValue(property.Type, value), ct);
        }

        public Task<T> ReadAsync<T>(string path, CancellationToken ct) =>
            _device.ReadAsync<T>(_device.Schema.GetProperty(_prefix + path), ct);
    }
}
