using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ODrive.Protocol;

/// <summary>
/// Photographie d'une carte : son JSON d'endpoints et la valeur de toutes ses propriétés. Produit par
/// <c>ODrive.Cli dump</c> ; sert à confirmer le JSON de référence et à faire démarrer le simulateur sur l'état
/// réel d'une carte.
/// </summary>
public sealed class DeviceDump
{
    private readonly byte[] _endpointJson;

    public DeviceDump(ReadOnlySpan<byte> endpointJson, ulong serialNumber, IReadOnlyDictionary<string, object> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _endpointJson = endpointJson.ToArray();
        Schema = EndpointSchema.Parse(_endpointJson);
        SerialNumber = serialNumber;
        Values = values;
    }

    public EndpointSchema Schema { get; }
    public ReadOnlyMemory<byte> EndpointJson => _endpointJson;
    public uint JsonVersionId => Schema.JsonVersionId;
    public ulong SerialNumber { get; }

    /// <summary>Valeur de chaque propriété par chemin, dans le type .NET de son type d'endpoint.</summary>
    public IReadOnlyDictionary<string, object> Values { get; }

    /// <summary>Lit toutes les propriétés de la carte. <paramref name="progress"/> reçoit le nombre de propriétés lues.</summary>
    public static async Task<DeviceDump> CaptureAsync(ODriveDevice device, IProgress<int>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        int done = 0;
        foreach (PropertyNode property in device.Schema.Properties)
        {
            values[property.Path] = await device.ReadAsync(property, ct).ConfigureAwait(false);
            progress?.Report(++done);
        }
        return new DeviceDump(device.Schema.Json.Span, device.SerialNumber, values);
    }

    public void Save(Stream stream)
    {
        using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("json_version_id", "0x" + JsonVersionId.ToString("X8", CultureInfo.InvariantCulture));
        writer.WriteString("json_crc", "0x" + Schema.JsonCrc.ToString("X4", CultureInfo.InvariantCulture));
        writer.WriteString("serial_number", SerialNumber.ToString("X", CultureInfo.InvariantCulture));
        writer.WriteStartObject("values");
        foreach (PropertyNode property in Schema.Properties)
        {
            if (Values.TryGetValue(property.Path, out object? value))
            {
                writer.WritePropertyName(property.Path);
                WriteValue(writer, value);
            }
        }
        writer.WriteEndObject();
        // Le JSON de la carte est conservé tel quel, en chaîne : son CRC dépend de chaque octet.
        writer.WriteString("endpoint_json", Encoding.UTF8.GetString(_endpointJson));
        writer.WriteEndObject();
    }

    public static DeviceDump Load(Stream stream)
    {
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        byte[] endpointJson = Encoding.UTF8.GetBytes(root.GetProperty("endpoint_json").GetString() ?? "");
        EndpointSchema schema = EndpointSchema.Parse(endpointJson);
        ulong serial = ulong.Parse(root.GetProperty("serial_number").GetString() ?? "0", NumberStyles.HexNumber,
            CultureInfo.InvariantCulture);

        var values = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (JsonProperty entry in root.GetProperty("values").EnumerateObject())
        {
            if (schema.FindProperty(entry.Name) is { } property)
            {
                values[entry.Name] = ReadValue(property.Type, entry.Value);
            }
        }
        return new DeviceDump(endpointJson, serial, values);
    }

    private static void WriteValue(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case bool b: writer.WriteBooleanValue(b); break;
            case float f when float.IsFinite(f): writer.WriteNumberValue(f); break;
            // JSON n'a pas de littéral pour NaN et les infinis.
            case float f: writer.WriteStringValue(f.ToString(CultureInfo.InvariantCulture)); break;
            case ulong u: writer.WriteNumberValue(u); break;
            case EndpointRef r: writer.WriteNumberValue(((uint)r.JsonCrc << 16) | r.EndpointId); break;
            default: writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
        }
    }

    private static object ReadValue(EndpointValueType type, JsonElement element) => type switch
    {
        EndpointValueType.Bool => element.GetBoolean(),
        EndpointValueType.Float => element.ValueKind == JsonValueKind.String
            ? float.Parse(element.GetString()!, CultureInfo.InvariantCulture)
            : element.GetSingle(),
        EndpointValueType.Int8 => element.GetSByte(),
        EndpointValueType.UInt8 => element.GetByte(),
        EndpointValueType.Int16 => element.GetInt16(),
        EndpointValueType.UInt16 => element.GetUInt16(),
        EndpointValueType.Int32 => element.GetInt32(),
        EndpointValueType.UInt32 => element.GetUInt32(),
        EndpointValueType.Int64 => element.GetInt64(),
        EndpointValueType.UInt64 => element.GetUInt64(),
        EndpointValueType.EndpointRef => new EndpointRef(
            (ushort)(element.GetUInt32() & 0xFFFF), (ushort)(element.GetUInt32() >> 16)),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };
}
