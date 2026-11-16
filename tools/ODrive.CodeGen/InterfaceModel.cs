using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.RepresentationModel;

namespace ODrive.CodeGen;

// Portage fidèle de la partie « chargement + résolution » du générateur de référence du firmware :
// ref/ODrive/tools/fibre-tools/interface_generator.py (tag fw-v0.5.6). Les numéros de ligne cités dans
// les commentaires renvoient à ce fichier. L'ordre des éléments est conservé partout, car il détermine
// la numérotation des endpoints côté carte.

public sealed record EnumValue(string Name, ulong Value, string? Brief, string? Doc);

public sealed class ValueTypeDef
{
    public required string FullName { get; init; }
    public required string Name { get; init; }
    public bool IsEnum { get; init; }
    public bool IsFlags { get; init; }
    public List<EnumValue> Values { get; } = [];

    /// <summary>Type tel qu'il apparaît dans le JSON de la carte (map_to_fibre01_type, l.443).</summary>
    public string JsonType
    {
        get
        {
            if (IsEnum)
            {
                ulong max = Values.Max(v => v.Value);
                return max <= 0xff ? "uint8" : max <= 0xffff ? "uint16" : max <= 0xffffffff ? "uint32" : "uint64";
            }
            return FullName == "float32" ? "float" : FullName;
        }
    }
}

public sealed class AttributeDef
{
    public required string Name { get; init; }
    public string? Unit { get; init; }
    public string? Brief { get; init; }
    public string? Doc { get; init; }

    // Référence non résolue (remplie au chargement), puis résolue par InterfaceModel.Resolve().
    public string? RefScope { get; set; }
    public string? RefName { get; set; }
    public bool RefIsValueOnly { get; set; }

    /// <summary>Non nul si l'attribut est un sous-objet.</summary>
    public InterfaceDef? SubObject { get; set; }
    /// <summary>Non nul si l'attribut est une propriété (fibre.Property&lt;T&gt;).</summary>
    public ValueTypeDef? Value { get; set; }
    public bool ReadOnly { get; set; }
}

public sealed class ArgDef
{
    public required string Name { get; init; }
    public string? Doc { get; init; }
    public required string TypeName { get; init; }
    public ValueTypeDef? Type { get; set; }
}

public sealed class FunctionDef
{
    public required string Name { get; init; }
    public string? Brief { get; init; }
    public string? Doc { get; init; }
    public List<ArgDef> Inputs { get; } = [];
    public List<ArgDef> Outputs { get; } = [];
}

public sealed class InterfaceDef
{
    public required string FullName { get; init; }
    public List<string> ImplementsNames { get; } = [];
    public List<InterfaceDef> Implements { get; } = [];
    public List<AttributeDef> Attributes { get; } = [];
    public List<FunctionDef> Functions { get; } = [];

    // get_all_attributes (l.321) : interfaces implémentées d'abord, puis les attributs propres.
    // OrderedDict.update : une clé déjà présente garde sa position, sa valeur est remplacée.
    public List<AttributeDef> GetAllAttributes()
    {
        var result = new List<AttributeDef>();
        foreach (var attr in Implements.SelectMany(i => i.GetAllAttributes()).Concat(Attributes))
        {
            int idx = result.FindIndex(a => a.Name == attr.Name);
            if (idx >= 0) result[idx] = attr; else result.Add(attr);
        }
        return result;
    }

    public List<FunctionDef> GetAllFunctions()
    {
        var result = new List<FunctionDef>();
        foreach (var func in Implements.SelectMany(i => i.GetAllFunctions()).Concat(Functions))
        {
            int idx = result.FindIndex(f => f.Name == func.Name);
            if (idx >= 0) result[idx] = func; else result.Add(func);
        }
        return result;
    }
}

public sealed class InterfaceModel
{
    private static readonly string[] BuiltinTypes =
        ["bool", "float32", "uint8", "uint16", "uint32", "uint64", "int8", "int16", "int32", "int64", "endpoint_ref"];

    private readonly List<string> _dictionary = [];

