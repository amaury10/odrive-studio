using System.Buffers.Binary;
using System.Diagnostics;
using ODrive.Protocol.Transport;

namespace ODrive.Protocol;

/// <summary>Cache des arbres d'endpoints, indexé par <c>json_version_id</c> (SPEC §1).</summary>
public interface ISchemaCache
{
    EndpointSchema? TryGet(uint jsonVersionId);
    void Store(EndpointSchema schema);
}

/// <summary>Cache en mémoire, pour la durée de vie de l'application (reconnexions après reboot).</summary>
public sealed class MemorySchemaCache : ISchemaCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<uint, EndpointSchema> _schemas = [];

    public EndpointSchema? TryGet(uint jsonVersionId)
    {
        lock (_gate)
        {
            return _schemas.GetValueOrDefault(jsonVersionId);
        }
    }

    public void Store(EndpointSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        lock (_gate)
        {
            _schemas[schema.JsonVersionId] = schema;
        }
    }
}

public sealed record ODriveDeviceOptions
{
    /// <summary>Délai d'attente de la réponse à une requête (docs/PROTOCOL_NATIVE.md §Robustesse).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Nombre d'essais d'une requête rejouable (lecture, écriture) avant de déclarer la liaison perdue.</summary>
    public int MaxAttempts { get; init; } = 3;

    public ISchemaCache? SchemaCache { get; init; }

    /// <summary>Journal des écritures et des appels de fonction (docs/SAFETY.md).</summary>
    public IWriteJournal? Journal { get; init; }
}

/// <summary>
/// Une carte ODrive connectée : arbre d'objets lu sur l'endpoint 0, lecture, écriture et appel de fonction.
/// Une seule requête est en vol à la fois ; les appels concurrents sont mis en file.
/// Client de référence : <c>ref/ODrive/Firmware/fibre-cpp/legacy_object_client.cpp</c>.
/// </summary>
public sealed class ODriveDevice : IAsyncDisposable
{
    private const uint JsonVersionIdOffset = 0xFFFFFFFF;

    private readonly ITransport _transport;
    private readonly ODriveDeviceOptions _options;
    private readonly PriorityAsyncLock _gate = new();
    private readonly byte[] _txBuffer = new byte[PacketCodec.MaxPacketSize];
    private readonly byte[] _rxBuffer = new byte[PacketCodec.MaxPacketSize + 1];
    private ushort _seqNo;
    private int _lost;
    private EndpointSchema? _schema;

    private ODriveDevice(ITransport transport, ODriveDeviceOptions options)
    {
        _transport = transport;
        _options = options;
        _transport.Disconnected += OnTransportDisconnected;
    }

    /// <summary>Levé une seule fois quand la liaison est perdue. La carte doit alors être reconnectée.</summary>
    public event EventHandler? ConnectionLost;

    public EndpointSchema Schema => _schema ?? throw new InvalidOperationException("Carte non connectée.");
    public bool IsConnected => Volatile.Read(ref _lost) == 0 && _transport.IsConnected;
    public string TransportDescription => _transport.Description;

    /// <summary>Numéro de série (affiché en hexadécimal, comme odrivetool), 0 si la carte n'expose pas <c>serial_number</c>.</summary>
    public ulong SerialNumber { get; private set; }

