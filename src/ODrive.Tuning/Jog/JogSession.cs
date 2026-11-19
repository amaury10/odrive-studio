using System.Globalization;
using ODrive.Model;
using ODrive.Protocol;
using ODrive.Tuning.Commissioning;

namespace ODrive.Tuning.Jog;

/// <summary>Bornes d'un essai manuel du moteur.</summary>
/// <param name="MaxVelocity">Vitesse maximale commandable, en tours/s.</param>
/// <param name="MaxCurrent">Limite de courant moteur pendant l'essai, en ampères.</param>
/// <param name="RampRate">Accélération de la consigne, en tours/s².</param>
public sealed record JogLimits(float MaxVelocity = 2f, float MaxCurrent = 10f, float RampRate = 2f)
{
    /// <summary>Température des transistors au-delà de laquelle l'essai est abandonné, en °C.</summary>
    public float MaxFetTemperature { get; init; } = 80f;

    /// <summary>Délai du watchdog de l'axe pendant l'essai, en secondes.</summary>
    public float WatchdogTimeout { get; init; } = 1f;

    /// <summary>
    /// <c>vel_limit</c> écrit dans la carte pendant l'essai, en tours/s, et seuil d'abandon de la surveillance.
    /// Plus large que <see cref="MaxVelocity"/> : le firmware coupe en OVERSPEED dès que l'estimation dépasse
    /// <c>vel_limit_tolerance</c> (1,2) × <c>vel_limit</c> (controller.cpp:337), et l'estimation tirée de
    /// capteurs Hall est bruitée. La consigne, elle, reste bornée à <see cref="MaxVelocity"/> par l'outil.
    /// </summary>
    public float FirmwareVelocityLimit => MaxVelocity * 1.5f + 0.5f;
}

/// <summary>Mesures relevées pendant l'essai.</summary>
/// <param name="Velocity">Vitesse estimée, en tours/s.</param>
/// <param name="Current">Courant Iq mesuré, en ampères.</param>
/// <param name="BusVoltage">Tension du bus, en volts.</param>
/// <param name="FetTemperature">Température des transistors, en °C.</param>
public sealed record JogReading(float Velocity, float Current, float BusVoltage, float FetTemperature);

/// <summary>
/// Essai manuel (« jog ») d'un axe en vitesse, selon docs/SAFETY.md :
/// contrôle de la carte et confirmation (<see cref="SafetyGuard"/>), limites temporaires de vitesse et de
/// courant restaurées à la fin quoi qu'il arrive, surveillance continue (état, vitesse, courant, Vbus,
/// température) avec abandon au premier dépassement, et watchdog de l'axe armé et nourri par l'outil : si la
/// liaison USB tombe, la carte arrête le moteur toute seule.
/// </summary>
public sealed class JogSession : IAsyncDisposable
{
    private static readonly TimeSpan MonitorPeriod = TimeSpan.FromMilliseconds(100);

    private readonly ODriveDevice _device;
    private readonly string _prefix;
    private readonly JogLimits _limits;
    private readonly DelayAsync _delay;
    private readonly List<(PropertyNode Property, object Value)> _saved = [];
    private readonly CancellationTokenSource _stop = new();
    private Task _monitor = Task.CompletedTask;
    private bool _ended;

    private JogSession(ODriveDevice device, int axis, JogLimits limits, DelayAsync delay)
    {
        _device = device;
        _prefix = string.Create(CultureInfo.InvariantCulture, $"axis{axis}.");
        _limits = limits;
        _delay = delay;
    }

    /// <summary>Levé une fois, quand l'essai se termine ; l'argument dit pourquoi, en français.</summary>
    public event EventHandler<string>? Ended;

    /// <summary>Levé à chaque relevé de surveillance (toutes les 100 ms environ).</summary>
    public event EventHandler<JogReading>? ReadingAvailable;

    /// <summary>Vrai tant que le moteur est asservi par cet essai.</summary>
    public bool IsRunning => !_ended;