    public Dictionary<string, InterfaceDef> Interfaces { get; } = [];
    /// <summary>Types valeur dans l'ordre de déclaration (comme value_types en Python).</summary>
    public List<ValueTypeDef> ValueTypes { get; } = [];
    private readonly Dictionary<string, ValueTypeDef> _valueTypesByName = [];

    public IEnumerable<ValueTypeDef> Enums => ValueTypes.Where(v => v.IsEnum);

    public static InterfaceModel Load(string yamlPath)
    {
        var stream = new YamlStream();
        using (var reader = new StreamReader(yamlPath))
            stream.Load(reader);
        var root = (YamlMappingNode)stream.Documents[0].RootNode;

        var model = new InterfaceModel();
        foreach (string builtin in BuiltinTypes)
            model.AddValueType(new ValueTypeDef { FullName = builtin, Name = builtin });

        if (Child(root, "dictionary") is YamlSequenceNode dict)
            model._dictionary.AddRange(dict.Children.Select(c => Scalar(c)!));

        // l.602-605 : d'abord toutes les interfaces, ensuite les types valeur.
        if (Child(root, "interfaces") is YamlMappingNode interfaces)
            foreach (var (key, node) in interfaces.Children)
                model.LoadInterface("", Scalar(key)!, (YamlMappingNode)node);
        if (Child(root, "valuetypes") is YamlMappingNode valueTypes)
            foreach (var (key, node) in valueTypes.Children)
                model.LoadValueType("", Scalar(key)!, node as YamlMappingNode);

        model.Resolve();
        return model;
    }

    // ---------- Chargement (InterfaceElement.__init__ l.289, regularize_* l.189-262) ----------

    private InterfaceDef LoadInterface(string path, string name, YamlMappingNode elem)
    {
        var intf = new InterfaceDef { FullName = JoinName(path, name) };
        Interfaces[intf.FullName] = intf;

        switch (Child(elem, "implements"))
        {
            case YamlScalarNode s: intf.ImplementsNames.Add(s.Value!); break;
            case YamlSequenceNode seq: intf.ImplementsNames.AddRange(seq.Children.Select(c => Scalar(c)!)); break;
        }

        if (Child(elem, "functions") is YamlMappingNode functions)
            foreach (var (key, node) in functions.Children)
                intf.Functions.Add(LoadFunction(Scalar(key)!, node as YamlMappingNode));

        if (Child(elem, "attributes") is YamlMappingNode attributes)
            foreach (var (key, node) in attributes.Children)
                intf.Attributes.Add(LoadAttribute(intf, Scalar(key)!, node));

        return intf;
    }

    private static FunctionDef LoadFunction(string name, YamlMappingNode? elem)
    {
        var func = new FunctionDef
        {
            Name = name,
            Brief = elem is null ? null : Scalar(Child(elem, "brief")),
            Doc = elem is null ? null : Scalar(Child(elem, "doc")),
        };
        if (elem is null) return func;

        if (Child(elem, "in") is YamlMappingNode inputs)
            func.Inputs.AddRange(inputs.Children.Select(kv => LoadArg(Scalar(kv.Key)!, kv.Value)));
        if (Child(elem, "out") is YamlMappingNode outputs)
            func.Outputs.AddRange(outputs.Children.Select(kv => LoadArg(Scalar(kv.Key)!, kv.Value)));
        return func;
    }

    // regularize_arg (l.189) : soit une chaîne (le type), soit {type, doc}.
    private static ArgDef LoadArg(string name, YamlNode node) => node switch
    {
        YamlMappingNode m => new ArgDef { Name = name, TypeName = Scalar(Child(m, "type"))!, Doc = Scalar(Child(m, "doc")) },
        _ => new ArgDef { Name = name, TypeName = Scalar(node) ?? throw new InvalidDataException($"argument {name} sans type") },
    };

