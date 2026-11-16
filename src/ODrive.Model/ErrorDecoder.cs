using System.Globalization;
using ODrive.Protocol;

namespace ODrive.Model;

/// <summary>Un flag d'erreur actif sur une carte.</summary>
/// <param name="Path">Propriété d'erreur, ex. <c>axis0.motor.error</c>.</param>
/// <param name="Source">Module concerné, ex. <c>axis0.motor</c> (« carte » pour l'erreur système).</param>
/// <param name="Name">Nom tel qu'affiché par odrivetool, ex. <c>MOTOR_ERROR_PHASE_RESISTANCE_OUT_OF_RANGE</c>.</param>
/// <param name="Description">Description issue de <c>odrive-interface.yaml</c> (en anglais), si elle existe.</param>
public sealed record ActiveError(string Path, string Source, string Name, ulong Bit, string? Description);

/// <summary>
/// Décodage des erreurs en clair à partir des flags générés (même principe que <c>dump_errors</c>,
/// <c>ref/ODrive/tools/odrive/utils.py:87-127</c>).
/// </summary>
public static class ErrorDecoder
{
    /// <summary>Propriétés d'erreur (champs de bits) que la carte expose réellement.</summary>
    public static IReadOnlyList<PropertyMetadata> ErrorProperties(EndpointSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return ODriveInterface.Properties
            .Where(p => (p.Path == "error" || p.Path.EndsWith(".error", StringComparison.Ordinal))
                        && p.EnumType is not null && ODriveEnums.Find(p.EnumType) is { IsFlags: true }
                        && schema.FindProperty(p.Path) is not null)
            .ToList();
    }

    public static IReadOnlyList<ActiveError> Decode(string path, ulong raw)
    {
        PropertyMetadata property = ODriveInterface.FindProperty(path)
            ?? throw new KeyNotFoundException($"Propriété inconnue du firmware 0.5.6 : {path}");
        EnumMetadata flags = (property.EnumType is null ? null : ODriveEnums.Find(property.EnumType))
            ?? throw new ArgumentException($"{path} n'est pas un champ d'erreurs.", nameof(path));

        int dot = path.LastIndexOf('.');
        string source = dot < 0 ? "carte" : path[..dot];
        var errors = new List<ActiveError>();
        ulong remaining = raw;
        foreach (EnumValueMetadata flag in flags.Decode(raw))
        {
            errors.Add(new ActiveError(path, source, flags.DisplayName(flag), flag.Value, flag.Doc ?? flag.Brief));
            remaining &= ~flag.Value;
        }

        // Bits sans nom dans le YAML : affichés tels quels plutôt que passés sous silence.
        for (int bit = 0; bit < 64; bit++)
        {
            ulong mask = 1ul << bit;
            if ((remaining & mask) != 0)
            {
                errors.Add(new ActiveError(path, source,
                    $"{flags.Prefix}_0x{mask.ToString("X", CultureInfo.InvariantCulture)}", mask, null));
            }
        }
        return errors;
    }

    /// <summary>Lit toutes les propriétés d'erreur de la carte et renvoie les flags actifs.</summary>
    public static async Task<IReadOnlyList<ActiveError>> ReadAllAsync(ODriveDevice device, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var errors = new List<ActiveError>();
        foreach (PropertyMetadata property in ErrorProperties(device.Schema))
        {
            object value = await device.ReadAsync(device.Schema.GetProperty(property.Path), ct).ConfigureAwait(false);
            ulong raw = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
            if (raw != 0)
            {
                errors.AddRange(Decode(property.Path, raw));
            }
        }
        return errors;
    }
}
