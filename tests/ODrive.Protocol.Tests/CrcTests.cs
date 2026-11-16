using System.Text;
using FluentAssertions;
using Xunit;

namespace ODrive.Protocol.Tests;

/// <summary>
/// Vecteurs calculés par une implémentation indépendante (Python) de l'algorithme de
/// <c>ref/ODrive/Firmware/fibre-cpp/crc.hpp:10-27</c> : MSB en premier, sans réflexion ni XOR final.
/// </summary>
public class CrcTests
{
    private static readonly byte[] Check = Encoding.ASCII.GetBytes("123456789");

    [Theory]
    [InlineData(0x1337, 0xAA01)] // init du tramage série
    [InlineData(0x0001, 0xB0BC)] // init du CRC du JSON (version du protocole)
    [InlineData(0x0000, 0x3D48)]
    public void Crc16_matches_reference_vectors(ushort init, ushort expected)
    {
        Crc.Crc16(init, Check).Should().Be(expected);
    }

    [Fact]
    public void Crc16_of_empty_data_is_the_initial_value()
    {
        Crc.Crc16(0x1337, []).Should().Be(0x1337);
    }

    [Fact]
    public void Crc16_can_be_computed_incrementally()
    {
        ushort partial = Crc.Crc16(0x1337, Check.AsSpan(0, 4));
        Crc.Crc16(partial, Check.AsSpan(4)).Should().Be(0xAA01);
    }

    [Fact]
    public void Crc8_matches_reference_vectors()
    {
        Crc.Crc8(Crc.Crc8Init, Check).Should().Be(0x8C);
        Crc.Crc8(Crc.Crc8Init, [0xAA, 0x08]).Should().Be(0x3D);
    }

    [Fact]
    public void Json_crc_of_reference_json_is_C1A0()
    {
        byte[] json = TestData.ReferenceJson();

        json.Should().HaveCount(55313);
        ushort jsonCrc = Crc.Crc16(PacketCodec.ProtocolVersion, json);
        jsonCrc.Should().Be(0xC1A0);
        // json_version_id = (json_crc << 16) | crc16(init = json_crc, json) — endpoints_template.j2:34
        Crc.Crc16(jsonCrc, json).Should().Be(0xAD28);
    }
}
