using System.Globalization;
using ODrive.Model;
using ODrive.Protocol;
using ODrive.Tuning.Profiles;

namespace ODrive.Tuning.Commissioning;

/// <summary>Une valeur changée par la copie d'un axe vers l'autre.</summary>
public sealed record AxisCopyChange(string Path, string OldValue, string NewValue);

/// <summary>
/// Copie la configuration d'un axe sur l'autre (deux moteurs identiques). Ne copie pas ce qui appartient à un
/// moteur précis (mesures et repères de calibration : l'axe d'arrivée doit faire la sienne) ni ce qui
/// appartient à un axe (broches, identifiant CAN). Rien n'est sauvegardé en flash.
/// </summary>
public static class AxisConfigCopy
{
    // Résultats de calibration : motor.cpp:438 et :478 (R, L), encoder.cpp (offset, sens, polarité Hall).
    private static readonly string[] MotorSpecific =
    [
        "motor.config.phase_resistance", "motor.config.phase_inductance",
        "encoder.config.phase_offset", "encoder.config.phase_offset_float", "encoder.config.direction",
        "encoder.config.index_offset", "encoder.config.hall_polarity", "encoder.config.hall_polarity_calibrated",
    ];

    /// <summary>Vrai si la propriété (chemin sans le préfixe <c>axisN.</c>) se copie d'un axe à l'autre.</summary>
    public static bool IsCopied(string axisRelativePath)
    {
        ArgumentNullException.ThrowIfNull(axisRelativePath);
        return !MotorSpecific.Contains(axisRelativePath, StringComparer.Ordinal)
            && !axisRelativePath.Contains("pre_calibrated", StringComparison.Ordinal)
            && !axisRelativePath.Contains("gpio", StringComparison.Ordinal)
            && !axisRelativePath.EndsWith("can.node_id", StringComparison.Ordinal)
            // Numéro de l'axe dont le capteur sert à la régulation : chaque axe désigne le sien par défaut.
            && !axisRelativePath.EndsWith("load_encoder_axis", StringComparison.Ordinal);
    }

    /// <exception cref="InvalidOperationException">Un axe n'est pas en IDLE.</exception>
    public static async Task<IReadOnlyList<AxisCopyChange>> CopyAsync(ODriveDevice device, int from, int to,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (from is not (0 or 1) || to is not (0 or 1) || from == to)
        {
            throw new ArgumentException("Les axes de départ et d'arrivée doivent être 0 et 1, et différents.");
        }

        SafetyDecision decision = await SafetyGuard.AuthorizeConfigurationChangeAsync(device, ct).ConfigureAwait(false);
        if (!decision.IsAllowed)
        {
            throw new InvalidOperationException(decision.Reason ?? "Un axe n'est pas en IDLE.");
        }

        string source = string.Create(CultureInfo.InvariantCulture, $"axis{from}.");
        string target = string.Create(CultureInfo.InvariantCulture, $"axis{to}.");
        var changes = new List<AxisCopyChange>();
        foreach (PropertyNode property in device.Schema.Properties.Where(ConfigProfile.IsConfigurationProperty))
        {
            if (!property.Path.StartsWith(source, StringComparison.Ordinal)
                || !IsCopied(property.Path[source.Length..])
                || device.Schema.FindProperty(target + property.Path[source.Length..]) is not { } destination)
            {
                continue;
            }
            await WriteIfDifferentAsync(device, destination, await device.ReadAsync(property, ct).ConfigureAwait(false),
                changes, ct).ConfigureAwait(false);
        }

        // Capteurs Hall : leurs entrées dépendent de l'axe (Board/v3/board.cpp:204-224 : GPIO9-11 pour
        // l'axe 0, GPIO12-14 pour l'axe 1) et doivent être en entrée numérique.
        PropertyNode mode = device.Schema.GetProperty(target + "encoder.config.mode");
        if ((EncoderMode)await device.ReadAsync<byte>(mode, ct).ConfigureAwait(false) == EncoderMode.Hall)
        {
            int first = to == 0 ? 9 : 12;
            for (int i = 0; i < 3; i++)
            {
                PropertyNode gpio = device.Schema.GetProperty(
                    string.Create(CultureInfo.InvariantCulture, $"config.gpio{first + i}_mode"));
                await WriteIfDifferentAsync(device, gpio, HoverboardPreset.ToEndpointValue(gpio.Type, GpioMode.Digital),
                    changes, ct).ConfigureAwait(false);
            }
        }
        return changes;
    }

    private static async Task WriteIfDifferentAsync(ODriveDevice device, PropertyNode destination, object value,
        List<AxisCopyChange> changes, CancellationToken ct)
    {
        object before = await device.ReadAsync(destination, ct).ConfigureAwait(false);
        if (Equals(before, value))
        {
            return;
        }
        await device.WriteAsync(destination, value, ct).ConfigureAwait(false);
        changes.Add(new AxisCopyChange(destination.Path, ConfigProfile.FormatValue(before), ConfigProfile.FormatValue(value)));
    }
}
