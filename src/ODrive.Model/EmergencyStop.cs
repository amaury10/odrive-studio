using ODrive.Protocol;

namespace ODrive.Model;

/// <summary>Une demande d'arrêt qui n'a pas pu être transmise.</summary>
public sealed record EmergencyStopFailure(ODriveDevice Device, string Path, Exception Error);

/// <summary>
/// Arrêt d'urgence (docs/SAFETY.md) : <c>requested_state = IDLE</c> sur tous les axes de toutes les cartes,
/// en tête de la file de requêtes de chaque carte.
/// </summary>
public static class EmergencyStop
{
    /// <summary>
    /// Demande l'arrêt de toutes les cartes en parallèle. Ne lève pas d'exception : une carte injoignable
    /// n'empêche pas d'arrêter les autres ; les échecs sont renvoyés.
    /// </summary>
    public static async Task<IReadOnlyList<EmergencyStopFailure>> StopAsync(IEnumerable<ODriveDevice> devices,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(devices);
        EmergencyStopFailure[][] results = await Task.WhenAll(devices.Select(d => StopDeviceAsync(d, ct)))
            .ConfigureAwait(false);
        return results.SelectMany(r => r).ToList();
    }

    private static async Task<EmergencyStopFailure[]> StopDeviceAsync(ODriveDevice device, CancellationToken ct)
    {
        var failures = new List<EmergencyStopFailure>();
        // Tous les axes présents dans l'arbre de la carte (axis0, axis1 sur v3.x).
        IEnumerable<PropertyNode> targets = device.Schema.Root.Members.OfType<ObjectNode>()
            .Select(axis => device.Schema.FindProperty(axis.Path + ".requested_state"))
            .OfType<PropertyNode>();

        foreach (PropertyNode requestedState in targets)
        {
            try
            {
                await device.WriteAsync(requestedState, AxisState.Idle, RequestPriority.Emergency, ct)
                    .ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Un arrêt d'urgence ne doit jamais s'interrompre sur une exception.
            catch (Exception e)
#pragma warning restore CA1031
            {
                failures.Add(new EmergencyStopFailure(device, requestedState.Path, e));
            }
        }
        return [.. failures];
    }
}
