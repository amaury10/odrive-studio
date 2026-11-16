using FluentAssertions;
using ODrive.Protocol.Simulation;
using Xunit;

namespace ODrive.Protocol.Tests;

public class ODriveDeviceTests
{
    private static readonly ODriveDeviceOptions FastTimeout = new() { RequestTimeout = TimeSpan.FromMilliseconds(30) };

    private readonly SimulatedODrive _board = SimulatedODrive.CreateReference();

    private static CancellationToken Ct => TestContext.Token;

    private async Task<(ODriveDevice Device, SimulatedTransport Transport)> ConnectAsync(ODriveDeviceOptions? options = null)
    {
        var transport = new SimulatedTransport(_board);
        ODriveDevice device = await ODriveDevice.ConnectAsync(transport, options ?? FastTimeout, Ct);
        return (device, transport);
    }

    [Fact]
    public async Task Connect_downloads_the_json_and_builds_the_schema()
    {
        (ODriveDevice device, _) = await ConnectAsync();

        device.IsConnected.Should().BeTrue();
        device.Schema.JsonCrc.Should().Be(TestData.ReferenceJsonCrc);
        device.Schema.Json.ToArray().Should().Equal(TestData.ReferenceJson());
        device.SerialNumber.Should().Be(0x2065_3884_5748ul);
    }

    [Fact]
    public async Task Read_returns_the_typed_value_of_a_property()
    {
        (ODriveDevice device, _) = await ConnectAsync();
        _board.Set("vbus_voltage", 23.75f);

        (await device.ReadAsync<float>("vbus_voltage", Ct)).Should().Be(23.75f);
        (await device.ReadAsync(device.Schema.GetProperty("vbus_voltage"), Ct)).Should().Be(23.75f);
    }

    [Fact]
    public async Task Write_updates_the_property_on_the_board()
    {
        (ODriveDevice device, _) = await ConnectAsync();

        await device.WriteAsync("axis0.controller.config.vel_limit", 5.5f, Ct);

        _board.Get("axis0.controller.config.vel_limit").Should().Be(5.5f);
    }