    // regularize_attribute (l.211)
    private AttributeDef LoadAttribute(InterfaceDef parent, string name, YamlNode node)
    {
        var map = node as YamlMappingNode;
        var attr = new AttributeDef
        {
            Name = name,
            Unit = map is null ? null : Scalar(Child(map, "unit")),
            Brief = map is null ? null : Scalar(Child(map, "brief")),
            Doc = map is null ? null : Scalar(Child(map, "doc")),
        };

        // Sans clé « type », l'élément décrit lui-même son type (interface ou enum en ligne), l.216-224.
        YamlNode typeNode = map is null ? node : Child(map, "type") ?? map;

        if (typeNode is YamlMappingNode typeMap)
        {
            if (Child(typeMap, "flags") is not null || Child(typeMap, "values") is not null)
            {
                // l.239-241 : enum/flags en ligne, nommé d'après l'attribut, en lecture-écriture.
                attr.Value = LoadValueType(parent.FullName, ToPascalCase(name), typeMap);
            }
            else
            {
                // l.244 : interface anonyme, nommée <parent>.<NomPascal>.
                attr.SubObject = LoadInterface(parent.FullName, ToPascalCase(name), typeMap);
            }
        }
        else
        {
            string typeName = Scalar(typeNode) ?? throw new InvalidDataException($"{parent.FullName}.{name} : type manquant");
            attr.RefScope = parent.FullName;
            if (typeName.StartsWith("readonly ", StringComparison.Ordinal))
            {
                // l.233-237
                attr.ReadOnly = true;
                attr.RefIsValueOnly = true;
                attr.RefName = typeName["readonly ".Length..];
            }
            else
            {
                attr.RefName = typeName;
            }
        }
        return attr;
    }

    // regularize_valuetype (l.398)
    private ValueTypeDef LoadValueType(string path, string name, YamlMappingNode? elem)
    {
        string fullName = JoinName(path, name);
        var flags = elem is null ? null : Child(elem, "flags") as YamlMappingNode;
        var values = elem is null ? null : Child(elem, "values") as YamlMappingNode;
        var vt = new ValueTypeDef
        {
            FullName = fullName,
            Name = SplitName(name)[^1],
            IsEnum = flags is not null || values is not null,
            IsFlags = flags is not null,
        };

        if (flags is not null)
        {
            // l.407-418 : le bit s'incrémente à partir du dernier bit explicite ; nullflag = valeur 0 en tête.
            if (Scalar(Child(elem!, "nullflag")) is { } nullFlag)
                vt.Values.Add(new EnumValue(nullFlag, 0, null, null));
            int bit = 0;
            foreach (var (key, node) in flags.Children)
            {
                var m = node as YamlMappingNode;
                int currentBit = m is not null && Scalar(Child(m, "bit")) is { } b ? (int)ParseInt(b) : bit;
                vt.Values.Add(new EnumValue(Scalar(key)!, 1UL << currentBit, Brief(m), Doc(m)));
                bit = currentBit + 1;
            }
        }
        else if (values is not null)
        {
            // l.423-430 : la valeur s'incrémente à partir de la dernière valeur explicite.
            ulong val = 0;
            foreach (var (key, node) in values.Children)
            {
                var m = node as YamlMappingNode;
                if (m is not null && Scalar(Child(m, "value")) is { } v) val = ParseInt(v);
                vt.Values.Add(new EnumValue(Scalar(key)!, val, Brief(m), Doc(m)));
                val++;
            }
        }

        AddValueType(vt);
        return vt;

        static string? Brief(YamlMappingNode? m) => m is null ? null : Scalar(Child(m, "brief"));
        static string? Doc(YamlMappingNode? m) => m is null ? null : Scalar(Child(m, "doc"));
    }

    private void AddValueType(ValueTypeDef vt)
    {
        if (!_valueTypesByName.TryAdd(vt.FullName, vt))
            throw new InvalidDataException($"type valeur en double : {vt.FullName}");
        ValueTypes.Add(vt);
    }

    // ---------- Résolution (l.615-631, InterfaceRefElement.resolve l.258, resolve_valuetype l.432) ----------

