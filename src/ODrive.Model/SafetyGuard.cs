using System.Globalization;
using ODrive.Protocol;

namespace ODrive.Model;

/// <summary>Résultat d'un contrôle de sécurité ; <see cref="Reason"/> explique un refus (message utilisateur).</summary>
public sealed record SafetyDecision(bool IsAllowed, string? Reason)
{
    public static SafetyDecision Allowed { get; } = new(true, null);
    public static SafetyDecision Refused(string reason) => new(false, reason);
}

/// <summary>
/// Passage obligé de toute action qui peut faire tourner un moteur (docs/SAFETY.md). Phase 2 : contrôle de
/// l'état de la carte et confirmation unique par session. Les limites temporaires et la surveillance pendant
/// l'exécution s'ajoutent avec les tests de la phase 5.
/// </summary>
public sealed class SafetyGuard
{
    public const string MotionQuestion =
        "Le moteur va être alimenté et peut tourner.\n\n"
        + "La roue est-elle soulevée ou libre de tourner, et personne n'est à portée ?";

    /// <summary>Vrai une fois que l'utilisateur a confirmé, pour le reste de la session.</summary>
    public bool MotionConfirmed { get; private set; }

    /// <summary>
    /// Vrai si écrire <paramref name="value"/> dans <paramref name="path"/> peut mettre un moteur en mouvement :
    /// toute demande d'état autre que UNDEFINED et IDLE (calibrations, boucle fermée…).
    /// </summary>
    public static bool IsMotionRequest(string path, object value)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!path.EndsWith(".requested_state", StringComparison.Ordinal))
        {
            return false;
        }
        var state = (AxisState)Convert.ToByte(value, CultureInfo.InvariantCulture);
        return state is not (AxisState.Undefined or AxisState.Idle);
    }

    /// <summary>
    /// Autorise une mise en mouvement : la carte ne doit avoir aucune erreur, Vbus doit être dans la plage de
    /// la carte, et l'utilisateur doit avoir confirmé (une fois par session).
    /// </summary>
    public async Task<SafetyDecision> AuthorizeMotionAsync(ODriveDevice device, Func<string, bool> confirm,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(confirm);

        IReadOnlyList<ActiveError> errors = await ErrorDecoder.ReadAllAsync(device, ct).ConfigureAwait(false);
        if (errors.Count > 0)
        {
            return SafetyDecision.Refused("La carte signale des erreurs : "
                + string.Join(", ", errors.Select(e => $"{e.Name} ({e.Source})"))
                + ".\nCorriger la cause puis effacer les erreurs avant d'alimenter un moteur.");
        }

        var odrv = new ODrive3(device);
        float vbus = await odrv.VbusVoltage.GetAsync(ct).ConfigureAwait(false);
        float low = await odrv.Config.DcBusUndervoltageTripLevel.GetAsync(ct).ConfigureAwait(false);
        float high = await odrv.Config.DcBusOvervoltageTripLevel.GetAsync(ct).ConfigureAwait(false);
        if (!(vbus > low && vbus < high))
        {
            return SafetyDecision.Refused(string.Create(CultureInfo.InvariantCulture,
                $"Vbus = {vbus:F1} V est hors de la plage de la carte ({low:F1} V à {high:F1} V)."));
        }

        if (!MotionConfirmed)
        {
            if (!confirm(MotionQuestion))
            {
                return SafetyDecision.Refused("Mise en mouvement annulée.");
            }
            MotionConfirmed = true;
        }
        return SafetyDecision.Allowed;
    }

    /// <summary>Save / Erase / Reboot : jamais pendant qu'un axe est actif (docs/SAFETY.md).</summary>
    public static async Task<SafetyDecision> AuthorizeConfigurationChangeAsync(ODriveDevice device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        EnumMetadata states = ODriveEnums.Get<AxisState>();
        foreach (ObjectNode axis in device.Schema.Root.Members.OfType<ObjectNode>())
        {
            if (device.Schema.FindProperty(axis.Path + ".current_state") is not { } property)
            {
                continue;
            }

            var state = await device.ReadAsync<AxisState>(property, ct).ConfigureAwait(false);
            if (state is not (AxisState.Idle or AxisState.Undefined))
            {
                string name = states.Decode((ulong)state).FirstOrDefault()?.Name ?? state.ToString();
                return SafetyDecision.Refused(
                    $"{axis.Path} est dans l'état {name}. Repasser tous les axes en IDLE avant cette opération.");
            }
        }
        return SafetyDecision.Allowed;
    }
}
