using System.Globalization;
using ODrive.Model;
using ODrive.Protocol;

namespace ODriveStudio.App.ViewModels;

/// <summary>Affichage et saisie des valeurs d'endpoints (explorateur, tableau de bord, journal).</summary>
public static class ValueText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Format(object? value, EnumMetadata? enumType)
    {
        switch (value)
        {
            case null:
                return "";
            case bool b:
                return b ? "true" : "false";
            case float f:
                return float.IsNaN(f) ? "nan"
                    : float.IsPositiveInfinity(f) ? "inf"
                    : float.IsNegativeInfinity(f) ? "-inf"
                    : f.ToString("G6", Inv);
            case EndpointRef reference:
                return $"endpoint {reference.EndpointId}";
        }

        if (enumType is not null && value is byte or ushort or uint or ulong)
        {
            ulong raw = Convert.ToUInt64(value, Inv);
            if (enumType.IsFlags)
            {
                return raw == 0
                    ? "0"
                    : $"0x{raw:X} ({string.Join(" | ", enumType.Decode(raw).Select(v => v.Name))})";
            }
            return enumType.Decode(raw).FirstOrDefault() is { } named ? $"{named.Name} ({raw})" : raw.ToString(Inv);
        }

        return value is IFormattable formattable ? formattable.ToString(null, Inv) : value.ToString() ?? "";
    }

    /// <summary>Convertit une saisie vers le type .NET de l'endpoint. Lève <see cref="FormatException"/> avec un message affichable.</summary>
    public static object Parse(string text, EndpointValueType type, EnumMetadata? enumType)
    {
        ArgumentNullException.ThrowIfNull(text);
        string input = text.Trim();
        if (input.Length == 0)
        {
            throw new FormatException("Saisir une valeur.");
        }

        if (enumType is not null && TryParseEnum(input, enumType, out ulong enumValue))
        {
            input = enumValue.ToString(Inv);
        }
        else if (enumType is not null && !enumType.IsFlags && !IsNumber(input))
        {
            throw new FormatException(
                $"« {text.Trim()} » n'est pas une valeur de {enumType.Prefix} "
                + $"({string.Join(", ", enumType.Values.Select(v => v.Name))}).");
        }

        try
        {
            return type switch
            {
                EndpointValueType.Bool => ParseBool(input),
                EndpointValueType.Float => ParseFloat(input),
                EndpointValueType.Int8 => checked((sbyte)ParseSigned(input)),
                EndpointValueType.Int16 => checked((short)ParseSigned(input)),
                EndpointValueType.Int32 => checked((int)ParseSigned(input)),
                EndpointValueType.Int64 => ParseSigned(input),
                EndpointValueType.UInt8 => checked((byte)ParseUnsigned(input)),
                EndpointValueType.UInt16 => checked((ushort)ParseUnsigned(input)),
                EndpointValueType.UInt32 => checked((uint)ParseUnsigned(input)),
                EndpointValueType.UInt64 => ParseUnsigned(input),
                _ => throw new FormatException("Ce type de valeur ne se saisit pas à la main."),
            };
        }
        catch (OverflowException)
        {
            throw new FormatException($"« {text.Trim()} » dépasse la plage du type {TypeName(type)}.");
        }
    }

    /// <summary>Nom du type tel qu'il apparaît dans le JSON de la carte.</summary>
    public static string TypeName(EndpointValueType type) => type switch
    {
        EndpointValueType.Float => "float",
        EndpointValueType.Bool => "bool",
        EndpointValueType.EndpointRef => "endpoint_ref",
        _ => type.ToString().ToLowerInvariant(),
    };

    private static bool IsNumber(string input) =>
        input.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
        || long.TryParse(input, NumberStyles.Integer, Inv, out _)
        || ulong.TryParse(input, NumberStyles.Integer, Inv, out _);

    private static bool TryParseEnum(string input, EnumMetadata enumType, out ulong value)
    {
        // Forme produite par Format : « NOM (8) ».
        int paren = input.IndexOf('(', StringComparison.Ordinal);
        string name = (paren > 0 ? input[..paren] : input).Trim();
        string prefix = enumType.Prefix + "_";
        if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            name = name[prefix.Length..];
        }

        EnumValueMetadata? match = enumType.Values.FirstOrDefault(
            v => string.Equals(v.Name, name, StringComparison.OrdinalIgnoreCase));
        value = match?.Value ?? 0;
        return match is not null;
    }

    private static bool ParseBool(string input) => input.ToLowerInvariant() switch
    {
        "true" or "1" or "vrai" or "oui" => true,
        "false" or "0" or "faux" or "non" => false,
        _ => throw new FormatException($"« {input} » n'est pas un booléen (true / false)."),
    };

    private static float ParseFloat(string input)
    {
        switch (input.ToLowerInvariant())
        {
            case "inf" or "+inf" or "infinity": return float.PositiveInfinity;
            case "-inf" or "-infinity": return float.NegativeInfinity;
            case "nan": return float.NaN;
        }

        // La virgule décimale est acceptée ; pas de séparateur de milliers.
        string normalized = input.Replace(',', '.');
        return float.TryParse(normalized, NumberStyles.Float, Inv, out float value)
            ? value
            : throw new FormatException($"« {input} » n'est pas un nombre.");
    }

    private static long ParseSigned(string input)
    {
        if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && long.TryParse(input.AsSpan(2), NumberStyles.HexNumber, Inv, out long hex))
        {
            return hex;
        }
        return long.TryParse(input, NumberStyles.Integer, Inv, out long value)
            ? value
            : throw new FormatException($"« {input} » n'est pas un entier.");
    }

    private static ulong ParseUnsigned(string input)
    {
        if (input.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(input.AsSpan(2), NumberStyles.HexNumber, Inv, out ulong hex))
        {
            return hex;
        }
        if (input.StartsWith('-') && long.TryParse(input, NumberStyles.Integer, Inv, out _))
        {
            throw new OverflowException();
        }
        return ulong.TryParse(input, NumberStyles.Integer, Inv, out ulong value)
            ? value
            : throw new FormatException($"« {input} » n'est pas un entier.");
    }
}