    private void Resolve()
    {
        string? clash = _valueTypesByName.Keys.FirstOrDefault(Interfaces.ContainsKey);
        if (clash is not null)
            throw new InvalidDataException($"{clash} est à la fois une interface et un type valeur");

        foreach (var intf in Interfaces.Values)
        {
            foreach (string name in intf.ImplementsNames)
                intf.Implements.Add(ResolveInterface(intf.FullName, name)
                    ?? throw new InvalidDataException($"{intf.FullName} : interface {name} introuvable"));

            foreach (var attr in intf.Attributes.Where(a => a.RefName is not null))
            {
                if (attr.RefIsValueOnly)
                {
                    attr.Value = ResolveValueType(attr.RefScope!, attr.RefName!);
                    continue;
                }
                // À chaque niveau de portée : interface d'abord, type valeur ensuite (l.277-283).
                foreach (string probe in Probes(attr.RefScope!, attr.RefName!))
                {
                    if (Interfaces.TryGetValue(probe, out var obj)) { attr.SubObject = obj; break; }
                    if (_valueTypesByName.TryGetValue(probe, out var vt)) { attr.Value = vt; break; }
                }
                if (attr.SubObject is null && attr.Value is null)
                    throw new InvalidDataException($"{intf.FullName}.{attr.Name} : type {attr.RefName} introuvable");
            }

            foreach (var func in intf.Functions)
                foreach (var arg in func.Inputs.Concat(func.Outputs))
                    arg.Type = ResolveValueType(intf.FullName, arg.TypeName);
        }
    }

    private InterfaceDef? ResolveInterface(string scope, string name) =>
        Probes(scope, name).Select(p => Interfaces.GetValueOrDefault(p)).FirstOrDefault(i => i is not null);

    private ValueTypeDef ResolveValueType(string scope, string name) =>
        Probes(scope, name).Select(p => _valueTypesByName.GetValueOrDefault(p)).FirstOrDefault(v => v is not null)
        ?? throw new InvalidDataException($"type valeur {name} introuvable depuis {scope}");

    /// <summary>Noms candidats, de la portée la plus interne à la portée globale.</summary>
    private static IEnumerable<string> Probes(string scope, string name)
    {
        string[] parts = scope.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i <= parts.Length; i++)
            yield return JoinName(string.Join('.', parts[..(parts.Length - i)]), name);
    }

    // ---------- Noms (l.132-160) ----------

    public static string JoinName(params string[] names) =>
        string.Join('.', names.SelectMany(n => n.Split('.', StringSplitOptions.RemoveEmptyEntries)));

    private static string[] SplitName(string name) => name.Split('.');

    /// <summary>get_words (l.132) : découpe du PascalCase, snake_case ou MACRO_CASE en mots minuscules.</summary>
    public List<string> GetWords(string s)
    {
        // str.isupper() : au moins une lettre, et aucune lettre minuscule.
        if (s.Any(char.IsLetter) && !s.Any(char.IsLower))
            return [.. s.Split('_').Select(w => w.ToLowerInvariant())];

        string pattern = string.Concat(_dictionary.Select(w => Regex.Escape(w) + "|")) + "[a-z0-9]+|[A-Z][a-z0-9]*";
        return [.. Regex.Matches(s, pattern).Select(m => _dictionary.Contains(m.Value) ? m.Value : m.Value.ToLowerInvariant())];
    }

    public string ToPascalCase(string s) =>
        string.Concat(GetWords(s).Select(w => _dictionary.Contains(w) ? w : Title(w)));

    public string ToMacroCase(string s) => string.Join('_', GetWords(s)).ToUpperInvariant();

    /// <summary>str.title() de Python : majuscule après tout caractère qui n'est pas une lettre.</summary>
    private static string Title(string word)
    {
        var chars = word.ToCharArray();
        bool previousIsLetter = false;
        for (int i = 0; i < chars.Length; i++)
        {
            chars[i] = previousIsLetter ? char.ToLowerInvariant(chars[i]) : char.ToUpperInvariant(chars[i]);
            previousIsLetter = char.IsLetter(chars[i]);
        }
        return new string(chars);
    }

    // ---------- Utilitaires YAML ----------

    private static YamlNode? Child(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) ? node : null;

    /// <summary>Valeur d'un scalaire, ou null pour un nœud absent ou vide (« clé: » sans valeur).</summary>
    private static string? Scalar(YamlNode? node) =>
        node is YamlScalarNode { Value: { Length: > 0 } value } ? value : null;

    private static ulong ParseInt(string text) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ulong.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : ulong.Parse(text, CultureInfo.InvariantCulture);
}