    /// <summary>Ouvre la session : lit l'arbre d'endpoints (ou le reprend du cache) puis le numéro de série.</summary>
    public static async Task<ODriveDevice> ConnectAsync(ITransport transport, ODriveDeviceOptions? options = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transport);
        var device = new ODriveDevice(transport, options ?? new ODriveDeviceOptions());
        try
        {
            device._schema = await device.LoadSchemaAsync(ct).ConfigureAwait(false);
            if (device._schema.FindProperty("serial_number") is { } serial)
            {
                device.SerialNumber = Convert.ToUInt64(
                    await device.ReadAsync(serial, ct).ConfigureAwait(false),
                    System.Globalization.CultureInfo.InvariantCulture);
            }
            return device;
        }
        catch
        {
            transport.Disconnected -= device.OnTransportDisconnected;
            throw;
        }
    }

    /// <summary>Lit <c>json_version_id</c> en une requête (<c>legacy_protocol.cpp:305-307</c>).</summary>
    public async Task<uint> ReadJsonVersionIdAsync(CancellationToken ct = default)
    {
        await _gate.EnterAsync(RequestPriority.Normal, ct).ConfigureAwait(false);
        try
        {
            return await ReadJsonVersionIdCoreAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Exit();
        }
    }

    public async Task<object> ReadAsync(PropertyNode property, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(property);
        byte[] data = await ReadRawAsync(property, ct).ConfigureAwait(false);
        return EndpointValue.Decode(property.Type, data);
    }

    public async Task<T> ReadAsync<T>(PropertyNode property, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(property);
        byte[] data = await ReadRawAsync(property, ct).ConfigureAwait(false);
        return EndpointValue.Decode<T>(property.Type, data);
    }

    public Task<T> ReadAsync<T>(string path, CancellationToken ct = default) =>
        ReadAsync<T>(Schema.GetProperty(path), ct);

    public Task WriteAsync(PropertyNode property, object value, CancellationToken ct = default) =>
        WriteAsync(property, value, RequestPriority.Normal, ct);

    /// <summary>
    /// Écrit une propriété. En priorité <see cref="RequestPriority.Emergency"/>, l'écriture passe devant toutes
    /// les requêtes en attente et part sans lecture préalable de l'ancienne valeur.
    /// </summary>
    public async Task WriteAsync(PropertyNode property, object value, RequestPriority priority,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(property);
        if (!property.IsWritable)
        {
            throw new InvalidOperationException($"La propriété {property.Path} est en lecture seule.");
        }

        byte[] data = EndpointValue.Encode(property.Type, value);
        IWriteJournal? journal = _options.Journal;
        object? oldValue = null;
        await _gate.EnterAsync(priority, ct).ConfigureAwait(false);
        try
        {
            if (journal is not null && priority == RequestPriority.Normal)
            {
                oldValue = EndpointValue.Decode(property.Type, await ReadCoreAsync(property, ct).ConfigureAwait(false));
            }
            await WriteCoreAsync(property, data, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Exit();
        }

        journal?.Record(new WriteJournalEntry(DateTimeOffset.Now, SerialNumber, JournalEntryKind.Write,
            property.Path, oldValue, EndpointValue.Decode(property.Type, data)));
    }

    public Task WriteAsync(string path, object value, CancellationToken ct = default) =>
        WriteAsync(Schema.GetProperty(path), value, ct);

    /// <summary>
    /// Appelle une fonction : écrit chaque entrée, déclenche, lit chaque sortie
    /// (<c>legacy_object_client.cpp:673-684</c>). Les trois étapes ne sont pas atomiques côté carte : la file
    /// est tenue pendant tout l'appel pour que rien ne s'intercale.
    /// </summary>
    public async Task<IReadOnlyList<object>> CallAsync(FunctionNode function, IReadOnlyList<object> arguments,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(function);
        ArgumentNullException.ThrowIfNull(arguments);
        if (arguments.Count != function.Inputs.Count)
        {
            throw new ArgumentException(
                $"{function.Path} attend {function.Inputs.Count} argument(s), {arguments.Count} fourni(s).",
                nameof(arguments));
        }

        byte[][] inputs = new byte[arguments.Count][];
        for (int i = 0; i < inputs.Length; i++)
        {
            inputs[i] = EndpointValue.Encode(function.Inputs[i].Type, arguments[i]);
        }

        // Journalisé avant l'envoi : un appel qui fait redémarrer la carte ne revient pas normalement.
        _options.Journal?.Record(new WriteJournalEntry(DateTimeOffset.Now, SerialNumber, JournalEntryKind.Call,
            function.Path, null, string.Join(", ", arguments.Select(WriteJournalEntry.Format))));

        await _gate.EnterAsync(RequestPriority.Normal, ct).ConfigureAwait(false);
        try
        {
            for (int i = 0; i < inputs.Length; i++)
            {
                await WriteCoreAsync(function.Inputs[i], inputs[i], ct).ConfigureAwait(false);
            }

            // Le déclenchement n'est jamais rejoué : si sa réponse se perdait, la fonction serait exécutée
            // deux fois.
            await TransactAsync(function.Id, ReadOnlyMemory<byte>.Empty, 0, Schema.JsonCrc, attempts: 1, ct)
                .ConfigureAwait(false);

            object[] outputs = new object[function.Outputs.Count];
            for (int i = 0; i < outputs.Length; i++)
            {
                PropertyNode output = function.Outputs[i];
                outputs[i] = EndpointValue.Decode(output.Type, await ReadCoreAsync(output, ct).ConfigureAwait(false));
            }
            return outputs;
        }
        finally
        {
            _gate.Exit();
        }
    }

    public Task<IReadOnlyList<object>> CallAsync(string path, IReadOnlyList<object> arguments,
        CancellationToken ct = default) => CallAsync(Schema.GetFunction(path), arguments, ct);

    /// <summary>Redémarre la carte. La liaison est perdue : il faut attendre la ré-énumération et se reconnecter.</summary>
    public Task RebootAsync(CancellationToken ct = default) => CallExpectingRebootAsync("reboot", ct);

    /// <summary>Efface la configuration ; la carte redémarre (<c>Firmware/MotorControl/main.cpp:186-195</c>).</summary>
    public Task EraseConfigurationAsync(CancellationToken ct = default) =>
        CallExpectingRebootAsync("erase_configuration", ct);

    /// <summary>
    /// Sauvegarde la configuration. En cas de succès la carte redémarre sans répondre et la liaison est perdue ;
    /// si un axe est armé, elle refuse et répond <c>false</c> (<c>Firmware/MotorControl/main.cpp:159-184</c>).
    /// </summary>
    /// <returns><c>true</c> si la carte a redémarré, <c>false</c> si elle a refusé.</returns>
    public async Task<bool> SaveConfigurationAsync(CancellationToken ct = default)
    {
        try
        {
            IReadOnlyList<object> outputs = await CallAsync("save_configuration", [], ct).ConfigureAwait(false);
            return outputs.Count > 0 && outputs[0] is true;
        }
        catch (ODriveConnectionLostException)
        {
            return true;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _transport.Disconnected -= OnTransportDisconnected;
        Volatile.Write(ref _lost, 1);
        await _transport.DisposeAsync().ConfigureAwait(false);
    }

    private async Task CallExpectingRebootAsync(string functionPath, CancellationToken ct)
    {
        try
        {
            await CallAsync(functionPath, [], ct).ConfigureAwait(false);
        }
        catch (ODriveConnectionLostException)
        {
            // Attendu : la carte redémarre sans acquitter.
        }
    }

    private async Task<EndpointSchema> LoadSchemaAsync(CancellationToken ct)
    {
        await _gate.EnterAsync(RequestPriority.Normal, ct).ConfigureAwait(false);
        try
        {
            ISchemaCache? cache = _options.SchemaCache;
            if (cache is not null)
            {
                uint versionId = await ReadJsonVersionIdCoreAsync(ct).ConfigureAwait(false);
                if (cache.TryGet(versionId) is { } cached)
                {
                    return cached;
                }
            }

            // legacy_object_client.cpp:347-374 : lire par fragments jusqu'à une réponse vide.
            using var json = new MemoryStream();
            byte[] offset = new byte[4];
            while (true)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(offset, (uint)json.Length);
                byte[] fragment = await TransactAsync(0, offset, PacketCodec.MaxResponsePayloadSize,
                    PacketCodec.ProtocolVersion, _options.MaxAttempts, ct).ConfigureAwait(false);
                if (fragment.Length == 0)
                {
                    break;
                }
                json.Write(fragment);
            }

            EndpointSchema schema;
            try
            {
                schema = EndpointSchema.Parse(json.GetBuffer().AsSpan(0, (int)json.Length));
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or KeyNotFoundException)
            {
                throw new ODriveProtocolException("Le JSON d'endpoints lu sur la carte est invalide.", e);
            }

            cache?.Store(schema);
            return schema;
        }
        finally
        {
            _gate.Exit();
        }
    }

    private async Task<uint> ReadJsonVersionIdCoreAsync(CancellationToken ct)
    {
        byte[] offset = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(offset, JsonVersionIdOffset);
        byte[] response = await TransactAsync(0, offset, 4, PacketCodec.ProtocolVersion, _options.MaxAttempts, ct)
            .ConfigureAwait(false);
        if (response.Length != 4)
        {
            throw new ODriveProtocolException($"json_version_id : 4 octets attendus, {response.Length} reçus.");
        }
        return BinaryPrimitives.ReadUInt32LittleEndian(response);
    }

    private async Task<byte[]> ReadRawAsync(PropertyNode property, CancellationToken ct)
    {
        await _gate.EnterAsync(RequestPriority.Normal, ct).ConfigureAwait(false);
        try
        {
            return await ReadCoreAsync(property, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Exit();
        }
    }

    private async Task<byte[]> ReadCoreAsync(PropertyNode property, CancellationToken ct)
    {
        int size = EndpointValue.SizeOf(property.Type);
        byte[] data = await TransactAsync(property.Id, ReadOnlyMemory<byte>.Empty, size, Schema.JsonCrc,
            _options.MaxAttempts, ct).ConfigureAwait(false);
        if (data.Length != size)
        {
            throw new ODriveProtocolException(
                $"Lecture de {property.Path} : {size} octet(s) attendu(s), {data.Length} reçu(s).");
        }
        return data;
    }

    private async Task WriteCoreAsync(PropertyNode property, byte[] data, CancellationToken ct) =>
        await TransactAsync(property.Id, data, 0, Schema.JsonCrc, _options.MaxAttempts, ct).ConfigureAwait(false);

    /// <summary>Envoie une requête et attend sa réponse. À appeler en tenant <see cref="_gate"/>.</summary>
    private async Task<byte[]> TransactAsync(ushort endpointId, ReadOnlyMemory<byte> payload, int responseSize,
        ushort trailer, int attempts, CancellationToken ct)
    {
        if (!IsConnected)
        {
            MarkLost();
            throw new ODriveConnectionLostException();
        }

        try
        {
            for (int attempt = 0; attempt < attempts; attempt++)
            {
                _seqNo = PacketCodec.NextSequenceNumber(_seqNo);
                ushort seqNo = _seqNo;
                int length = PacketCodec.EncodeRequest(_txBuffer, seqNo, endpointId, (ushort)responseSize,
                    payload.Span, trailer);
                await _transport.SendAsync(_txBuffer.AsMemory(0, length), ct).ConfigureAwait(false);

                long start = Stopwatch.GetTimestamp();
                while (true)
                {
                    TimeSpan remaining = _options.RequestTimeout - Stopwatch.GetElapsedTime(start);
                    if (remaining <= TimeSpan.Zero)
                    {
                        break;
                    }

                    int received = await _transport.ReceiveAsync(_rxBuffer, remaining, ct).ConfigureAwait(false);
                    if (received == 0)
                    {
                        break;
                    }

                    // Une réponse dont le seq_no n'est pas celui de la requête en cours (réponse tardive d'un
                    // essai précédent) est jetée.
                    if (PacketCodec.TryDecodeResponse(_rxBuffer.AsSpan(0, received), out ushort responseSeqNo,
                            out ReadOnlySpan<byte> responsePayload) && responseSeqNo == seqNo)
                    {
                        return responsePayload.ToArray();
                    }
                }
            }
        }
        catch (ODriveConnectionLostException)
        {
            MarkLost();
            throw;
        }

        MarkLost();
        throw new ODriveConnectionLostException(
            $"Pas de réponse de la carte après {attempts} essai(s) (endpoint {endpointId}).");
    }

    private void OnTransportDisconnected(object? sender, EventArgs e) => MarkLost();

    private void MarkLost()
    {
        if (Interlocked.Exchange(ref _lost, 1) == 0)
        {
            ConnectionLost?.Invoke(this, EventArgs.Empty);
        }
    }
}

/// <summary>Propriété distante typée : brique des wrappers générés (<c>odrv.Axis0.Motor.Config.PolePairs</c>).</summary>
/// <typeparam name="T">Type primitif, enum généré ou <see cref="EndpointRef"/>.</typeparam>
public sealed class RemoteProperty<T>
{
    private readonly ODriveDevice _device;
    private PropertyNode? _node;

    public RemoteProperty(ODriveDevice device, string path)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(path);
        _device = device;
        Path = path;
    }

    public string Path { get; }

    /// <summary>Description de l'endpoint sur la carte connectée ; lève une exception si la carte ne l'expose pas.</summary>
    public PropertyNode Node => _node ??= _device.Schema.GetProperty(Path);

    public Task<T> GetAsync(CancellationToken ct = default) => _device.ReadAsync<T>(Node, ct);

    public Task SetAsync(T value, CancellationToken ct = default) => _device.WriteAsync(Node, value!, ct);
}
