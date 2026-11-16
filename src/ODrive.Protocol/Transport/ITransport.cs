namespace ODrive.Protocol.Transport;

/// <summary>Transport d'un paquet du protocole natif (un transfert bulk USB = un paquet).</summary>
public interface ITransport : IAsyncDisposable
{
    string Description { get; }
    bool IsConnected { get; }

    /// <summary>Levé une seule fois quand le transport est perdu (débranchement, reboot de la carte).</summary>
    event EventHandler? Disconnected;

    /// <summary>Envoie un paquet. Lève <see cref="ODriveConnectionLostException"/> si le transport est perdu.</summary>
    ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct);

    /// <summary>
    /// Reçoit un paquet. Renvoie sa longueur, ou 0 si rien n'est arrivé avant <paramref name="timeout"/>.
    /// Lève <see cref="ODriveConnectionLostException"/> si le transport est perdu.
    /// </summary>
    ValueTask<int> ReceiveAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct);
}
