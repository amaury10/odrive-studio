using FluentAssertions;
using ODrive.Protocol.Simulation;
using Xunit;

namespace ODrive.Protocol.Tests;

public class PriorityAsyncLockTests
{
    [Fact]
    public async Task Emergency_waiter_passes_before_normal_waiters_queued_earlier()
    {
        var gate = new PriorityAsyncLock();
        var order = new List<string>();
        await gate.EnterAsync(RequestPriority.Normal, CancellationToken.None);

        Task first = RunAsync("normal 1", RequestPriority.Normal);
        Task second = RunAsync("normal 2", RequestPriority.Normal);
        Task urgent = RunAsync("urgence", RequestPriority.Emergency);
        gate.Exit();
        await Task.WhenAll(first, second, urgent);

        order.Should().Equal("urgence", "normal 1", "normal 2");

        async Task RunAsync(string name, RequestPriority priority)
        {
            await gate.EnterAsync(priority, CancellationToken.None);
            order.Add(name);
            gate.Exit();
        }
    }

    [Fact]
    public async Task Cancelled_waiter_does_not_block_the_next_one()
    {
        var gate = new PriorityAsyncLock();
        await gate.EnterAsync(RequestPriority.Normal, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        Task cancelled = gate.EnterAsync(RequestPriority.Normal, cts.Token);
        Task next = gate.EnterAsync(RequestPriority.Normal, CancellationToken.None);

        await cts.CancelAsync();
        gate.Exit();

        await FluentActions.Awaiting(() => cancelled).Should().ThrowAsync<OperationCanceledException>();
        await next.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

public class WriteJournalTests
{
    private static CancellationToken Ct => TestContext.Token;
    private readonly SimulatedODrive _board = SimulatedODrive.CreateReference();
    private readonly WriteJournal _journal = new();

    private Task<ODriveDevice> ConnectAsync() => ODriveDevice.ConnectAsync(
        new SimulatedTransport(_board), new ODriveDeviceOptions { Journal = _journal }, Ct);

    [Fact]
    public async Task Write_is_recorded_with_old_and_new_values()
    {
        ODriveDevice device = await ConnectAsync();
        DateTimeOffset before = DateTimeOffset.Now;

        await device.WriteAsync("axis0.controller.config.vel_limit", 5f, Ct);

        WriteJournalEntry entry = _journal.Entries.Should().ContainSingle().Subject;
        entry.Kind.Should().Be(JournalEntryKind.Write);
        entry.Path.Should().Be("axis0.controller.config.vel_limit");
        entry.OldValue.Should().Be(2f);
        entry.NewValue.Should().Be(5f);
        entry.SerialNumber.Should().Be(device.SerialNumber);
        entry.Timestamp.Should().BeOnOrAfter(before);
    }

    [Fact]
    public async Task Function_call_is_recorded_with_its_arguments()
    {
        ODriveDevice device = await ConnectAsync();

        await device.CallAsync("get_adc_voltage", [3u], Ct);

        WriteJournalEntry entry = _journal.Entries.Should().ContainSingle().Subject;
        entry.Kind.Should().Be(JournalEntryKind.Call);
        entry.Path.Should().Be("get_adc_voltage");
        entry.NewValue.Should().Be("3");
    }

    [Fact]
    public async Task Emergency_write_is_recorded_without_reading_the_old_value_first()
    {
        ODriveDevice device = await ConnectAsync();
        var transport = new SimulatedTransport(_board);
        ODriveDevice counted = await ODriveDevice.ConnectAsync(transport,
            new ODriveDeviceOptions { Journal = _journal, SchemaCache = new MemorySchemaCache() }, Ct);
        int before = transport.RequestCount;

        await counted.WriteAsync(counted.Schema.GetProperty("axis0.requested_state"), 1, RequestPriority.Emergency, Ct);

        (transport.RequestCount - before).Should().Be(1);
        _journal.Entries.Should().ContainSingle().Which.OldValue.Should().BeNull();
        await device.DisposeAsync();
    }

    [Fact]
    public async Task Journal_raises_an_event_and_exports_csv()
    {
        ODriveDevice device = await ConnectAsync();
        var seen = new List<WriteJournalEntry>();
        _journal.EntryAdded += (_, e) => seen.Add(e);

        await device.WriteAsync("axis0.controller.config.vel_limit", 5.5f, Ct);
        using var writer = new StringWriter();
        _journal.ExportCsv(writer);

        seen.Should().ContainSingle();
        string[] lines = writer.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        lines[0].Should().Be("horodatage;carte;type;chemin;ancienne valeur;nouvelle valeur");
        lines[1].Should().EndWith($";{device.SerialNumber:X};écriture;axis0.controller.config.vel_limit;2;5.5");
    }
}
