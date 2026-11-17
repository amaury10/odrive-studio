using System.Globalization;
using System.Text.Json;
using ODrive.Protocol;

namespace ODrive.Tuning.Profiles;

/// <summary>Écart entre un profil et une carte (ou un autre profil), pour une propriété.</summary>
/// <param name="ProfileValue">Valeur du profil, ou <c>null</c> si le profil ne la contient pas.</param>
/// <param name="DeviceValue">Valeur de la carte, ou <c>null</c> si la carte n'a pas cette propriété.</param>
public sealed record ProfileDifference(string Path, string? ProfileValue, string? DeviceValue)
{
    /// <summary>Vrai si la valeur du profil peut être écrite dans la carte.</summary>
    public bool CanApply => ProfileValue is not null && DeviceValue is not null;
}

/// <summary>Écriture d'un profil refusée ou en échec pour une propriété.</summary>
public sealed record ProfileApplyFailure(string Path, string Message);

/// <summary>
/// Profil de configuration (SPEC §9) : toutes les propriétés persistantes modifiables (<c>*.config.*</c>) de
/// la carte et de ses axes, avec la version du firmware et le CRC de l'arbre pour détecter un écart.
/// Les valeurs sont gardées en texte invariant, sans perte (<c>Infinity</c> compris), et retypées d'après
/// l'arbre de la carte au moment de comparer ou d'écrire.
/// </summary>
public sealed class ConfigProfile
{
    public const string FormatId = "odrive-studio-profile/1";

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public ConfigProfile(string firmware, string hardware, ushort jsonCrc, string note, DateTimeOffset created,
        IReadOnlyDictionary<string, string> values)
    {
        Firmware = firmware;
        Hardware = hardware;
        JsonCrc = jsonCrc;
        Note = note;
        Created = created;
        Values = values;
    }

    public string Firmware { get; }
    public string Hardware { get; }

    /// <summary>CRC de l'arbre d'endpoints de la carte d'origine.</summary>
    public ushort JsonCrc { get; }

    public string Note { get; }
    public DateTimeOffset Created { get; }

    /// <summary>Chemin → valeur en texte invariant.</summary>
    public IReadOnlyDictionary<string, string> Values { get; }

    /// <summary>
    /// Vrai pour une propriété de configuration qu'un profil sait porter : modifiable, sous un objet
    /// <c>config</c>, et pas une référence d'endpoint (elle dépend de l'arbre de la carte).
    /// </summary>
    public static bool IsConfigurationProperty(PropertyNode property)
    {
        ArgumentNullException.ThrowIfNull(property);
        return property.IsWritable
            && property.Type != EndpointValueType.EndpointRef
            && (property.Path.StartsWith("config.", StringComparison.Ordinal)
                || property.Path.Contains(".config.", StringComparison.Ordinal));
    }

    /// <summary>Lit toute la configuration de la carte.</summary>
    public static async Task<ConfigProfile> CaptureAsync(ODriveDevice device, string note = "",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (PropertyNode property in device.Schema.Properties.Where(IsConfigurationProperty))
        {
            values[property.Path] = FormatValue(await device.ReadAsync(property, ct).ConfigureAwait(false));
        }

        string firmware = string.Join('.', await ReadVersionAsync(device, ct,
            "fw_version_major", "fw_version_minor", "fw_version_revision").ConfigureAwait(false));
        string[] hw = await ReadVersionAsync(device, ct, "hw_version_major", "hw_version_minor", "hw_version_variant")
            .ConfigureAwait(false);
        return new ConfigProfile(firmware, $"v{hw[0]}.{hw[1]}-{hw[2]}V", device.Schema.JsonCrc, note,
            DateTimeOffset.Now, values);
    }

    public void Save(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var document = new Dictionary<string, object>
        {
            ["format"] = FormatId,
            ["created"] = Created.ToString("o", Inv),
            ["firmware"] = Firmware,
            ["hardware"] = Hardware,
            ["json_crc"] = JsonCrc,
            ["note"] = Note,
            ["values"] = Values,
        };
        JsonSerializer.Serialize(stream, document, JsonOptions);
    }

    /// <exception cref="InvalidDataException">Le fichier n'est pas un profil ODrive Studio.</exception>
    public static ConfigProfile Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        try
        {
            using JsonDocument document = JsonDocument.Parse(stream);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("format", out JsonElement format) || format.GetString() != FormatId
                || !root.TryGetProperty("values", out JsonElement valuesElement)
                || valuesElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Ce fichier n'est pas un profil ODrive Studio.");
            }

