using FluentAssertions;
using ODrive.Protocol;
using ODrive.Protocol.Simulation;
using ODrive.Protocol.Transport;
using Xunit;

namespace ODrive.Model.Tests;

public class ErrorDecoderTests
{
    private readonly SimulatedODrive _board = SimulatedODrive.CreateReference();

    private Task<ODriveDevice> ConnectAsync() => ODriveDevice.ConnectAsync(new SimulatedTransport(_board));

    [Fact]
    public void Error_properties_cover_board_can_and_every_axis_module()
    {
        IEnumerable<string> paths = ErrorDecoder.ErrorProperties(_board.Schema).Select(p => p.Path);

        paths.Should().BeEquivalentTo(
            "error", "can.error",
            "axis0.error", "axis0.motor.error", "axis0.controller.error", "axis0.encoder.error",
            "axis0.sensorless_estimator.error",
            "axis1.error", "axis1.motor.error", "axis1.controller.error", "axis1.encoder.error",
            "axis1.sensorless_estimator.error");
    }

    [Fact]
    public void Decode_splits_a_raw_value_into_named_flags_with_their_description()
    {
        IReadOnlyList<ActiveError> errors = ErrorDecoder.Decode("axis0.encoder.error", 0x10 | 0x02);

        errors.Select(e => e.Name).Should().BeEquivalentTo("ENCODER_ERROR_CPR_POLEPAIRS_MISMATCH", "ENCODER_ERROR_ILLEGAL_HALL_STATE");
        ActiveError hall = errors.Single(e => e.Name == "ENCODER_ERROR_ILLEGAL_HALL_STATE");
        hall.Source.Should().Be("axis0.encoder");
        hall.Path.Should().Be("axis0.encoder.error");
        hall.Description.Should().Contain("6 valid states");
    }

    [Fact]
    public void Decode_reports_unknown_bits_instead_of_hiding_them()
    {
        IReadOnlyList<ActiveError> errors = ErrorDecoder.Decode("can.error", 0x80);

        errors.Should().ContainSingle().Which.Name.Should().Be("CAN_ERROR_0x80");
    }

    [Fact]
    public async Task ReadAll_returns_every_active_error_of_the_board()
    {
        ODriveDevice device = await ConnectAsync();
        _board.Set("axis0.error", 0x40u);
        _board.Set("axis0.motor.error", 0x1ul);
        _board.Set("error", (byte)0x2);

        IReadOnlyList<ActiveError> errors = await ErrorDecoder.ReadAllAsync(device);

        errors.Select(e => e.Name).Should().BeEquivalentTo(
            "ODRIVE_ERROR_DC_BUS_UNDER_VOLTAGE", "AXIS_ERROR_MOTOR_FAILED", "MOTOR_ERROR_PHASE_RESISTANCE_OUT_OF_RANGE");
    }

    [Fact]
    public async Task ReadAll_returns_nothing_for_a_healthy_board()
    {
        ODriveDevice device = await ConnectAsync();

        (await ErrorDecoder.ReadAllAsync(device)).Should().BeEmpty();
    }
}

public class EmergencyStopTests
{
    [Fact]
    public async Task Stop_requests_idle_on_every_axis_of_every_board()
    {
        SimulatedODrive first = SimulatedODrive.CreateReference();
        SimulatedODrive second = SimulatedODrive.CreateReference();
        ODriveDevice a = await ODriveDevice.ConnectAsync(new SimulatedTransport(first));
        ODriveDevice b = await ODriveDevice.ConnectAsync(new SimulatedTransport(second));
        await new ODrive3(a).Axis0.RequestedState.SetAsync(AxisState.ClosedLoopControl);
        await new ODrive3(a).Axis1.RequestedState.SetAsync(AxisState.ClosedLoopControl);
        await new ODrive3(b).Axis1.RequestedState.SetAsync(AxisState.ClosedLoopControl);

        IReadOnlyList<EmergencyStopFailure> failures = await EmergencyStop.StopAsync([a, b]);

        failures.Should().BeEmpty();
        first.Get("axis0.current_state").Should().Be((byte)AxisState.Idle);
        first.Get("axis1.current_state").Should().Be((byte)AxisState.Idle);
        second.Get("axis1.current_state").Should().Be((byte)AxisState.Idle);
        first.Get("axis0.motor.is_armed").Should().Be(false);
    }

