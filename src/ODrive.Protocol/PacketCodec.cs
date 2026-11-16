using System.Buffers.Binary;

namespace ODrive.Protocol;

/// <summary>
/// Encodage et décodage des paquets du protocole natif (fibre « legacy »), little-endian.
/// Client de référence : <c>ref/ODrive/Firmware/fibre-cpp/legacy_protocol.cpp:230-248</c> ;
/// côté carte : <c>legacy_protocol.cpp:411-501</c>.
/// </summary>
public static class PacketCodec
{
    /// <summary>Version du protocole : trailer des requêtes sur l'endpoint 0 et init du CRC du JSON (<c>legacy_protocol.hpp:30</c>).</summary>
    public const ushort ProtocolVersion = 1;

    /// <summary>Taille maximale d'un paquet USB dans chaque sens (<c>Firmware/communication/interface_usb.cpp:65-74,149</c>).</summary>
    public const int MaxPacketSize = 63;

    /// <summary>seq_no (2) + endpoint_id (2) + expected_response_size (2).</summary>
    public const int RequestHeaderSize = 6;

    public const int TrailerSize = 2;

    /// <summary>Charge utile maximale d'une requête : 63 − 6 − 2.</summary>
    public const int MaxRequestPayloadSize = MaxPacketSize - RequestHeaderSize - TrailerSize;

    /// <summary>Charge utile maximale d'une réponse : 63 − 2 (<c>legacy_protocol.cpp:486-487</c>).</summary>
    public const int MaxResponsePayloadSize = MaxPacketSize - 2;

    private const ushort ResponseFlag = 0x8000;

    /// <summary>
    /// Écrit une requête dans <paramref name="destination"/> et renvoie sa longueur. Une réponse est toujours
    /// demandée (bit 15 de endpoint_id), comme libfibre (<c>legacy_protocol.cpp:232</c>) : pour une écriture,
    /// la réponse vide sert d'accusé de réception.
    /// </summary>
    /// <param name="trailer"><see cref="ProtocolVersion"/> pour l'endpoint 0, CRC16 du JSON pour les autres (<c>legacy_protocol.cpp:473</c>).</param>
    public static int EncodeRequest(Span<byte> destination, ushort seqNo, ushort endpointId,
        ushort expectedResponseSize, ReadOnlySpan<byte> payload, ushort trailer)
    {
        // Bit 15 à 1 : la carte prendrait la requête pour un ACK (legacy_protocol.cpp:419).
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(seqNo, ResponseFlag);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(endpointId, ResponseFlag);
        if (payload.Length > MaxRequestPayloadSize)
        {
            throw new ArgumentException(
                $"Payload de {payload.Length} octets : le maximum est {MaxRequestPayloadSize}.", nameof(payload));
        }

        int length = RequestHeaderSize + payload.Length + TrailerSize;
        if (destination.Length < length)
        {
            throw new ArgumentException("Tampon de destination trop petit.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt16LittleEndian(destination, seqNo);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[2..], (ushort)(endpointId | ResponseFlag));
        BinaryPrimitives.WriteUInt16LittleEndian(destination[4..], expectedResponseSize);
        payload.CopyTo(destination[RequestHeaderSize..]);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[(RequestHeaderSize + payload.Length)..], trailer);
        return length;
    }

    /// <summary>
    /// Numéro de séquence suivant : compteur sur 15 bits, bit 7 forcé à 1 pour ne pas être confondu avec de
    /// l'ASCII côté carte (<c>legacy_protocol.cpp:205-208</c>).
    /// </summary>
    public static ushort NextSequenceNumber(ushort previous) =>
        (ushort)(((previous + 1) & 0x7FFF) | 0x0080);

    /// <summary>Décode une réponse : <c>seq_no | 0x8000</c> suivi du payload (<c>legacy_protocol.cpp:494-497</c>).</summary>
    /// <param name="seqNo">Numéro de séquence de la requête d'origine (bit 15 retiré).</param>
    public static bool TryDecodeResponse(ReadOnlySpan<byte> packet, out ushort seqNo, out ReadOnlySpan<byte> payload)
    {
        seqNo = 0;
        payload = default;
        if (packet.Length < 2)
        {
            return false;
        }

        ushort raw = BinaryPrimitives.ReadUInt16LittleEndian(packet);
        if ((raw & ResponseFlag) == 0)
        {
            return false;
        }

        seqNo = (ushort)(raw & 0x7FFF);
        payload = packet[2..];
        return true;
    }
}
