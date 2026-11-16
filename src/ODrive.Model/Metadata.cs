using System.Collections.Frozen;

namespace ODrive.Model;

/// <summary>Une valeur d'enum ou un flag, avec son nom firmware (ex. <c>CLOSED_LOOP_CONTROL</c>).</summary>
public sealed record EnumValueMetadata(string Name, ulong Value, string? Brief, string? Doc);

/// <summary>Un enum ou un jeu de flags de <c>odrive-interface.yaml</c>.</summary>
/// <param name="Type">Type C# généré.</param>
/// <param name="FirmwareName">Nom complet dans le YAML, ex. <c>ODrive.Axis.AxisState</c>.</param>
/// <param name="Prefix">Préfixe des constantes odrivetool, ex. <c>AXIS_STATE</c>.</param>
/// <param name="IsFlags">Vrai pour les champs de bits (erreurs).</param>
/// <param name="Values">Valeurs dans l'ordre du YAML.</param>
public sealed record EnumMetadata(
    Type Type, string FirmwareName, string Prefix, bool IsFlags, IReadOnlyList<EnumValueMetadata> Values)
{
    /// <summary>Nom tel qu'affiché par odrivetool, ex. <c>AXIS_STATE_CLOSED_LOOP_CONTROL</c>.</summary>
    public string DisplayName(EnumValueMetadata value) => $"{Prefix}_{value.Name}";

    /// <summary>Décompose une valeur brute en flags actifs (ou en la valeur d'enum correspondante).</summary>
    public IEnumerable<EnumValueMetadata> Decode(ulong raw) =>
        IsFlags
            ? Values.Where(v => v.Value != 0 && (raw & v.Value) == v.Value)
            : Values.Where(v => v.Value == raw);
}

/// <summary>Une propriété de l'arbre ODrive, identifiée par son chemin (ex. <c>axis0.motor.config.pole_pairs</c>).</summary>
/// <param name="Path">Chemin depuis la racine, séparé par des points.</param>
/// <param name="JsonType">Type annoncé par le JSON de la carte (<c>float</c>, <c>uint8</c>, <c>bool</c>…).</param>
/// <param name="IsWritable">Accès <c>rw</c> dans le JSON.</param>
/// <param name="Unit">Unité firmware, si le YAML la précise.</param>
/// <param name="Brief">Résumé d'une ligne.</param>
/// <param name="Doc">Documentation détaillée (Markdown).</param>
/// <param name="EnumType">Enum généré quand la valeur en est un.</param>
public sealed record PropertyMetadata(
    string Path, string JsonType, bool IsWritable, string? Unit, string? Brief, string? Doc, Type? EnumType);

/// <summary>Un argument ou une valeur de retour de fonction.</summary>
public sealed record ArgumentMetadata(string Name, string JsonType, string? Doc, Type? EnumType);

/// <summary>Une fonction de l'arbre ODrive (ex. <c>axis0.controller.start_anticogging_calibration</c>).</summary>
public sealed record FunctionMetadata(
    string Path,
    IReadOnlyList<ArgumentMetadata> Inputs,
    IReadOnlyList<ArgumentMetadata> Outputs,
    string? Brief,
    string? Doc);

public static partial class ODriveInterface
{
    private static readonly Lazy<FrozenDictionary<string, PropertyMetadata>> PropertiesByPath =
        new(() => Properties.ToFrozenDictionary(p => p.Path, StringComparer.Ordinal));

    private static readonly Lazy<FrozenDictionary<string, FunctionMetadata>> FunctionsByPath =
        new(() => Functions.ToFrozenDictionary(f => f.Path, StringComparer.Ordinal));

    /// <summary>Métadonnées d'une propriété, ou <c>null</c> si le chemin est inconnu du firmware 0.5.6.</summary>
    public static PropertyMetadata? FindProperty(string path) => PropertiesByPath.Value.GetValueOrDefault(path);

    /// <summary>Métadonnées d'une fonction, ou <c>null</c> si le chemin est inconnu du firmware 0.5.6.</summary>
    public static FunctionMetadata? FindFunction(string path) => FunctionsByPath.Value.GetValueOrDefault(path);
}

public static partial class ODriveEnums
{
    private static readonly Lazy<FrozenDictionary<Type, EnumMetadata>> ByType =
        new(() => All.ToFrozenDictionary(e => e.Type));

    /// <summary>Métadonnées d'un enum généré.</summary>
    public static EnumMetadata Get<TEnum>() where TEnum : struct, Enum => ByType.Value[typeof(TEnum)];

    /// <summary>Métadonnées d'un enum généré, ou <c>null</c> si le type n'en est pas un.</summary>
    public static EnumMetadata? Find(Type type) => ByType.Value.GetValueOrDefault(type);
}
