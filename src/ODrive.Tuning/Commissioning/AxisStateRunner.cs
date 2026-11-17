using System.Globalization;
using ODrive.Model;
using ODrive.Protocol;

namespace ODrive.Tuning.Commissioning;

/// <summary>Résultat d'une calibration ou d'une séquence d'états lancée sur un axe.</summary>
/// <param name="Success">Vrai si l'axe est revenu en IDLE sans erreur.</param>
/// <param name="Message">Compte rendu en français, affichable tel quel.</param>
/// <param name="Errors">Erreurs relevées sur la carte à la fin.</param>
public sealed record AxisRunResult(bool Success, string Message, IReadOnlyList<ActiveError> Errors);

/// <summary>Attente injectable : les tests la remplacent par une avance de l'horloge simulée.</summary>
public delegate Task DelayAsync(TimeSpan duration, CancellationToken ct);

/// <summary>
/// Lance un état d'axe qui se termine tout seul (calibrations) et attend son retour en IDLE. Passe par
/// <see cref="SafetyGuard"/> : ces états alimentent le moteur et peuvent le faire tourner.
/// </summary>
public static class AxisStateRunner
{
    private static readonly TimeSpan PollPeriod = TimeSpan.FromMilliseconds(100);

    public static async Task<AxisRunResult> RunAsync(ODriveDevice device, int axis, AxisState state, TimeSpan timeout,
        SafetyGuard guard, Func<string, bool> confirm, IProgress<AxisState>? progress = null,
        DelayAsync? delay = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(guard);
        delay ??= Task.Delay;
        string prefix = string.Create(CultureInfo.InvariantCulture, $"axis{axis}.");
        PropertyNode requested = device.Schema.GetProperty(prefix + "requested_state");
        PropertyNode current = device.Schema.GetProperty(prefix + "current_state");

        SafetyDecision decision = await guard.AuthorizeMotionAsync(device, confirm, ct).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            return new AxisRunResult(false, decision.Reason ?? "Action refusée.", []);
        }

        await device.WriteAsync(requested, (byte)state, ct).ConfigureAwait(false);
        TimeSpan elapsed = TimeSpan.Zero;
        try
        {
            while (true)
            {
                await delay(PollPeriod, ct).ConfigureAwait(false);
                elapsed += PollPeriod;
                var now = (AxisState)await device.ReadAsync<byte>(current, ct).ConfigureAwait(false);
                progress?.Report(now);
                if (now == AxisState.Idle)
                {
                    break;
                }
                if (elapsed >= timeout)
                {
                    await device.WriteAsync(requested, (byte)AxisState.Idle, RequestPriority.Emergency, CancellationToken.None)
                        .ConfigureAwait(false);
                    return new AxisRunResult(false,
                        string.Create(CultureInfo.InvariantCulture,
                            $"Pas terminé au bout de {timeout.TotalSeconds:F0} s : l'axe a été remis en IDLE."),
                        await ErrorDecoder.ReadAllAsync(device, CancellationToken.None).ConfigureAwait(false));
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Annulation par l'utilisateur : ne jamais laisser le moteur alimenté.
            await device.WriteAsync(requested, (byte)AxisState.Idle, RequestPriority.Emergency, CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        IReadOnlyList<ActiveError> errors = await ErrorDecoder.ReadAllAsync(device, ct).ConfigureAwait(false);
        return errors.Count == 0
            ? new AxisRunResult(true, "Terminé sans erreur.", errors)
            : new AxisRunResult(false, "Échec : " + string.Join(", ", errors.Select(e => $"{e.Name} ({e.Source})")), errors);
    }
}

/// <summary>Contrôles de plausibilité des mesures de calibration (SPEC §4, étape 4).</summary>
public static class CalibrationChecks
{
    /// <summary>
    /// Avis sur la résistance et l'inductance mesurées pour une roue d'hoverboard (guide : ≈ 0,18 Ω et
    /// ≈ 0,34 mH), ou <c>null</c> si elles sont plausibles. Bornes larges : elles servent à repérer un moteur
    /// mal branché, pas à juger un moteur précis.
    /// </summary>
    public static string? HoverboardMotor(float phaseResistance, float phaseInductance)
    {
        if (phaseResistance is < 0.05f or > 0.6f)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"Résistance de phase inhabituelle pour une roue d'hoverboard : {phaseResistance:F3} Ω (attendu vers 0,18 Ω). Vérifier le câblage des trois phases.");
        }
        if (phaseInductance is < 0.0001f or > 0.001f)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"Inductance de phase inhabituelle pour une roue d'hoverboard : {phaseInductance * 1000:F3} mH (attendu vers 0,34 mH).");
        }
        return null;
    }

    /// <summary>
    /// Avis sur l'offset de phase d'un capteur Hall, ou <c>null</c> s'il est plausible : il doit valoir 0,5
    /// modulo 1 (−1,5 ; −0,5 ; 0,5 ; 1,5…, docs/HOVERBOARD.md).
    /// </summary>
    public static string? HallPhaseOffset(float phaseOffsetFloat)
    {
        double fraction = phaseOffsetFloat - Math.Floor(phaseOffsetFloat);
        return Math.Abs(fraction - 0.5) <= 0.1
            ? null
            : string.Create(CultureInfo.InvariantCulture,
                $"Offset de phase inattendu : {phaseOffsetFloat:F3} (attendu 0,5 modulo 1). Bruit sur les capteurs Hall ? Vérifier les condensateurs de 22 nF.");
    }
}
