using System.Buffers.Binary;
using System.Reflection;

namespace ODrive.Protocol.Simulation;

/// <summary>Contexte d'un appel de fonction simulé : entrées déjà écrites par le client, sorties à publier.</summary>
public sealed class SimulatedCall
{
    private readonly SimulatedODrive _device;
    private readonly FunctionNode _function;

    internal SimulatedCall(SimulatedODrive device, FunctionNode function)
    {
        _device = device;
        _function = function;
    }

    public SimulatedODrive Device => _device;

    /// <summary>Si vrai, la carte ne répond pas à la requête (elle redémarre pendant l'appel).</summary>
    public bool SuppressResponse { get; set; }

    public object Input(string name) => _device.Get(_function.Path + "." + name);

    public void SetOutput(string name, object value) => _device.Set(_function.Path + "." + name, value);
}

/// <summary>
/// Carte ODrive simulée : serveur d'objets en mémoire qui répond aux paquets comme le firmware 0.5.6
/// (<c>ref/ODrive/Firmware/fibre-cpp/legacy_protocol.cpp:411-501</c> et <c>:298-318</c>). Sert au développement
/// sans matériel et aux tests ; le modèle moteur vient se greffer dessus par <see cref="OnCall"/> et
/// <see cref="Get"/> / <see cref="Set"/>.
/// </summary>
public sealed class SimulatedODrive
{
    private const string ReferenceJsonResource = "ODrive.Protocol.Simulation.odrive-v3.6-0.5.6.json";

    private readonly Lock _gate = new();
    private readonly Dictionary<ushort, PropertyNode> _endpoints = [];
    private readonly Dictionary<ushort, FunctionNode> _functions = [];
    private readonly Dictionary<ushort, byte[]> _values = [];
    private readonly Dictionary<ushort, byte[]> _defaults = [];
    private readonly Dictionary<string, Action<SimulatedCall>> _handlers = new(StringComparer.Ordinal);
    private Dictionary<ushort, byte[]> _saved = [];

    public SimulatedODrive(ReadOnlySpan<byte> json)
    {
        Schema = EndpointSchema.Parse(json);

        foreach (PropertyNode property in Schema.Properties)
        {
            _endpoints[property.Id] = property;
        }
        foreach (FunctionNode function in Schema.Functions)
        {
            _functions[function.Id] = function;
            foreach (PropertyNode argument in function.Inputs.Concat(function.Outputs))
            {
                _endpoints[argument.Id] = argument;
            }
        }
        foreach (PropertyNode endpoint in _endpoints.Values)
        {
            _values[endpoint.Id] = new byte[EndpointValue.SizeOf(endpoint.Type)];
        }

        foreach ((string path, object value) in SimulatedDefaults.Values)
        {
            if (FindEndpoint(path) is { } endpoint)
            {
                _values[endpoint.Id] = EndpointValue.Encode(endpoint.Type, value);
            }
        }
        foreach ((ushort id, byte[] value) in _values)
        {
            _defaults[id] = (byte[])value.Clone();
        }

        RegisterBuiltInFunctions();
    }

    /// <summary>Levé quand la carte simulée redémarre (reboot, save_configuration, erase_configuration).</summary>
    public event EventHandler? Rebooted;

    public EndpointSchema Schema { get; }

    /// <summary>Carte simulée v3.6 en firmware 0.5.6, avec le JSON d'endpoints de référence.</summary>
    public static SimulatedODrive CreateReference()
    {
        using Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ReferenceJsonResource)
            ?? throw new InvalidOperationException($"Ressource {ReferenceJsonResource} introuvable.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return new SimulatedODrive(memory.ToArray());
    }

    /// <summary>
    /// Carte simulée qui démarre dans l'état d'une vraie carte (<c>ODrive.Cli dump</c>) : même JSON d'endpoints,
    /// et les valeurs du dump comme valeurs au démarrage.
    /// </summary>
    public static SimulatedODrive FromDump(DeviceDump dump)
    {
        ArgumentNullException.ThrowIfNull(dump);
        var device = new SimulatedODrive(dump.EndpointJson.Span);
        foreach ((string path, object value) in dump.Values)
        {
            if (device.Schema.FindProperty(path) is { } property)
            {
                byte[] data = EndpointValue.Encode(property.Type, value);
                device._values[property.Id] = data;
                device._defaults[property.Id] = (byte[])data.Clone();
            }
        }
        return device;
    }

