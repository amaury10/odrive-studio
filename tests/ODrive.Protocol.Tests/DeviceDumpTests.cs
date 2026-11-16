using FluentAssertions;
using ODrive.Protocol.Simulation;
using Xunit;

namespace ODrive.Protocol.Tests;

public class DeviceDumpTests
{
    private static CancellationToken Ct => TestContext.Token;

    private static async Task<DeviceDump> CaptureAsync(SimulatedODrive board)
    {
        await using ODriveDevice device = await ODriveDevice.ConnectAsync(new SimulatedTransport(board), ct: Ct);
        return await DeviceDump.CaptureAsync(device, ct: Ct);
    }

    private static DeviceDump RoundTrip(DeviceDump dump)
    {
        using var stream = new MemoryStream();
        dump.Save(stream);
        stream.Position = 0;
        return DeviceDump.Load(stream);
    }

    [Fact]
    public async Task Capture_reads_identity_and_every_property()
    {
        SimulatedODrive board = SimulatedODrive.CreateReference();
        board.Set("vbus_voltage", 25.5f);

        DeviceDump dump = await CaptureAsync(board);

        dump.JsonVersionId.Should().Be(TestData.ReferenceJsonVersionId);
        dump.SerialNumber.Should().Be(0x2065_3884_5748ul);
        dump.EndpointJson.ToArray().Should().Equal(TestData.ReferenceJson());
        dump.Values.Should().HaveCount(734);
        dump.Values["vbus_voltage"].Should().Be(25.5f);
        dump.Values["axis0.motor.config.pole_pairs"].Should().Be(7);
    }

    [Fact]
    public async Task Capture_reports_progress()
    {
        var reported = new List<int>();
        SimulatedODrive board = SimulatedODrive.CreateReference();
        await using ODriveDevice device = await ODriveDevice.ConnectAsync(new SimulatedTransport(board), ct: Ct);

        await DeviceDump.CaptureAsync(device, new SynchronousProgress(reported.Add), Ct);

        reported.Should().HaveCount(734).And.BeInAscendingOrder();
        reported[^1].Should().Be(734);
    }

    [Fact]
    public async Task Saved_dump_is_loaded_back_with_identical_typed_values()
    {
        SimulatedODrive board = SimulatedODrive.CreateReference();
        board.Set("axis0.motor.error", 0xFFFF_FFFF_FFFFul);       // uint64 au-delà de 2^32
        board.Set("axis0.encoder.pos_estimate", float.NaN);
        board.Set("axis1.controller.input_vel", -1.25f);

        DeviceDump dump = await CaptureAsync(board);
        DeviceDump loaded = RoundTrip(dump);

        loaded.JsonVersionId.Should().Be(dump.JsonVersionId);
        loaded.SerialNumber.Should().Be(dump.SerialNumber);
        loaded.EndpointJson.ToArray().Should().Equal(dump.EndpointJson.ToArray());
        loaded.Values.Should().HaveCount(dump.Values.Count);
        loaded.Values["axis0.motor.error"].Should().Be(0xFFFF_FFFF_FFFFul);
        loaded.Values["axis0.encoder.pos_estimate"].Should().Be(float.NaN);
        loaded.Values["axis1.controller.input_vel"].Should().Be(-1.25f);
        loaded.Values["config.dc_max_positive_current"].Should().Be(float.PositiveInfinity);
        loaded.Values["config.enable_brake_resistor"].Should().Be(false);
    }

    [Fact]
    public async Task Simulated_board_can_start_from_a_dump()
    {
        SimulatedODrive original = SimulatedODrive.CreateReference();
        original.Set("axis0.motor.config.pole_pairs", 15);
        original.Set("vbus_voltage", 36f);
        DeviceDump dump = RoundTrip(await CaptureAsync(original));

        SimulatedODrive replay = SimulatedODrive.FromDump(dump);

        replay.Schema.JsonVersionId.Should().Be(TestData.ReferenceJsonVersionId);
        replay.Get("axis0.motor.config.pole_pairs").Should().Be(15);
        replay.Get("vbus_voltage").Should().Be(36f);
        // Les valeurs du dump sont celles du démarrage : elles survivent à un reboot.
        replay.Reboot();
        replay.Get("axis0.motor.config.pole_pairs").Should().Be(15);
    }

    private sealed class SynchronousProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
