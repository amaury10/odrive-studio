using FluentAssertions;
using ODrive.Protocol.Simulation;
using ODrive.Protocol.Transport;
using Xunit;

namespace ODrive.Protocol.Tests;

public class DeviceManagerTests
{
    private static CancellationToken Ct => TestContext.Token;

    [Fact]
    public async Task Scan_connects_every_board_found_by_the_sources()
    {
        var first = new SimulatedTransportSource(SimulatedODrive.CreateReference(), "sim:a");
        var second = new SimulatedTransportSource(SimulatedODrive.CreateReference(), "sim:b");
        await using var manager = new DeviceManager([first, second]);
        var connected = new List<ODriveDevice>();
        manager.DeviceConnected += (_, d) => connected.Add(d);

        await manager.ScanAsync(Ct);

        manager.Devices.Should().HaveCount(2);
        connected.Should().BeEquivalentTo(manager.Devices);
    }

    [Fact]
    public async Task Scan_does_not_reopen_a_board_that_is_already_connected()
    {
        var source = new SimulatedTransportSource(SimulatedODrive.CreateReference(), "sim:a");
        await using var manager = new DeviceManager([source]);

        await manager.ScanAsync(Ct);
        ODriveDevice device = manager.Devices.Single();
        await manager.ScanAsync(Ct);

        manager.Devices.Should().ContainSingle().Which.Should().BeSameAs(device);
    }

    [Fact]
    public async Task Rebooted_board_is_removed_then_reconnected_by_the_next_scan()
    {
        var source = new SimulatedTransportSource(SimulatedODrive.CreateReference(), "sim:a");
        await using var manager = new DeviceManager([source]);
        var disconnected = new List<ODriveDevice>();
        manager.DeviceDisconnected += (_, d) => disconnected.Add(d);
        await manager.ScanAsync(Ct);
        ODriveDevice before = manager.Devices.Single();

        await before.RebootAsync(Ct);

        manager.Devices.Should().BeEmpty();
        disconnected.Should().ContainSingle().Which.Should().BeSameAs(before);

        await manager.ScanAsync(Ct);

        ODriveDevice after = manager.Devices.Single();
        after.Should().NotBeSameAs(before);
        after.SerialNumber.Should().Be(before.SerialNumber);
        // Le JSON n'est pas retéléchargé : le cache du gestionnaire sert pour la reconnexion.
        after.Schema.Should().BeSameAs(before.Schema);
    }

    [Fact]
    public async Task Board_that_fails_to_open_is_reported_and_retried()
    {
        var source = new FlakySource();
        await using var manager = new DeviceManager([source]);

        await manager.ScanAsync(Ct);

        manager.Devices.Should().BeEmpty();
        manager.LastErrors.Should().ContainKey("flaky").WhoseValue.Should().Contain("occupée");

        await manager.ScanAsync(Ct);

        manager.Devices.Should().ContainSingle();
        manager.LastErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task Removed_source_board_stays_listed_until_its_connection_is_lost()
    {
        var board = SimulatedODrive.CreateReference();
        var source = new SimulatedTransportSource(board, "sim:a");
        await using var manager = new DeviceManager([source]);
        await manager.ScanAsync(Ct);

        source.IsPresent = false;
        await manager.ScanAsync(Ct);

        manager.Devices.Should().ContainSingle();
    }

    private sealed class FlakySource : ITransportSource
    {
        private readonly SimulatedODrive _board = SimulatedODrive.CreateReference();
        private int _attempts;

        public IReadOnlyList<TransportCandidate> Enumerate() =>
        [
            new TransportCandidate("flaky", "carte capricieuse", () =>
                ++_attempts == 1
                    ? throw new IOException("Interface occupée par une autre application.")
                    : new SimulatedTransport(_board)),
        ];
    }
}
