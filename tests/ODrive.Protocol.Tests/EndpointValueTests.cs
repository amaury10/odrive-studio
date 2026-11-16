using FluentAssertions;
using Xunit;

namespace ODrive.Protocol.Tests;

public class EndpointValueTests
{
    public static TheoryData<EndpointValueType, object, byte[]> Vectors => new()
    {
        { EndpointValueType.Bool, true, new byte[] { 0x01 } },
        { EndpointValueType.Bool, false, new byte[] { 0x00 } },
        { EndpointValueType.Int8, (sbyte)-2, new byte[] { 0xFE } },
        { EndpointValueType.UInt8, (byte)0xAB, new byte[] { 0xAB } },
        { EndpointValueType.Int16, (short)-2, new byte[] { 0xFE, 0xFF } },
        { EndpointValueType.UInt16, (ushort)0x1234, new byte[] { 0x34, 0x12 } },
        { EndpointValueType.Int32, -2, new byte[] { 0xFE, 0xFF, 0xFF, 0xFF } },
        { EndpointValueType.UInt32, 0x12345678u, new byte[] { 0x78, 0x56, 0x34, 0x12 } },
        { EndpointValueType.Int64, -2L, new byte[] { 0xFE, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF } },
        { EndpointValueType.UInt64, 0x0102030405060708ul, new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 } },
        { EndpointValueType.Float, 1.5f, new byte[] { 0x00, 0x00, 0xC0, 0x3F } },
        // endpoint_id u16 puis json_crc u16 (legacy_object_client.cpp:512-513)
        { EndpointValueType.EndpointRef, new EndpointRef(0x0123, 0xC1A0), new byte[] { 0x23, 0x01, 0xA0, 0xC1 } },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Encode_writes_little_endian(EndpointValueType type, object value, byte[] expected)
    {
        EndpointValue.Encode(type, value).Should().Equal(expected);
        EndpointValue.SizeOf(type).Should().Be(expected.Length);
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Decode_reads_little_endian(EndpointValueType type, object expected, byte[] data)
    {
        EndpointValue.Decode(type, data).Should().Be(expected);
    }

    [Fact]
    public void Encode_converts_compatible_numeric_values()
    {
        // L'explorateur et les profils manipulent des double / long : la conversion est faite ici.
        EndpointValue.Encode(EndpointValueType.Float, 1.5).Should().Equal(0x00, 0x00, 0xC0, 0x3F);
        EndpointValue.Encode(EndpointValueType.UInt8, 8).Should().Equal(0x08);
    }

    [Fact]
    public void Encode_accepts_enums_through_their_underlying_value()
    {
        EndpointValue.Encode(EndpointValueType.UInt8, SampleState.ClosedLoop).Should().Equal(0x08);
    }

    [Fact]
    public void Encode_rejects_values_that_do_not_fit()
    {
        Action act = () => EndpointValue.Encode(EndpointValueType.UInt8, 256);

        act.Should().Throw<OverflowException>();
    }

    [Fact]
    public void Decode_rejects_data_of_the_wrong_size()
    {
        Action act = () => EndpointValue.Decode(EndpointValueType.Float, [0x00, 0x00]);

        act.Should().Throw<FormatException>();
    }

    [Fact]
    public void Decode_generic_converts_to_enums_and_primitives()
    {
        EndpointValue.Decode<SampleState>(EndpointValueType.UInt8, [0x08]).Should().Be(SampleState.ClosedLoop);
        EndpointValue.Decode<float>(EndpointValueType.Float, [0x00, 0x00, 0xC0, 0x3F]).Should().Be(1.5f);
        EndpointValue.Decode<double>(EndpointValueType.Float, [0x00, 0x00, 0xC0, 0x3F]).Should().Be(1.5);
        EndpointValue.Decode<bool>(EndpointValueType.Bool, [0x01]).Should().BeTrue();
    }

    private enum SampleState : byte
    {
        Idle = 1,
        ClosedLoop = 8,
    }
}
