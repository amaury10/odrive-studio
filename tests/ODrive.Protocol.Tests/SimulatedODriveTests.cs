using System.Buffers.Binary;
using FluentAssertions;
using ODrive.Protocol.Simulation;
using Xunit;

namespace ODrive.Protocol.Tests;

/// <summary>
/// Le simulateur doit répondre comme la carte : mêmes règles de trailer, de bit d'ACK et de MTU
/// (<c>ref/ODrive/Firmware/fibre-cpp/legacy_protocol.cpp:411-501</c>).
/// </summary>
public class SimulatedODriveTests
{
    private const ushort JsonCrc = TestData.ReferenceJsonCrc;
    private readonly SimulatedODrive _device = SimulatedODrive.CreateReference();

    private static byte[] Request(ushort seqNo, ushort endpointId, ushort responseSize, byte[] payload, ushort trailer)
    {
        byte[] buffer = new byte[PacketCodec.MaxPacketSize];
        int length = PacketCodec.EncodeRequest(buffer, seqNo, endpointId, responseSize, payload, trailer);
        return buffer[..length];
    }

    private static byte[] Offset(uint offset)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, offset);
        return payload;
    }

    [Fact]
    public void Reference_device_embeds_the_reference_json()
    {
        _device.Schema.JsonCrc.Should().Be(JsonCrc);
        _device.Schema.Json.ToArray().Should().Equal(TestData.ReferenceJson());
    }

    [Fact]
    public void Endpoint0_returns_json_fragments_capped_at_61_bytes()
    {
        byte[]? response = _device.HandlePacket(Request(0x0081, 0, 512, Offset(100), PacketCodec.ProtocolVersion));

        response.Should().NotBeNull();
        response!.Length.Should().Be(63);
        response[..2].Should().Equal(0x81, 0x80);
        response[2..].Should().Equal(TestData.ReferenceJson()[100..161]);
    }

    [Fact]
    public void Endpoint0_returns_an_empty_response_past_the_end()
    {
        byte[]? response = _device.HandlePacket(Request(0x0081, 0, 61, Offset(55313), PacketCodec.ProtocolVersion));

        response.Should().Equal(0x81, 0x80);
    }

    [Fact]
    public void Endpoint0_returns_json_version_id_for_offset_FFFFFFFF()
    {
        byte[]? response = _device.HandlePacket(Request(0x0081, 0, 4, Offset(0xFFFFFFFF), PacketCodec.ProtocolVersion));

        response.Should().Equal(0x81, 0x80, 0x28, 0xAD, 0xA0, 0xC1);
    }

    [Fact]
    public void Endpoint0_ignores_a_request_whose_trailer_is_not_the_protocol_version()
    {
        _device.HandlePacket(Request(0x0081, 0, 61, Offset(0), JsonCrc)).Should().BeNull();
    }

    [Fact]
    public void Other_endpoints_ignore_a_request_whose_trailer_is_not_the_json_crc()
    {
        _device.HandlePacket(Request(0x0081, 2, 4, [], 0x1337)).Should().BeNull();
        _device.HandlePacket(Request(0x0081, 2, 4, [], PacketCodec.ProtocolVersion)).Should().BeNull();
    }

    [Fact]
    public void Property_read_returns_the_value()
    {
        _device.Set("vbus_voltage", 24.5f);

        byte[]? response = _device.HandlePacket(Request(0x0081, 2, 4, [], JsonCrc));

        response.Should().NotBeNull();
        BinaryPrimitives.ReadSingleLittleEndian(response.AsSpan(2)).Should().Be(24.5f);
    }

    [Fact]
    public void Property_write_stores_the_value_and_acknowledges_with_an_empty_response()
    {
        byte[]? response = _device.HandlePacket(Request(0x0081, 281, 0, EndpointValue.Encode(EndpointValueType.Float, 3.5f), JsonCrc));

        response.Should().Equal(0x81, 0x80);
        _device.Get("axis0.controller.config.vel_limit").Should().Be(3.5f);
    }

    [Fact]
    public void Write_to_a_read_only_property_is_ignored()
    {
        _device.Set("vbus_voltage", 24f);

        _device.HandlePacket(Request(0x0081, 2, 0, EndpointValue.Encode(EndpointValueType.Float, 99f), JsonCrc));

        _device.Get("vbus_voltage").Should().Be(24f);
    }

    [Fact]
    public void Request_without_the_response_bit_is_executed_but_not_answered()
    {
        byte[] request = Request(0x0081, 281, 0, EndpointValue.Encode(EndpointValueType.Float, 7f), JsonCrc);
        request[3] &= 0x7F; // retire le bit 15 de endpoint_id (legacy_protocol.cpp:459)

        _device.HandlePacket(request).Should().BeNull();
        _device.Get("axis0.controller.config.vel_limit").Should().Be(7f);
    }

    [Fact]
    public void Packets_with_bit_15_of_seq_no_or_too_short_are_ignored()
    {
        byte[] ack = Request(0x0081, 2, 4, [], JsonCrc);
        ack[1] |= 0x80;

        _device.HandlePacket(ack).Should().BeNull();
        _device.HandlePacket([0x81, 0x00, 0x02, 0x80, 0x04]).Should().BeNull();
    }

    [Fact]
    public void Function_call_reads_inputs_and_publishes_outputs()
    {
        _device.OnCall("get_adc_voltage", call => call.SetOutput("voltage", (uint)call.Input("gpio") * 0.5f));

        _device.HandlePacket(Request(0x0081, 770, 0, EndpointValue.Encode(EndpointValueType.UInt32, 3u), JsonCrc));
        byte[]? trigger = _device.HandlePacket(Request(0x0082, 769, 0, [], JsonCrc));
        byte[]? output = _device.HandlePacket(Request(0x0083, 771, 4, [], JsonCrc));

        trigger.Should().Equal(0x82, 0x80);
        BinaryPrimitives.ReadSingleLittleEndian(output.AsSpan(2)).Should().Be(1.5f);
    }

    [Fact]
    public void Clear_errors_resets_all_error_properties()
    {
        // ref/ODrive/Firmware/MotorControl/main.cpp:236-249
        _device.Set("error", (byte)2);
        _device.Set("axis0.error", 0x100u);
        _device.Set("axis1.motor.error", 0x10ul);
        _device.Set("axis0.encoder.error", (ushort)2);

        _device.HandlePacket(Request(0x0081, 787, 0, [], JsonCrc));

        _device.Get("error").Should().Be((byte)0);
        _device.Get("axis0.error").Should().Be(0u);
        _device.Get("axis1.motor.error").Should().Be(0ul);
        _device.Get("axis0.encoder.error").Should().Be((ushort)0);
    }

    [Fact]
    public void Reboot_sends_no_response_and_resets_unsaved_values()
    {
        _device.Set("axis0.controller.config.vel_limit", 9f);
        bool rebooted = false;
        _device.Rebooted += (_, _) => rebooted = true;

        byte[]? response = _device.HandlePacket(Request(0x0081, 775, 0, [], JsonCrc));

        response.Should().BeNull();
        rebooted.Should().BeTrue();
        _device.Get("axis0.controller.config.vel_limit").Should().Be(2f); // défaut du firmware
    }

    [Fact]
    public void Save_configuration_persists_config_values_across_the_reboot_it_triggers()
    {
        // ref/ODrive/Firmware/MotorControl/main.cpp:159-184 : la carte redémarre sans répondre.
        _device.Set("axis0.controller.config.vel_limit", 9f);
        _device.Set("axis0.controller.input_vel", 1f);
        bool rebooted = false;
        _device.Rebooted += (_, _) => rebooted = true;

        byte[]? response = _device.HandlePacket(Request(0x0081, 772, 0, [], JsonCrc));

        response.Should().BeNull();
        rebooted.Should().BeTrue();
        _device.Get("axis0.controller.config.vel_limit").Should().Be(9f);
        _device.Get("axis0.controller.input_vel").Should().Be(0f);
    }

    [Fact]
    public void Save_configuration_is_refused_without_reboot_while_an_axis_is_armed()
    {
        _device.Set("axis0.motor.is_armed", true);

        byte[]? response = _device.HandlePacket(Request(0x0081, 772, 0, [], JsonCrc));

        response.Should().Equal(0x81, 0x80);
        _device.Get("save_configuration.success").Should().Be(false);
    }

    [Fact]
    public void Requesting_closed_loop_arms_the_axis_and_requesting_idle_disarms_it()
    {
        // L'axe consomme requested_state et le remet à UNDEFINED (ref/ODrive/Firmware/MotorControl/axis.cpp:485).
        _device.HandlePacket(Request(0x0081, 135, 0, [8], JsonCrc)); // AXIS_STATE_CLOSED_LOOP_CONTROL

        _device.Get("axis0.current_state").Should().Be((byte)8);
        _device.Get("axis0.motor.is_armed").Should().Be(true);
        _device.Get("axis0.requested_state").Should().Be((byte)0);
        _device.Get("axis1.current_state").Should().Be((byte)1);

        _device.HandlePacket(Request(0x0082, 135, 0, [1], JsonCrc)); // AXIS_STATE_IDLE

        _device.Get("axis0.current_state").Should().Be((byte)1);
        _device.Get("axis0.motor.is_armed").Should().Be(false);
    }

    [Fact]
    public void Closed_loop_is_refused_while_the_axis_has_an_error()
    {
        _device.Set("axis0.error", 0x40u);

        _device.HandlePacket(Request(0x0081, 135, 0, [8], JsonCrc));

        _device.Get("axis0.current_state").Should().Be((byte)1);
        _device.Get("axis0.motor.is_armed").Should().Be(false);
    }

    [Fact]
    public void Write_hook_runs_after_the_value_is_stored()
    {
        object? seen = null;
        _device.OnWrite("axis0.controller.input_vel", device => seen = device.Get("axis0.controller.input_vel"));

        _device.HandlePacket(Request(0x0081, _device.Schema.GetProperty("axis0.controller.input_vel").Id, 0,
            EndpointValue.Encode(EndpointValueType.Float, 1.5f), JsonCrc));

        seen.Should().Be(1.5f);
    }

    [Fact]
    public void Erase_configuration_restores_defaults_and_reboots()
    {
        _device.Set("axis0.controller.config.vel_limit", 9f);
        _device.HandlePacket(Request(0x0081, 772, 0, [], JsonCrc)); // sauvegarde

        ushort eraseId = _device.Schema.GetFunction("erase_configuration").Id;
        _device.HandlePacket(Request(0x0082, eraseId, 0, [], JsonCrc)).Should().BeNull();

        _device.Get("axis0.controller.config.vel_limit").Should().Be(2f);
    }
}
