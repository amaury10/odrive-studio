using ODrive.Protocol;

namespace ODrive.Model;

/// <summary>
/// Base des wrappers typés générés (<c>Generated/Wrappers.g.cs</c>) : un objet de l'arbre de la carte, à un
/// chemin donné. Les propriétés ne sont résolues dans l'arbre de la carte connectée qu'à leur premier usage.
/// </summary>
public abstract class RemoteObject
{
    protected RemoteObject(ODriveDevice device, string path)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(path);
        Device = device;
        Path = path;
    }

    public ODriveDevice Device { get; }

    /// <summary>Chemin de l'objet depuis la racine (« axis0.motor »), vide pour la racine.</summary>
    public string Path { get; }

    protected string ChildPath(string name) => Path.Length == 0 ? name : Path + "." + name;

    protected RemoteProperty<T> Property<T>(string name) => new(Device, ChildPath(name));

    protected Task<IReadOnlyList<object>> CallAsync(string name, IReadOnlyList<object> arguments, CancellationToken ct) =>
        Device.CallAsync(ChildPath(name), arguments, ct);

    /// <summary>Convertit une sortie de fonction (primitif du type JSON) vers le type du wrapper.</summary>
    protected static T Output<T>(IReadOnlyList<object> outputs, int index)
    {
        ArgumentNullException.ThrowIfNull(outputs);
        object value = outputs[index];
        return value is T typed ? typed
            : typeof(T).IsEnum ? (T)Enum.ToObject(typeof(T), value)
            : (T)Convert.ChangeType(value, typeof(T), System.Globalization.CultureInfo.InvariantCulture);
    }
}