    /// <summary>
    /// Démarre l'essai : l'axe passe en boucle fermée, consigne de vitesse nulle.
    /// </summary>
    /// <returns>La session, ou <c>null</c> avec la raison du refus.</returns>
    public static async Task<(JogSession? Session, string? Refusal)> StartAsync(ODriveDevice device, int axis,
        JogLimits limits, SafetyGuard guard, Func<string, bool> confirm, DelayAsync? delay = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(limits);
        ArgumentNullException.ThrowIfNull(guard);
        if (limits.MaxVelocity <= 0 || limits.MaxCurrent <= 0 || limits.RampRate <= 0)
        {
            return (null, "Les limites de vitesse, de courant et de rampe doivent être positives.");
        }

        SafetyDecision decision = await guard.AuthorizeMotionAsync(device, confirm, ct).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return (null, decision.Reason ?? "Action refusée.");
        }

        var session = new JogSession(device, axis, limits, delay ?? Task.Delay);
        try
        {
            // Limites temporaires : tout ce qui est écrit ici est restauré par EndAsync.
            await session.OverrideAsync("controller.config.control_mode", (byte)ControlMode.VelocityControl, ct).ConfigureAwait(false);
            await session.OverrideAsync("controller.config.input_mode", (byte)InputMode.VelRamp, ct).ConfigureAwait(false);
            await session.OverrideAsync("controller.config.vel_ramp_rate", limits.RampRate, ct).ConfigureAwait(false);
            await session.OverrideAsync("controller.config.vel_limit", limits.FirmwareVelocityLimit, ct).ConfigureAwait(false);
            await session.OverrideAsync("controller.config.enable_vel_limit", true, ct).ConfigureAwait(false);
            await session.OverrideAsync("motor.config.current_lim", limits.MaxCurrent, ct).ConfigureAwait(false);
            await session.OverrideAsync("config.watchdog_timeout", limits.WatchdogTimeout, ct).ConfigureAwait(false);
            await session.WriteAsync("controller.input_vel", 0f, ct).ConfigureAwait(false);
            await session.FeedWatchdogAsync(ct).ConfigureAwait(false);
            await session.OverrideAsync("config.enable_watchdog", true, ct).ConfigureAwait(false);

            await session.WriteAsync("requested_state", (byte)AxisState.ClosedLoopControl, ct).ConfigureAwait(false);
            await session._delay(MonitorPeriod, ct).ConfigureAwait(false);
            if (await session.ReadStateAsync(ct).ConfigureAwait(false) != AxisState.ClosedLoopControl)
            {
                string errors = await session.DescribeErrorsAsync().ConfigureAwait(false);
                await session.EndAsync("refus").ConfigureAwait(false);
                return (null, "L'axe n'est pas passé en boucle fermée" + (errors.Length > 0 ? " : " + errors : ".")
                    + " Le moteur et le capteur sont-ils calibrés ?");
            }
        }
        catch
        {
            await session.EndAsync("erreur au démarrage").ConfigureAwait(false);
            throw;
        }