            var values = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty entry in valuesElement.EnumerateObject())
            {
                values[entry.Name] = entry.Value.ValueKind == JsonValueKind.String
                    ? entry.Value.GetString() ?? ""
                    : entry.Value.GetRawText();
            }

            return new ConfigProfile(
                Text(root, "firmware"), Text(root, "hardware"),
                root.TryGetProperty("json_crc", out JsonElement crc) && crc.TryGetUInt16(out ushort value) ? value : (ushort)0,
                Text(root, "note"),
                DateTimeOffset.TryParse(Text(root, "created"), Inv, DateTimeStyles.RoundtripKind, out DateTimeOffset created)
                    ? created
                    : default,
                values);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException("Profil illisible : " + e.Message, e);
        }
    }

    /// <summary>Avertissement si la carte n'a pas le même arbre que celle du profil, sinon <c>null</c>.</summary>
    public string? CompatibilityWarning(ODriveDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.Schema.JsonCrc == JsonCrc
            ? null
            : $"Ce profil vient d'un firmware {Firmware} ({Hardware}) dont l'arbre de paramètres diffère de celui "
              + "de la carte : certaines valeurs peuvent manquer ou avoir changé de sens.";
    }

    /// <summary>Écarts entre ce profil et la carte, triés par chemin.</summary>
    public async Task<IReadOnlyList<ProfileDifference>> CompareToDeviceAsync(ODriveDevice device,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var differences = new List<ProfileDifference>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (PropertyNode property in device.Schema.Properties.Where(IsConfigurationProperty))
        {
            seen.Add(property.Path);
            string deviceValue = FormatValue(await device.ReadAsync(property, ct).ConfigureAwait(false));
            if (!Values.TryGetValue(property.Path, out string? profileValue))
            {
                differences.Add(new ProfileDifference(property.Path, null, deviceValue));
            }
            else if (Normalize(property.Type, profileValue) != deviceValue)
            {
                differences.Add(new ProfileDifference(property.Path, profileValue, deviceValue));
            }
        }
        differences.AddRange(Values.Where(v => !seen.Contains(v.Key)).Select(v => new ProfileDifference(v.Key, v.Value, null)));
        differences.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return differences;
    }

    /// <summary>Écarts entre ce profil et un autre (<see cref="ProfileDifference.DeviceValue"/> = l'autre).</summary>
    public IReadOnlyList<ProfileDifference> CompareTo(ConfigProfile other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var differences = new List<ProfileDifference>();
        foreach (string path in Values.Keys.Union(other.Values.Keys).Order(StringComparer.Ordinal))
        {
            Values.TryGetValue(path, out string? mine);
            other.Values.TryGetValue(path, out string? theirs);
            if (mine != theirs)
            {
                differences.Add(new ProfileDifference(path, mine, theirs));
            }
        }
        return differences;
    }

    /// <summary>
    /// Écrit dans la carte les valeurs de profil des écarts choisis. Une valeur refusée n'arrête pas les
    /// suivantes. Rien n'est sauvegardé en flash : c'est à l'appelant de le proposer.
    /// </summary>
    public static async Task<IReadOnlyList<ProfileApplyFailure>> ApplyAsync(ODriveDevice device,
        IEnumerable<ProfileDifference> selected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(selected);
        var failures = new List<ProfileApplyFailure>();
        foreach (ProfileDifference difference in selected)
        {
            if (difference.ProfileValue is null || device.Schema.FindProperty(difference.Path) is not { } property)
            {
                failures.Add(new ProfileApplyFailure(difference.Path, "absente de la carte ou du profil"));
                continue;
            }
            if (!IsConfigurationProperty(property))
            {
                failures.Add(new ProfileApplyFailure(difference.Path, "ce n'est pas une propriété de configuration modifiable"));
                continue;
            }

            try
            {
                await device.WriteAsync(property, ParseValue(property.Type, difference.ProfileValue), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException)
            {
                failures.Add(new ProfileApplyFailure(difference.Path, $"valeur « {difference.ProfileValue} » invalide"));
            }
        }
        return failures;
    }

    /// <summary>Texte invariant d'une valeur d'endpoint, relisible sans perte.</summary>
    public static string FormatValue(object value) => value switch
    {
        bool b => b ? "true" : "false",
        float f => f.ToString("R", Inv),
        IFormattable formattable => formattable.ToString(null, Inv),
        _ => value?.ToString() ?? "",
    };

    /// <summary>Valeur typée pour l'endpoint, depuis son texte invariant.</summary>
    /// <exception cref="FormatException">Le texte ne convient pas au type.</exception>
    public static object ParseValue(EndpointValueType type, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        Type clr = EndpointValue.ClrType(type);
        if (clr == typeof(bool))
        {
            return text.Trim().ToUpperInvariant() switch
            {
                "TRUE" or "1" => true,
                "FALSE" or "0" => false,
                _ => throw new FormatException($"« {text} » n'est pas un booléen."),
            };
        }
        return Convert.ChangeType(text.Trim(), clr, Inv);
    }

    private static string Normalize(EndpointValueType type, string text)
    {
        try
        {
            return FormatValue(ParseValue(type, text));
        }
        catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException)
        {
            return text;
        }
    }

    private static string Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString() ?? ""
            : "";

    private static async Task<string[]> ReadVersionAsync(ODriveDevice device, CancellationToken ct, params string[] paths)
    {
        string[] parts = new string[paths.Length];
        for (int i = 0; i < paths.Length; i++)
        {
            parts[i] = device.Schema.FindProperty(paths[i]) is { } property
                ? FormatValue(await device.ReadAsync(property, ct).ConfigureAwait(false))
                : "?";
        }
        return parts;
    }
}