    [Fact]
    public async Task Stop_still_stops_the_other_boards_when_one_is_unreachable()
    {
        SimulatedODrive healthy = SimulatedODrive.CreateReference();
        var deadTransport = new SimulatedTransport(SimulatedODrive.CreateReference());
        ODriveDevice dead = await ODriveDevice.ConnectAsync(deadTransport);
        ODriveDevice alive = await ODriveDevice.ConnectAsync(new SimulatedTransport(healthy));
        await new ODrive3(alive).Axis0.RequestedState.SetAsync(AxisState.ClosedLoopControl);
        deadTransport.Disconnect();

        IReadOnlyList<EmergencyStopFailure> failures = await EmergencyStop.StopAsync([dead, alive]);

        healthy.Get("axis0.current_state").Should().Be((byte)AxisState.Idle);
        failures.Should().NotBeEmpty().And.OnlyContain(f => f.Device == dead);
    }

    [Fact]
    public async Task Stop_passes_before_requests_already_waiting()
    {
        SimulatedODrive board = SimulatedODrive.CreateReference();
        // Le transport simulé répond de façon synchrone et en quelques microsecondes : ce relais retient
        // les réponses le temps que la file se remplisse, comme une liaison occupée par une vraie rafale.
        var transport = new HeldTransport(new SimulatedTransport(board));
        ODriveDevice device = await ODriveDevice.ConnectAsync(transport);
        await new ODrive3(device).Axis0.RequestedState.SetAsync(AxisState.ClosedLoopControl);
        int readsBeforeStop = -1;
        int reads = 0;
        board.OnWrite("axis1.requested_state", _ => readsBeforeStop = Volatile.Read(ref reads));
        PropertyNode vbus = device.Schema.GetProperty("vbus_voltage");

        // Une rafale de lectures (comme l'acquisition des courbes) : la première attend sa réponse, les 499
        // autres sont en file quand l'arrêt est demandé.
        transport.Hold();
        Task[] burst = Enumerable.Range(0, 500).Select(async _ =>
        {
            await device.ReadAsync(vbus);
            Interlocked.Increment(ref reads);
        }).ToArray();
        Task<IReadOnlyList<EmergencyStopFailure>> stop = EmergencyStop.StopAsync([device]);
        transport.Release();
        await stop;
        await Task.WhenAll(burst);

        // Seule la lecture déjà sur le fil peut passer avant l'arrêt des deux axes.
        readsBeforeStop.Should().BeInRange(0, 1, "l'arrêt des deux axes doit passer devant toutes les lectures en file");
        board.Get("axis0.current_state").Should().Be((byte)AxisState.Idle);
    }

    private sealed class HeldTransport(ITransport inner) : ITransport
    {
        private TaskCompletionSource _gate = Open();

        public string Description => inner.Description;
        public bool IsConnected => inner.IsConnected;

        public event EventHandler? Disconnected
        {
            add => inner.Disconnected += value;
            remove => inner.Disconnected -= value;
        }

        /// <summary>Retient les réponses jusqu'à <see cref="Release"/>.</summary>
        public void Hold() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _gate.TrySetResult();

        public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct) => inner.SendAsync(packet, ct);

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct)
        {
            await _gate.Task;
            await Task.Yield();
            return await inner.ReceiveAsync(buffer, timeout, ct);
        }

        private static TaskCompletionSource Open()
        {
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            gate.SetResult();
            return gate;
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
