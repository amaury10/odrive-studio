using System.Text.Json;

namespace ODrive.Protocol;

/// <summary>Élément de l'arbre décrit par le JSON d'endpoints.</summary>
public abstract class EndpointNode
{
    private protected EndpointNode(string name, string path)
    {
        Name = name;
        Path = path;
    }

    public string Name { get; }

    /// <summary>Chemin pointé depuis la racine, comme dans odrivetool (« axis0.motor.config.pole_pairs »).</summary>
    public string Path { get; }

    public override string ToString() => Path;
}

public sealed class ObjectNode : EndpointNode
{
    internal ObjectNode(string name, string path, IReadOnlyList<EndpointNode> members) : base(name, path)
    {
        Members = members;
    }

    public IReadOnlyList<EndpointNode> Members { get; }
}

public sealed class PropertyNode : EndpointNode
{
    internal PropertyNode(string name, string path, ushort id, EndpointValueType type, bool isWritable)
        : base(name, path)
    {
        Id = id;
        Type = type;
        IsWritable = isWritable;
    }

    public ushort Id { get; }
    public EndpointValueType Type { get; }
    public bool IsWritable { get; }
}

public sealed class FunctionNode : EndpointNode
{
    internal FunctionNode(string name, string path, ushort id,
        IReadOnlyList<PropertyNode> inputs, IReadOnlyList<PropertyNode> outputs) : base(name, path)
    {
        Id = id;
        Inputs = inputs;
        Outputs = outputs;
    }

    public ushort Id { get; }

    /// <summary>Arguments d'entrée : chacun est un endpoint à écrire avant de déclencher la fonction.</summary>
    public IReadOnlyList<PropertyNode> Inputs { get; }

    /// <summary>Valeurs de retour : chacune est un endpoint à lire après le déclenchement.</summary>
    public IReadOnlyList<PropertyNode> Outputs { get; }
}

/// <summary>
/// Arbre d'objets d'une carte, construit depuis le JSON lu sur l'endpoint 0. Structure du JSON :
/// <c>ref/ODrive/tools/fibre-tools/interface_generator.py:462-541</c> (voir docs/PROTOCOL_NATIVE.md §Structure du JSON).
/// </summary>
public sealed class EndpointSchema
{
    private readonly Dictionary<string, PropertyNode> _propertiesByPath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FunctionNode> _functionsByPath = new(StringComparer.Ordinal);
    private readonly List<PropertyNode> _properties = [];
    private readonly List<FunctionNode> _functions = [];
    private readonly byte[] _json;

    private EndpointSchema(byte[] json)
    {
        _json = json;
        // CRC16 du JSON, init = version du protocole (endpoints_template.j2:33) : trailer de toutes les
        // requêtes hors endpoint 0.
        JsonCrc = Crc.Crc16(PacketCodec.ProtocolVersion, json);
        // endpoints_template.j2:34
        JsonVersionId = ((uint)JsonCrc << 16) | Crc.Crc16(JsonCrc, json);

        using JsonDocument document = JsonDocument.Parse(json);
        Root = new ObjectNode("", "", ParseMembers(document.RootElement, ""));
    }

    public ObjectNode Root { get; }
    public ushort JsonCrc { get; }

    /// <summary>Clé de cache : valeur renvoyée par l'endpoint 0 pour l'offset 0xFFFFFFFF (<c>legacy_protocol.cpp:305-307</c>).</summary>
    public uint JsonVersionId { get; }

    public ReadOnlyMemory<byte> Json => _json;

    /// <summary>Toutes les propriétés, dans l'ordre du JSON (hors arguments de fonctions).</summary>
    public IReadOnlyList<PropertyNode> Properties => _properties;

    public IReadOnlyList<FunctionNode> Functions => _functions;

    public static EndpointSchema Parse(ReadOnlySpan<byte> json) => new(json.ToArray());

    public PropertyNode? FindProperty(string path) => _propertiesByPath.GetValueOrDefault(path);

    public FunctionNode? FindFunction(string path) => _functionsByPath.GetValueOrDefault(path);

    public PropertyNode GetProperty(string path) =>
        FindProperty(path) ?? throw new KeyNotFoundException($"Propriété absente de la carte : {path}");

    public FunctionNode GetFunction(string path) =>
        FindFunction(path) ?? throw new KeyNotFoundException($"Fonction absente de la carte : {path}");

    private List<EndpointNode> ParseMembers(JsonElement array, string prefix)
    {
        var members = new List<EndpointNode>();
        foreach (JsonElement element in array.EnumerateArray())
        {
            string name = element.GetProperty("name").GetString() ?? "";
            string type = element.GetProperty("type").GetString() ?? "";
            string path = prefix + name;

            switch (type)
            {
                case "json":
                    // Premier élément du JSON : l'endpoint 0 lui-même.
                    break;

                case "object":
                    members.Add(new ObjectNode(name, path, ParseMembers(element.GetProperty("members"), path + ".")));
                    break;

                case "function":
                    var function = new FunctionNode(name, path, ReadId(element),
                        ParseArguments(element, "inputs", path), ParseArguments(element, "outputs", path));
                    members.Add(function);
                    _functions.Add(function);
                    _functionsByPath[path] = function;
                    break;

                default:
                    PropertyNode property = ParseProperty(element, name, path);
                    members.Add(property);
                    _properties.Add(property);
                    _propertiesByPath[path] = property;
                    break;
            }
        }
        return members;
    }

    private static List<PropertyNode> ParseArguments(JsonElement function, string key, string functionPath)
    {
        var arguments = new List<PropertyNode>();
        if (function.TryGetProperty(key, out JsonElement array))
        {
            foreach (JsonElement element in array.EnumerateArray())
            {
                string name = element.GetProperty("name").GetString() ?? "";
                arguments.Add(ParseProperty(element, name, functionPath + "." + name));
            }
        }
        return arguments;
    }

    private static PropertyNode ParseProperty(JsonElement element, string name, string path)
    {
        string typeName = element.GetProperty("type").GetString() ?? "";
        if (!EndpointValue.TryParseTypeName(typeName, out EndpointValueType type))
        {
            throw new FormatException($"Type d'endpoint inconnu « {typeName} » pour {path}.");
        }

        bool writable = element.TryGetProperty("access", out JsonElement access)
            && (access.GetString() ?? "").Contains('w', StringComparison.Ordinal);
        return new PropertyNode(name, path, ReadId(element), type, writable);
    }

    private static ushort ReadId(JsonElement element) => element.GetProperty("id").GetUInt16();
}