    /// <summary>Lit une valeur par son chemin (propriété, ou argument de fonction « fonction.argument »).</summary>
    public object Get(string path)
    {
        PropertyNode endpoint = GetEndpoint(path);
        lock (_gate)
        {
            return EndpointValue.Decode(endpoint.Type, _values[endpoint.Id]);
        }
    }

    /// <summary>Écrit une valeur côté carte, sans contrôle d'accès (c'est le firmware simulé qui écrit).</summary>
    public void Set(string path, object value)
    {
        PropertyNode endpoint = GetEndpoint(path);
        byte[] data = EndpointValue.Encode(endpoint.Type, value);
        lock (_gate)
        {
            _values[endpoint.Id] = data;
        }
    }

    /// <summary>Définit le comportement d'une fonction. Sans gestionnaire, l'appel est acquitté sans effet.</summary>
    public void OnCall(string functionPath, Action<SimulatedCall> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        Schema.GetFunction(functionPath);
        lock (_gate)
        {
            _handlers[functionPath] = handler;
        }
    }

    /// <summary>Redémarre la carte : la RAM revient aux valeurs par défaut, recouvertes par la configuration sauvegardée.</summary>
    public void Reboot()
    {
        lock (_gate)
        {
            ResetRam();
        }
        Rebooted?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Traite un paquet reçu et renvoie la réponse, ou <c>null</c> si la carte ne répond pas.</summary>
    public byte[]? HandlePacket(ReadOnlySpan<byte> packet)
    {
        // legacy_protocol.cpp:415-419 : seq_no ; bit 15 = c'est un ACK, pas une requête.
        if (packet.Length < 2)
        {
            return null;
        }
        ushort seqNo = BinaryPrimitives.ReadUInt16LittleEndian(packet);
        if ((seqNo & 0x8000) != 0)
        {
            return null;
        }

        // legacy_protocol.cpp:447-451 : il faut au moins endpoint_id, taille de réponse et trailer.
        ReadOnlySpan<byte> rest = packet[2..];
        if (rest.Length < 6)
        {
            return null;
        }

        ushort endpointId = BinaryPrimitives.ReadUInt16LittleEndian(rest);
        bool expectResponse = (endpointId & 0x8000) != 0;
        endpointId &= 0x7FFF;

        // legacy_protocol.cpp:470-478 : trailer = version du protocole pour l'endpoint 0, CRC du JSON sinon.
        ushort expectedTrailer = endpointId != 0 ? Schema.JsonCrc : PacketCodec.ProtocolVersion;
        ushort actualTrailer = BinaryPrimitives.ReadUInt16LittleEndian(rest[^2..]);
        if (expectedTrailer != actualTrailer)
        {
            return null;
        }

        // legacy_protocol.cpp:483-487 : taille de réponse plafonnée à MTU − 2.
        int requestedSize = BinaryPrimitives.ReadUInt16LittleEndian(rest[2..]);
        int responseSize = Math.Min(requestedSize, PacketCodec.MaxResponsePayloadSize);
        ReadOnlySpan<byte> input = rest[4..^2];

        byte[] output;
        bool suppressResponse = false;
        bool rebooted;
        lock (_gate)
        {
            output = endpointId == 0
                ? HandleEndpoint0(input, responseSize)
                : HandleEndpoint(endpointId, input, responseSize, out suppressResponse);
            rebooted = suppressResponse;
        }

        if (rebooted)
        {
            Rebooted?.Invoke(this, EventArgs.Empty);
        }
        if (!expectResponse || suppressResponse)
        {
            return null;
        }

        // legacy_protocol.cpp:494-497
        byte[] response = new byte[2 + output.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(response, (ushort)(seqNo | 0x8000));
        output.CopyTo(response, 2);
        return response;
    }

    // legacy_protocol.cpp:298-318
    private byte[] HandleEndpoint0(ReadOnlySpan<byte> input, int responseSize)
    {
        if (input.Length < 4)
        {
            return [];
        }

        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(input);
        if (offset == 0xFFFFFFFF)
        {
            if (responseSize < 4)
            {
                return [];
            }
            byte[] id = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(id, Schema.JsonVersionId);
            return id;
        }

        ReadOnlySpan<byte> json = Schema.Json.Span;
        if (offset >= json.Length)
        {
            return [];
        }
        int count = Math.Min(responseSize, json.Length - (int)offset);
        return json.Slice((int)offset, count).ToArray();
    }

    private byte[] HandleEndpoint(ushort endpointId, ReadOnlySpan<byte> input, int responseSize, out bool suppressResponse)
    {
        suppressResponse = false;

        if (_functions.TryGetValue(endpointId, out FunctionNode? function))
        {
            var call = new SimulatedCall(this, function);
            if (_handlers.TryGetValue(function.Path, out Action<SimulatedCall>? handler))
            {
                handler(call);
            }
            suppressResponse = call.SuppressResponse;
            return [];
        }

        if (!_endpoints.TryGetValue(endpointId, out PropertyNode? endpoint))
        {
            return [];
        }

        // Endpoint de propriété = « exchange » : écrit la valeur fournie (si l'accès le permet), puis renvoie
        // la valeur courante (endpoints_template.j2:36-62).
        int size = EndpointValue.SizeOf(endpoint.Type);
        if (endpoint.IsWritable && input.Length >= size)
        {
            _values[endpointId] = input[..size].ToArray();
        }
        return _values[endpointId].AsSpan(0, Math.Min(size, responseSize)).ToArray();
    }

    private void RegisterBuiltInFunctions()
    {
        // ref/ODrive/Firmware/MotorControl/main.cpp:236-249
        _handlers["clear_errors"] = _ =>
        {
            foreach (PropertyNode property in Schema.Properties)
            {
                if (property.Name == "error" && property.Path.Split('.').Length <= 3
                    && !property.Path.StartsWith("can.", StringComparison.Ordinal)
                    && !property.Path.Contains(".config.", StringComparison.Ordinal))
                {
                    Array.Clear(_values[property.Id]);
                }
            }
        };

        // main.cpp:159-184 : refusé si un moteur est armé, sinon écriture en flash puis reset sans réponse.
        _handlers["save_configuration"] = call =>
        {
            bool anyArmed = Schema.Properties.Any(p =>
                p.Name == "is_armed" && _values[p.Id][0] != 0);
            if (anyArmed)
            {
                call.SetOutput("success", false);
                return;
            }

            _saved = Schema.Properties.Where(IsConfiguration)
                .ToDictionary(p => p.Id, p => (byte[])_values[p.Id].Clone());
            ResetRam();
            call.SuppressResponse = true;
        };

        // main.cpp:186-195
        _handlers["erase_configuration"] = call =>
        {
            _saved = [];
            ResetRam();
            call.SuppressResponse = true;
        };

        _handlers["reboot"] = call =>
        {
            ResetRam();
            call.SuppressResponse = true;
        };
    }

    private static bool IsConfiguration(PropertyNode property) =>
        property.Path.StartsWith("config.", StringComparison.Ordinal)
        || property.Path.Contains(".config.", StringComparison.Ordinal);

    private void ResetRam()
    {
        foreach ((ushort id, byte[] value) in _defaults)
        {
            _values[id] = (byte[])(_saved.TryGetValue(id, out byte[]? saved) ? saved : value).Clone();
        }
    }

    private PropertyNode? FindEndpoint(string path)
    {
        if (Schema.FindProperty(path) is { } property)
        {
            return property;
        }

        int dot = path.LastIndexOf('.');
        if (dot > 0 && Schema.FindFunction(path[..dot]) is { } function)
        {
            string name = path[(dot + 1)..];
            return function.Inputs.Concat(function.Outputs).FirstOrDefault(a => a.Name == name);
        }
        return null;
    }

    private PropertyNode GetEndpoint(string path) =>
        FindEndpoint(path) ?? throw new KeyNotFoundException($"Endpoint absent de la carte simulée : {path}");
}