        session._monitor = session.MonitorAsync();
        return (session, null);
    }

    /// <summary>Consigne de vitesse, en tours/s, bornée à <see cref="JogLimits.MaxVelocity"/>.</summary>
    public async Task SetVelocityAsync(float turnsPerSecond, CancellationToken ct = default)
    {
        if (_ended)
        {
            return;
        }
        float clamped = Math.Clamp(float.IsFinite(turnsPerSecond) ? turnsPerSecond : 0f, -_limits.MaxVelocity, _limits.MaxVelocity);
        await WriteAsync("controller.input_vel", clamped, ct).ConfigureAwait(false);
    }

    /// <summary>Termine l'essai : consigne nulle, axe en IDLE, réglages d'origine restaurés.</summary>
    public async ValueTask DisposeAsync()
    {
        await EndAsync("Essai terminé.").ConfigureAwait(false);
        try
        {
            await _monitor.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        _stop.Dispose();
    }

    private async Task MonitorAsync()
    {
        CancellationToken ct = _stop.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await FeedWatchdogAsync(ct).ConfigureAwait(false);
                AxisState state = await ReadStateAsync(ct).ConfigureAwait(false);
                var reading = new JogReading(
                    await ReadAsync<float>("encoder.vel_estimate", ct).ConfigureAwait(false),
                    await ReadAsync<float>("motor.current_control.Iq_measured", ct).ConfigureAwait(false),
                    await _device.ReadAsync<float>(_device.Schema.GetProperty("vbus_voltage"), ct).ConfigureAwait(false),
                    await ReadAsync<float>("motor.fet_thermistor.temperature", ct).ConfigureAwait(false));

                if (Violation(state, reading) is { } reason)
                {
                    if (state != AxisState.ClosedLoopControl)
                    {
                        string errors = await DescribeErrorsAsync().ConfigureAwait(false);
                        reason += errors.Length > 0 ? " : " + errors : ".";
                    }
                    await EndAsync("Essai abandonné — " + reason).ConfigureAwait(false);
                    return;
                }
                ReadingAvailable?.Invoke(this, reading);
                await _delay(MonitorPeriod, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IOException or ODriveProtocolException)
        {
            // Liaison perdue : l'outil ne peut plus rien, c'est le watchdog de la carte qui arrête le moteur.
            _ended = true;
            Ended?.Invoke(this, "Essai interrompu — liaison avec la carte perdue. Le watchdog de l'axe arrête le moteur.");
        }
    }

    private string? Violation(AxisState state, JogReading reading)
    {
        if (state != AxisState.ClosedLoopControl)
        {
            return "l'axe a quitté la boucle fermée";
        }
        if (Math.Abs(reading.Velocity) > _limits.FirmwareVelocityLimit)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"vitesse de {reading.Velocity:F2} tours/s au-delà de la limite de {_limits.MaxVelocity:F2} tours/s.");
        }
        if (Math.Abs(reading.Current) > _limits.MaxCurrent * 1.2f)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"courant de {reading.Current:F1} A au-delà de la limite de {_limits.MaxCurrent:F1} A.");
        }
        if (reading.FetTemperature > _limits.MaxFetTemperature)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"transistors à {reading.FetTemperature:F0} °C (limite {_limits.MaxFetTemperature:F0} °C).");
        }
        return null;
    }

    // Arrêt puis restauration, dans cet ordre, chaque étape étant tentée même si la précédente échoue.
    private async Task EndAsync(string reason)
    {
        if (_ended)
        {
            return;
        }
        _ended = true;
        await _stop.CancelAsync().ConfigureAwait(false);

        await TryAsync(() => _device.WriteAsync(_device.Schema.GetProperty(_prefix + "requested_state"),
            (byte)AxisState.Idle, RequestPriority.Emergency, CancellationToken.None)).ConfigureAwait(false);
        await TryAsync(() => WriteAsync("controller.input_vel", 0f, CancellationToken.None)).ConfigureAwait(false);
        for (int i = _saved.Count - 1; i >= 0; i--)
        {
            (PropertyNode property, object value) = _saved[i];
            await TryAsync(() => _device.WriteAsync(property, value, CancellationToken.None)).ConfigureAwait(false);
        }
        Ended?.Invoke(this, reason);
    }

    private static async Task TryAsync(Func<Task> action)
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ODriveProtocolException)
        {
            // Liaison perdue : rien d'autre à faire, le watchdog de la carte prend le relais.
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

    private Task<T> ReadAsync<T>(string path, CancellationToken ct) =>
        _device.ReadAsync<T>(_device.Schema.GetProperty(_prefix + path), ct);

    private async Task<AxisState> ReadStateAsync(CancellationToken ct) =>
        (AxisState)await ReadAsync<byte>("current_state", ct).ConfigureAwait(false);

    // Seul watchdog_feed() nourrit le watchdog en USB natif (docs/SAFETY.md, axis.cpp:166).
    private async Task FeedWatchdogAsync(CancellationToken ct) =>
        await _device.CallAsync(_prefix + "watchdog_feed", [], ct).ConfigureAwait(false);

    private async Task<string> DescribeErrorsAsync()
    {
        try
        {
            IReadOnlyList<ActiveError> errors = await ErrorDecoder.ReadAllAsync(_device, CancellationToken.None).ConfigureAwait(false);
            return string.Join(", ", errors.Select(e => $"{e.Name} ({e.Source})"));
        }
        catch (Exception e) when (e is IOException or ODriveProtocolException)
        {
            return "";
        }
    }
}
