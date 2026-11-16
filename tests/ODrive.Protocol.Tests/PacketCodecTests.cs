using FluentAssertions;
using Xunit;

namespace ODrive.Protocol.Tests;

/// <summary>Format des paquets : <c>ref/ODrive/Firmware/fibre-cpp/legacy_protocol.cpp:230-248</c> et <c>:411-501</c>.</summary>
public class PacketCodecTests
{
    [Fact]
    public void EncodeRequest_writes_header_payload_and_trailer_little_endian()
    {
        Span<byte> buffer = stackalloc byte[PacketCodec.MaxPacketSize];

        int length = PacketCodec.EncodeRequest(buffer, seqNo: 0x0081, endpointId: 0x0123,
            expectedResponseSize: 4, payload: [0xDE, 0xAD], trailer: 0xC1A0);

        buffer[..length].ToArray().Should().Equal(
            0x81, 0x00,  // seq_no
            0x23, 0x81,  // endpoint_id | 0x8000 (réponse toujours demandée)
            0x04, 0x00,  // expected_response_size
            0xDE, 0xAD,  // payload
            0xA0, 0xC1); // trailer
    }

    [Fact]
    public void EncodeRequest_accepts_the_largest_payload_that_fits_in_63_bytes()
    {
        byte[] buffer = new byte[PacketCodec.MaxPacketSize];
        byte[] payload = new byte[PacketCodec.MaxRequestPayloadSize];

        PacketCodec.EncodeRequest(buffer, 0x0080, 1, 0, payload, 1).Should().Be(63);
        PacketCodec.MaxRequestPayloadSize.Should().Be(55);
    }

    [Fact]
    public void EncodeRequest_rejects_a_payload_that_would_exceed_the_mtu()
    {
        byte[] buffer = new byte[128];
        byte[] payload = new byte[PacketCodec.MaxRequestPayloadSize + 1];

        Action act = () => PacketCodec.EncodeRequest(buffer, 0x0080, 1, 0, payload, 1);

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0x8001)] // bit 15 : la carte prendrait la requête pour un ACK (legacy_protocol.cpp:419)
    public void EncodeRequest_rejects_a_sequence_number_with_bit_15_set(ushort seqNo)
    {
        byte[] buffer = new byte[PacketCodec.MaxPacketSize];

        Action act = () => PacketCodec.EncodeRequest(buffer, seqNo, 1, 0, [], 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void EncodeRequest_rejects_an_endpoint_id_above_15_bits()
    {
        byte[] buffer = new byte[PacketCodec.MaxPacketSize];

        Action act = () => PacketCodec.EncodeRequest(buffer, 0x0080, 0x8000, 0, [], 1);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Theory]
    [InlineData(0x0000, 0x0081)]
    [InlineData(0x0081, 0x0082)]
    [InlineData(0x00FF, 0x0180)] // le bit 7 reste à 1 (legacy_protocol.cpp:208)
    [InlineData(0x7FFF, 0x0080)] // retour à zéro sans jamais toucher au bit 15
    public void NextSequenceNumber_increments_on_15_bits_and_forces_bit_7(ushort previous, ushort expected)
    {
        PacketCodec.NextSequenceNumber(previous).Should().Be(expected);
    }

    [Fact]
    public void TryDecodeResponse_returns_sequence_number_and_payload()
    {
        byte[] packet = [0x81, 0x80, 0x01, 0x02, 0x03];

        bool ok = PacketCodec.TryDecodeResponse(packet, out ushort seqNo, out ReadOnlySpan<byte> payload);

        ok.Should().BeTrue();
        seqNo.Should().Be(0x0081);
        payload.ToArray().Should().Equal(0x01, 0x02, 0x03);
    }

    [Fact]
    public void TryDecodeResponse_accepts_an_empty_acknowledgement()
    {
        PacketCodec.TryDecodeResponse([0x81, 0x80], out ushort seqNo, out ReadOnlySpan<byte> payload).Should().BeTrue();
        seqNo.Should().Be(0x0081);
        payload.Length.Should().Be(0);
    }

    [Theory]
    [InlineData(new byte[] { })]
    [InlineData(new byte[] { 0x81 })]
    [InlineData(new byte[] { 0x81, 0x00, 0x01 })] // bit 15 à 0 : ce n'est pas une réponse
    public void TryDecodeResponse_rejects_packets_that_are_not_responses(byte[] packet)
    {
        PacketCodec.TryDecodeResponse(packet, out _, out _).Should().BeFalse();
    }
}
