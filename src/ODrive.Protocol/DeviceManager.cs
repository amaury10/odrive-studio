using ODrive.Protocol.Simulation;
using ODrive.Protocol.Transport;
using ODrive.Protocol.Transport.WinUsb;

namespace ODrive.Protocol;

/// <summary>Une carte vue par une source, pas encore ouverte.</summary>
/// <param name="Key">Identifiant stable tant que la carte reste branchée (chemin d'interface USB).</param>
public sealed record TransportCandidate(string Key, string Description, Func<ITransport> Open);

/// <summary>Source de cartes : USB (WinUSB), carte simulée…</summary>
public interface ITransportSource
{
    IReadOnlyList<TransportCandidate> Enumerate();
}

public sealed class WinUsbTransportSource : ITransportSource
{
    public IReadOnlyList<TransportCandidate> Enumerate() =>
        WinUsbEnumerator.Enumerate()
            .Select(info => new TransportCandidate(info.DevicePath, "USB", () => WinUsbTransport.Open(info)))
            .ToList();
}

public sealed class SimulatedTransportSource(SimulatedODrive board, string key = "sim") : ITransportSource
{
    public SimulatedODrive Board { get; } = board;

    /// <summary>Faux pour simuler une carte débranchée.</summary>
    public bool IsPresent { get; set; } = true;

    public IReadOnlyList<TransportCandidate> Enumerate() =>
        IsPresent ? [new TransportCandidate(key, "ODrive simulé", () => new SimulatedTransport(Board))] : [];
}

/// <summary>
/// Tient la liste des cartes connectées (plusieurs en parallèle) : chaque balayage ouvre les cartes nouvelles,
/// et une carte dont la liaison est perdue (débranchement, reboot, save_configuration) est retirée puis
/// reconnectée au balayage suivant, sans relire le JSON s'il est en cache.
/// </summary>
public sealed class DeviceManager : IAsyncDisposable
{
    private readonly IReadOnlyList<ITransportSource> _sources;
    private readonly ODriveDeviceOptions _options;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, ODriveDevice> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _lastErrors = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private CancellationTokenSource? _loopCancellation;
    private Task? _loop;

    public DeviceManager(IEnumerable<ITransportSource> sources, ODriveDeviceOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = [.. sources];
        options ??= new ODriveDeviceOptions();
        _options = options.SchemaCache is null ? options with { SchemaCache = new MemorySchemaCache() } : options;
    }

    /// <summary>Levés sur le fil du balayage ou de la requête qui a constaté la perte, pas sur celui de l'interface.</summary>
    public event EventHandler<ODriveDevice>? DeviceConnected;
    public event EventHandler<ODriveDevice>? DeviceDisconnected;

    public IReadOnlyList<ODriveDevice> Devices
    {
        get
        {
            lock (_gate)
            {
                return [.. _devices.Values];
            }
        }
    }

    /// <summary>Dernière erreur d'ouverture par carte vue mais non connectée (clé de la source).</summary>
    public IReadOnlyDictionary<string, string> LastErrors
    {
        get
        {
            lock (_gate)
            {
                return new Dictionary<string, string>(_lastErrors);
            }
        }
    }

    /// <summary>Un balayage : ouvre et connecte les cartes vues par les sources qui ne le sont pas encore.</summary>
    public async Task ScanAsync(CancellationToken ct = default)
    {
        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (TransportCandidate candidate in _sources.SelectMany(s => s.Enumerate()))
            {
                seen.Add(candidate.Key);
                bool known;
                lock (_gate)
                {
                    known = _devices.ContainsKey(candidate.Key);
                }
                if (!known)
                {
                    await TryConnectAsync(candidate, ct).ConfigureAwait(false);
                }
            }

            lock (_gate)
            {
                foreach (string key in _lastErrors.Keys.Where(k => !seen.Contains(k)).ToList())
                {
                    _lastErrors.Remove(key);
                }
            }
        }
        finally
        {
            _scanGate.Release();
        }
    }

    /// <summary>Lance le balayage périodique en arrière-plan.</summary>
    public void Start(TimeSpan interval)
    {
        if (_loop is not null)
        {
            return;
        }

        _loopCancellation = new CancellationTokenSource();
        CancellationToken ct = _loopCancellation.Token;
        _loop = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await ScanAsync(ct).ConfigureAwait(false);
                    await Task.Delay(interval, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
#pragma warning disable CA1031 // Le balayage ne doit jamais s'arrêter : une erreur d'énumération est retentée au tour suivant.
                catch (Exception)
#pragma warning restore CA1031
                {
                    await Task.Delay(interval, CancellationToken.None).ConfigureAwait(false);
                }
            }
        }, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_loopCancellation is not null)
        {
            await _loopCancellation.CancelAsync().ConfigureAwait(false);
            if (_loop is not null)
            {
                await _loop.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
            _loopCancellation.Dispose();
        }

        List<ODriveDevice> devices;
        lock (_gate)
        {
            devices = [.. _devices.Values];
            _devices.Clear();
        }
        foreach (ODriveDevice device in devices)
        {
            await device.DisposeAsync().ConfigureAwait(false);
        }
        _scanGate.Dispose();
    }

    private async Task TryConnectAsync(TransportCandidate candidate, CancellationToken ct)
    {
        ITransport? transport = null;
        try
        {
            transport = candidate.Open();
            ODriveDevice device = await ODriveDevice.ConnectAsync(transport, _options, ct).ConfigureAwait(false);
            lock (_gate)
            {
                _devices[candidate.Key] = device;
                _lastErrors.Remove(candidate.Key);
            }
            device.ConnectionLost += (_, _) => OnConnectionLost(candidate.Key, device);
            DeviceConnected?.Invoke(this, device);

            // La liaison a pu tomber entre la connexion et l'abonnement.
            if (!device.IsConnected)
            {
                OnConnectionLost(candidate.Key, device);
            }
        }
        catch (Exception e) when (e is IOException or ODriveProtocolException)
        {
            // Carte en cours de ré-énumération, ou interface déjà ouverte par une autre application.
            lock (_gate)
            {
                _lastErrors[candidate.Key] = e.Message;
            }
            if (transport is not null)
            {
                await transport.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void OnConnectionLost(string key, ODriveDevice device)
    {
        lock (_gate)
        {
            if (!_devices.TryGetValue(key, out ODriveDevice? current) || !ReferenceEquals(current, device))
            {
                return;
            }
            _devices.Remove(key);
        }

        DeviceDisconnected?.Invoke(this, device);
        // Libère le transport hors du fil qui a signalé la perte (il peut être le fil de lecture USB).
        _ = Task.Run(async () => await device.DisposeAsync().ConfigureAwait(false));
    }
}
