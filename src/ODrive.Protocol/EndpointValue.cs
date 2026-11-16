using System.Buffers.Binary;
using System.Globalization;

namespace ODrive.Protocol;

/// <summary>Types de propriété du JSON d'endpoints (<c>ref/ODrive/Firmware/fibre-cpp/legacy_object_client.cpp:182-194</c>).</summary>
#pragma warning disable CA1720 // Les noms reprennent ceux des types du JSON (int8, uint16, float�).
public enum EndpointValueType
{
    Bool,
    Int8,
    UInt8,
    Int16,
    UInt16,
    Int32,
    UInt32,
    Int64,
    UInt64,
    Float,
    EndpointRef,
}
#pragma warning restore CA1720

/// <summary>Référence vers un endpoint : <c>endpoint_id</c> u16 puis <c>json_crc</c> u16 (<c>legacy_object_client.cpp:512-513</c>).</summary>
public readonly record struct EndpointRef(ushort EndpointId, ushort JsonCrc);

/// <summary>Conversion entre valeurs .NET et octets little-endian des endpoints.</summary>
public static class EndpointValue
{
    public static int SizeOf(EndpointValueType type) => type switch
    {
        EndpointValueType.Bool or EndpointValueType.Int8 or EndpointValueType.UInt8 => 1,
        EndpointValueType.Int16 or EndpointValueType.UInt16 => 2,
        EndpointValueType.Int32 or EndpointValueType.UInt32 or EndpointValueType.Float
            or EndpointValueType.EndpointRef => 4,
        EndpointValueType.Int64 or EndpointValueType.UInt64 => 8,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    /// <summary>Nom du type dans le JSON (« float », « uint8 », « endpoint_ref »…).</summary>
    public static bool TryParseTypeName(string name, out EndpointValueType type)
    {
        EndpointValueType? parsed = name switch
        {
            "bool" => EndpointValueType.Bool,
            "int8" => EndpointValueType.Int8,
            "uint8" => EndpointValueType.UInt8,
            "int16" => EndpointValueType.Int16,
            "uint16" => EndpointValueType.UInt16,
            "int32" => EndpointValueType.Int32,
            "uint32" => EndpointValueType.UInt32,
            "int64" => EndpointValueType.Int64,
            "uint64" => EndpointValueType.UInt64,
            "float" => EndpointValueType.Float,
            "endpoint_ref" => EndpointValueType.EndpointRef,
            _ => null,
        };
        type = parsed ?? default;
        return parsed is not null;
    }

    /// <summary>Type .NET renvoyé par <see cref="Decode(EndpointValueType, ReadOnlySpan{byte})"/>.</summary>
    public static Type ClrType(EndpointValueType type) => type switch
    {
        EndpointValueType.Bool => typeof(bool),
        EndpointValueType.Int8 => typeof(sbyte),
        EndpointValueType.UInt8 => typeof(byte),
        EndpointValueType.Int16 => typeof(short),
        EndpointValueType.UInt16 => typeof(ushort),
        EndpointValueType.Int32 => typeof(int),
        EndpointValueType.UInt32 => typeof(uint),
        EndpointValueType.Int64 => typeof(long),
        EndpointValueType.UInt64 => typeof(ulong),
        EndpointValueType.Float => typeof(float),
        EndpointValueType.EndpointRef => typeof(EndpointRef),
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    public static object Decode(EndpointValueType type, ReadOnlySpan<byte> data)
    {
        if (data.Length != SizeOf(type))
        {
            throw new FormatException(
                $"Valeur de type {type} : {SizeOf(type)} octet(s) attendu(s), {data.Length} reçu(s).");
        }

        return type switch
        {
            EndpointValueType.Bool => data[0] != 0,
            EndpointValueType.Int8 => (sbyte)data[0],
            EndpointValueType.UInt8 => data[0],
            EndpointValueType.Int16 => BinaryPrimitives.ReadInt16LittleEndian(data),
            EndpointValueType.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(data),
            EndpointValueType.Int32 => BinaryPrimitives.ReadInt32LittleEndian(data),
            EndpointValueType.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(data),
            EndpointValueType.Int64 => BinaryPrimitives.ReadInt64LittleEndian(data),
            EndpointValueType.UInt64 => BinaryPrimitives.ReadUInt64LittleEndian(data),
            EndpointValueType.Float => BinaryPrimitives.ReadSingleLittleEndian(data),
            EndpointValueType.EndpointRef => new EndpointRef(
                BinaryPrimitives.ReadUInt16LittleEndian(data),
                BinaryPrimitives.ReadUInt16LittleEndian(data[2..])),
            _ => throw new ArgumentOutOfRangeException(nameof(type)),
        };
    }

    /// <summary>Décode puis convertit vers <typeparamref name="T"/> (primitif, enum généré ou <see cref="EndpointRef"/>).</summary>
    public static T Decode<T>(EndpointValueType type, ReadOnlySpan<byte> data)
    {
        object value = Decode(type, data);
        if (value is T typed)
        {
            return typed;
        }

        Type target = typeof(T);
        if (target.IsEnum)
        {
            return (T)Enum.ToObject(target, value);
        }

        return (T)Convert.ChangeType(value, target, CultureInfo.InvariantCulture);
    }

    public static byte[] Encode(EndpointValueType type, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        byte[] data = new byte[SizeOf(type)];

        if (type == EndpointValueType.EndpointRef)
        {
            var reference = (EndpointRef)value;
            BinaryPrimitives.WriteUInt16LittleEndian(data, reference.EndpointId);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2), reference.JsonCrc);
            return data;
        }

        if (value is Enum)
        {
            value = Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()), CultureInfo.InvariantCulture);
        }

        CultureInfo inv = CultureInfo.InvariantCulture;
        switch (type)
        {
            case EndpointValueType.Bool: data[0] = Convert.ToBoolean(value, inv) ? (byte)1 : (byte)0; break;
            case EndpointValueType.Int8: data[0] = (byte)Convert.ToSByte(value, inv); break;
            case EndpointValueType.UInt8: data[0] = Convert.ToByte(value, inv); break;
            case EndpointValueType.Int16: BinaryPrimitives.WriteInt16LittleEndian(data, Convert.ToInt16(value, inv)); break;
            case EndpointValueType.UInt16: BinaryPrimitives.WriteUInt16LittleEndian(data, Convert.ToUInt16(value, inv)); break;
            case EndpointValueType.Int32: BinaryPrimitives.WriteInt32LittleEndian(data, Convert.ToInt32(value, inv)); break;
            case EndpointValueType.UInt32: BinaryPrimitives.WriteUInt32LittleEndian(data, Convert.ToUInt32(value, inv)); break;
            case EndpointValueType.Int64: BinaryPrimitives.WriteInt64LittleEndian(data, Convert.ToInt64(value, inv)); break;
            case EndpointValueType.UInt64: BinaryPrimitives.WriteUInt64LittleEndian(data, Convert.ToUInt64(value, inv)); break;
            case EndpointValueType.Float: BinaryPrimitives.WriteSingleLittleEndian(data, Convert.ToSingle(value, inv)); break;
            default: throw new ArgumentOutOfRangeException(nameof(type));
        }
        return data;
    }
}
