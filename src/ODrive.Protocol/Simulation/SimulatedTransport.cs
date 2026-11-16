using System.Buffers.Binary;
using System.Threading.Channels;
using ODrive.Protocol.Transport;

namespace ODrive.Protocol.Simulation;

/// <summary>
/// Transport en mémoire vers une <see cref="SimulatedODrive"/>. Un reboot de la carte simulée coupe le
/// transport, comme une ré-énumération USB : il faut en créer un nouveau pour se reconnecter.
/// </summary>
public sealed class SimulatedTransport : ITransport
{
    private readonly SimulatedODrive _device;
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>();
    private int _connected = 1;
    private int _requestCount;
    private int _endpoint0RequestCount;
    private int _responsesToDrop;

    public SimulatedTransport(SimulatedODrive device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _device = device;
        _device.Rebooted += OnDeviceRebooted;
    }

    public event EventHandler? Disconnected;

    public string Description => "ODrive simulé";
    public bool IsConnected => Volatile.Read(ref _connected) == 1;

    /// <summary>Nombre de paquets envoyés à la carte simulée.</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>Nombre de requêtes sur l'endpoint 0 (JSON et identifiant de version).</summary>
    public int Endpoint0RequestCount => Volatile.Read(ref _endpoint0RequestCount);

    /// <summary>Injection de défaut : nombre de réponses à perdre.</summary>
    public int ResponsesToDrop
    {
        get => Volatile.Read(ref _responsesToDrop);
        set => Volatile.Write(ref _responsesToDrop, value);
    }

    /// <summary>Injection de défaut : fait arriver un paquet que la carte n'a pas émis en réponse à la requête en cours.</summary>
    public void InjectIncoming(byte[] packet) => _incoming.Writer.TryWrite(packet);

    /// <summary>Simule un débranchement.</summary>
    public void Disconnect()
    {
        if (Interlocked.Exchange(ref _connected, 0) == 1)
        {
            _device.Rebooted -= OnDeviceRebooted;
            _incoming.Writer.TryComplete();
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!IsConnected)
        {
            throw new ODriveConnectionLostException();
        }

        Interlocked.Increment(ref _requestCount);
        if (packet.Length >= 4 && (BinaryPrimitives.ReadUInt16LittleEndian(packet.Span[2..]) & 0x7FFF) == 0)
        {
            Interlocked.Increment(ref _endpoint0RequestCount);
        }

        byte[]? response = _device.HandlePacket(packet.Span);
        if (response is not null)
        {
            if (_responsesToDrop > 0)
            {
                Interlocked.Decrement(ref _responsesToDrop);
            }
            else
            {
                _incoming.Writer.TryWrite(response);
            }
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct)
    {
        byte[] packet;
        if (!_incoming.Reader.TryRead(out packet!))
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutSource.CancelAfter(timeout);
            try
            {
                packet = await _incoming.Reader.ReadAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return 0;
            }
            catch (ChannelClosedException)
            {
                throw new ODriveConnectionLostException();
            }
        }

        packet.CopyTo(buffer);
        return packet.Length;
    }

    public ValueTask DisposeAsync()
    {
        Disconnect();
        return ValueTask.CompletedTask;
    }

    private void OnDeviceRebooted(object? sender, EventArgs e) => Disconnect();
}
