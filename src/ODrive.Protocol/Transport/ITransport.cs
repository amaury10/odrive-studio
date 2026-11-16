namespace ODrive.Protocol.Transport;

/// <summary>Transport d'un paquet du protocole natif (un transfert bulk USB = un paquet).</summary>
public interface ITransport : IAsyncDisposable
{
    string Description { get; }
    bool IsConnected { get; }
    event EventHandler? Disconnected;

    ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct);
    ValueTask<int> ReceiveAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct);
}
