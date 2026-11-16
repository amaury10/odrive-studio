namespace ODrive.Protocol;

/// <summary>
/// CRC du protocole natif : calcul MSB en premier, sans réflexion ni XOR final
/// (<c>ref/ODrive/Firmware/fibre-cpp/crc.hpp:10-27</c>).
/// </summary>
public static class Crc
{
    /// <summary>Polynôme du CRC16 (<c>legacy_protocol.hpp:25</c>, CANONICAL_CRC16_POLYNOMIAL).</summary>
    public const ushort Crc16Polynomial = 0x3D65;

    /// <summary>Init du CRC16 du tramage série uniquement (<c>legacy_protocol.hpp:26</c>). Le CRC du JSON part de <see cref="PacketCodec.ProtocolVersion"/>.</summary>
    public const ushort Crc16SerialInit = 0x1337;

    /// <summary>Polynôme du CRC8 de l'en-tête série (<c>legacy_protocol.hpp:18</c>).</summary>
    public const byte Crc8Polynomial = 0x37;

    /// <summary>Init du CRC8 de l'en-tête série (<c>legacy_protocol.hpp:19</c>).</summary>
    public const byte Crc8Init = 0x42;

    public static ushort Crc16(ushort init, ReadOnlySpan<byte> data)
    {
        uint remainder = init;
        foreach (byte value in data)
        {
            remainder ^= (uint)value << 8;
            for (int bit = 0; bit < 8; bit++)
            {
                remainder = (remainder & 0x8000) != 0
                    ? (remainder << 1) ^ Crc16Polynomial
                    : remainder << 1;
            }
            remainder &= 0xFFFF;
        }
        return (ushort)remainder;
    }

    public static byte Crc8(byte init, ReadOnlySpan<byte> data)
    {
        uint remainder = init;
        foreach (byte value in data)
        {
            remainder ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                remainder = (remainder & 0x80) != 0
                    ? (remainder << 1) ^ Crc8Polynomial
                    : remainder << 1;
            }
            remainder &= 0xFF;
        }
        return (byte)remainder;
    }
}