    [Fact]
    public async Task Write_to_a_read_only_property_is_refused_before_sending_anything()
    {
        (ODriveDevice device, SimulatedTransport transport) = await ConnectAsync();
        int before = transport.RequestCount;

        Func<Task> act = () => device.WriteAsync("vbus_voltage", 1f, Ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*vbus_voltage*");
        transport.RequestCount.Should().Be(before);
    }

    [Fact]
    public async Task Unknown_path_is_reported_with_its_name()
    {
        (ODriveDevice device, _) = await ConnectAsync();

        Func<Task> act = () => device.ReadAsync<float>("axis0.nope", Ct);

        await act.Should().ThrowAsync<KeyNotFoundException>().WithMessage("*axis0.nope*");
    }

    [Fact]
    public async Task Call_writes_inputs_triggers_the_function_and_reads_outputs()
    {
        (ODriveDevice device, _) = await ConnectAsync();
        _board.OnCall("get_adc_voltage", call => call.SetOutput("voltage", (uint)call.Input("gpio") * 0.5f));

        IReadOnlyList<object> outputs = await device.CallAsync("get_adc_voltage", [3u], Ct);

        outputs.Should().Equal(1.5f);
    }

    [Fact]
    public async Task Call_without_arguments_runs_the_function()
    {
        (ODriveDevice device, _) = await ConnectAsync();
        _board.Set("axis0.error", 0x40u);

        IReadOnlyList<object> outputs = await device.CallAsync("clear_errors", [], Ct);

        outputs.Should().BeEmpty();
        _board.Get("axis0.error").Should().Be(0u);
    }

    [Fact]
    public async Task Call_rejects_a_wrong_number_of_arguments()
    {
        (ODriveDevice device, _) = await ConnectAsync();

        Func<Task> act = () => device.CallAsync("get_adc_voltage", [], Ct);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Lost_response_is_retried_up_to_three_attempts()
    {
        (ODriveDevice device, SimulatedTransport transport) = await ConnectAsync();
        _board.Set("vbus_voltage", 12f);
        transport.ResponsesToDrop = 2;

        (await device.ReadAsync<float>("vbus_voltage", Ct)).Should().Be(12f);
        device.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task Three_lost_responses_mark_the_connection_as_lost()
    {
        (ODriveDevice device, SimulatedTransport transport) = await ConnectAsync();
        bool lost = false;
        device.ConnectionLost += (_, _) => lost = true;
        transport.ResponsesToDrop = 3;

        Func<Task> act = () => device.ReadAsync<float>("vbus_voltage", Ct);

        await act.Should().ThrowAsync<ODriveConnectionLostException>();
        device.IsConnected.Should().BeFalse();
        lost.Should().BeTrue();
    }

    [Fact]
    public async Task Requests_fail_immediately_once_the_connection_is_lost()
    {
        (ODriveDevice device, SimulatedTransport transport) = await ConnectAsync();
        transport.Disconnect();

        Func<Task> act = () => device.ReadAsync<float>("vbus_voltage", Ct);

        await act.Should().ThrowAsync<ODriveConnectionLostException>();
        device.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task Response_with_another_sequence_number_is_discarded()
    {
        (ODriveDevice device, SimulatedTransport transport) = await ConnectAsync();
        _board.Set("vbus_voltage", 12f);
        // Réponse tardive d'une ancienne requête : seq_no 0x7F00, valeur 99.
        transport.InjectIncoming([0x00, 0xFF, .. EndpointValue.Encode(EndpointValueType.Float, 99f)]);

        (await device.ReadAsync<float>("vbus_voltage", Ct)).Should().Be(12f);
    }

    [Fact]
    public async Task Function_trigger_is_never_sent_twice()
    {
        // Rejouer un déclenchement dont la réponse s'est perdue exécuterait la fonction deux fois.
        (ODriveDevice device, SimulatedTransport transport) = await ConnectAsync();
        int calls = 0;
        _board.OnCall("axis0.watchdog_feed", _ => calls++);
        transport.ResponsesToDrop = 1;

        Func<Task> act = () => device.CallAsync("axis0.watchdog_feed", [], Ct);

        await act.Should().ThrowAsync<ODriveConnectionLostException>();
        calls.Should().Be(1);
    }

    [Fact]
    public async Task Concurrent_requests_are_serialized()
    {
        (ODriveDevice device, _) = await ConnectAsync();
        _board.Set("vbus_voltage", 12f);
        _board.Set("axis0.controller.config.vel_limit", 3f);

        Task<float>[] reads = Enumerable.Range(0, 50)
            .Select(i => device.ReadAsync<float>(i % 2 == 0 ? "vbus_voltage" : "axis0.controller.config.vel_limit", Ct))
            .ToArray();
        float[] values = await Task.WhenAll(reads);

        values.Where((_, i) => i % 2 == 0).Should().AllBeEquivalentTo(12f);
        values.Where((_, i) => i % 2 == 1).Should().AllBeEquivalentTo(3f);
    }

    [Fact]
    public async Task Reboot_loses_the_connection_without_throwing()
    {
        (ODriveDevice device, _) = await ConnectAsync();

        await device.RebootAsync(Ct);

        device.IsConnected.Should().BeFalse();
    }

    [Fact]
    public async Task Board_can_be_reconnected_after_a_reboot()
    {
        (ODriveDevice device, _) = await ConnectAsync();
        await device.WriteAsync("axis0.controller.config.vel_limit", 8f, Ct);
        await device.RebootAsync(Ct);

        (ODriveDevice again, _) = await ConnectAsync();

        (await again.ReadAsync<float>("axis0.controller.config.vel_limit", Ct)).Should().Be(2f);
    }

    [Fact]
    public async Task SaveConfiguration_returns_true_when_the_board_reboots_and_keeps_the_values()
    {
        (ODriveDevice device, _) = await ConnectAsync();
        await device.WriteAsync("axis0.controller.config.vel_limit", 8f, Ct);

        bool saved = await device.SaveConfigurationAsync(Ct);

        saved.Should().BeTrue();
        device.IsConnected.Should().BeFalse();
        (ODriveDevice again, _) = await ConnectAsync();
        (await again.ReadAsync<float>("axis0.controller.config.vel_limit", Ct)).Should().Be(8f);
    }

    [Fact]
    public async Task SaveConfiguration_returns_false_when_the_board_refuses()
    {
        (ODriveDevice device, _) = await ConnectAsync();
        _board.Set("axis1.motor.is_armed", true);

        bool saved = await device.SaveConfigurationAsync(Ct);

        saved.Should().BeFalse();
        device.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task Cached_schema_is_reused_after_a_single_version_request()
    {
        var cache = new MemorySchemaCache();
        var options = new ODriveDeviceOptions { RequestTimeout = FastTimeout.RequestTimeout, SchemaCache = cache };
        (ODriveDevice first, SimulatedTransport firstTransport) = await ConnectAsync(options);
        int fullDownload = firstTransport.Endpoint0RequestCount;

        (ODriveDevice second, SimulatedTransport secondTransport) = await ConnectAsync(options);

        fullDownload.Should().BeGreaterThan(900);
        secondTransport.Endpoint0RequestCount.Should().Be(1);
        second.Schema.Should().BeSameAs(first.Schema);
    }

    [Fact]
    public async Task RemoteProperty_reads_and_writes_typed_values()
    {
        (ODriveDevice device, _) = await ConnectAsync();
        var velLimit = new RemoteProperty<float>(device, "axis0.controller.config.vel_limit");
        var state = new RemoteProperty<SampleState>(device, "axis0.current_state");

        await velLimit.SetAsync(4f, Ct);

        (await velLimit.GetAsync(Ct)).Should().Be(4f);
        (await state.GetAsync(Ct)).Should().Be(SampleState.Idle);
        velLimit.Path.Should().Be("axis0.controller.config.vel_limit");
    }

    private enum SampleState : byte
    {
        Undefined = 0,
        Idle = 1,
    }
}

internal static class TestContext
{
    /// <summary>Garde-fou : aucun test du protocole ne doit durer plus de quelques secondes.</summary>
    public static CancellationToken Token => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;
}
