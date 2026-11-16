using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ODrive.Model;
using ODrive.Protocol;

namespace ODriveStudio.App.ViewModels;

public enum ExplorerNodeKind
{
    Group,
    Property,
    Function,
}

/// <summary>Argument d'une fonction, saisi dans l'explorateur.</summary>
public sealed partial class FunctionArgumentViewModel(PropertyNode node, EnumMetadata? enumType) : ObservableObject
{
    public PropertyNode Node { get; } = node;
    public EnumMetadata? EnumType { get; } = enumType;
    public string Name => Node.Name;
    public string TypeName => ValueText.TypeName(Node.Type);

    [ObservableProperty] private string _text = "";
}

/// <summary>Un nœud de l'arbre de la carte : objet, propriété ou fonction.</summary>
public sealed partial class ExplorerNodeViewModel : ObservableObject
{
    private readonly ExplorerViewModel _owner;

    internal ExplorerNodeViewModel(ExplorerViewModel owner, EndpointNode node, ExplorerNodeViewModel? parent)
    {
        _owner = owner;
        Node = node;
        Parent = parent;
        Kind = node switch
        {
            ObjectNode => ExplorerNodeKind.Group,
            FunctionNode => ExplorerNodeKind.Function,
            _ => ExplorerNodeKind.Property,
        };

        if (node is PropertyNode property)
        {
            // Type, unité et doc viennent du code généré ; le reste (id, type, accès) de l'arbre lu sur la carte.
            PropertyMetadata? metadata = ODriveInterface.FindProperty(property.Path);
            EnumType = metadata?.EnumType is { } enumType ? ODriveEnums.Find(enumType) : null;
            Unit = metadata?.Unit ?? "";
            Documentation = JoinDoc(metadata?.Brief, metadata?.Doc);
            TypeName = ValueText.TypeName(property.Type) + (EnumType is null ? "" : $" ({EnumType.Prefix})");
            IsWritable = property.IsWritable;
            Choices = EnumType is { IsFlags: false } ? EnumType.Values.Select(v => $"{v.Name} ({v.Value})").ToList() : [];
        }
        else if (node is FunctionNode function)
        {
            FunctionMetadata? metadata = ODriveInterface.FindFunction(function.Path);
            Documentation = JoinDoc(metadata?.Brief, metadata?.Doc);
            TypeName = "fonction";
            Arguments = function.Inputs
                .Select((input, i) => new FunctionArgumentViewModel(input,
                    metadata?.Inputs.ElementAtOrDefault(i)?.EnumType is { } t ? ODriveEnums.Find(t) : null))
                .ToList();
        }
        else if (node is ObjectNode obj)
        {
            TypeName = "objet";
            Children = obj.Members.Select(m => new ExplorerNodeViewModel(owner, m, this)).ToList();
        }
    }

    public EndpointNode Node { get; }
    public ExplorerNodeViewModel? Parent { get; }
    public ExplorerNodeKind Kind { get; }
    public string Name => Node.Name;
    public string Path => Node.Path;
    public IReadOnlyList<ExplorerNodeViewModel> Children { get; } = [];
    public IReadOnlyList<FunctionArgumentViewModel> Arguments { get; } = [];
    public IReadOnlyList<string> Choices { get; } = [];
    public EnumMetadata? EnumType { get; }
    public string Unit { get; } = "";
    public string TypeName { get; } = "";
    public string Documentation { get; } = "";
    public bool IsWritable { get; }
    public bool IsProperty => Kind == ExplorerNodeKind.Property;
    public bool IsFunction => Kind == ExplorerNodeKind.Function;
    public bool IsObject => Kind == ExplorerNodeKind.Group;
    public bool HasChoices => Choices.Count > 0;
    public string Access => IsProperty ? (IsWritable ? "lecture / écriture" : "lecture seule") : "";

    /// <summary>Valeur lue sur la carte, formatée ; vide tant qu'elle n'a pas été lue.</summary>
    [ObservableProperty] private string _currentValue = "";

    /// <summary>Saisie de l'utilisateur pour une écriture.</summary>
    [ObservableProperty] private string _editText = "";

    /// <summary>Résultat du dernier appel (fonction) ou message d'erreur de saisie.</summary>
    [ObservableProperty] private string _message = "";

    [ObservableProperty] private bool _isExpanded;
    [ObservableProperty] private bool _isVisible = true;
    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
        {
            _owner.Select(this);
        }
    }

    [RelayCommand]
    private Task ApplyAsync() => _owner.WriteAsync(this);

    [RelayCommand]
    private Task CallAsync() => _owner.CallAsync(this);

    [RelayCommand]
    private Task RefreshAsync() => _owner.RefreshNodeAsync(this);

    private static string JoinDoc(string? brief, string? doc) =>
        string.Join("\n\n", new[] { brief, doc }.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()));
}

/// <summary>
/// Explorateur de paramètres générique (SPEC §3) : l'arbre complet lu sur la carte, comme dans odrivetool,
/// avec recherche, édition typée et appel de fonctions.
/// </summary>
public sealed partial class ExplorerViewModel : ObservableObject
{
    /// <summary>Nombre maximal de lectures par cycle de rafraîchissement, pour ne pas saturer la liaison.</summary>
    private const int MaxReadsPerRefresh = 80;

    private readonly DeviceSession _session;
    private readonly List<ExplorerNodeViewModel> _all = [];
    private int _refreshCursor;

    public ExplorerViewModel(DeviceSession session)
    {
        _session = session;
        Roots = session.Device.Schema.Root.Members.Select(m => new ExplorerNodeViewModel(this, m, null)).ToList();
        Collect(Roots);

        void Collect(IEnumerable<ExplorerNodeViewModel> nodes)
        {
            foreach (ExplorerNodeViewModel node in nodes)
            {
                _all.Add(node);
                Collect(node.Children);
            }
        }
    }

    public IReadOnlyList<ExplorerNodeViewModel> Roots { get; }

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private ExplorerNodeViewModel? _selectedNode;
    [ObservableProperty] private string _matchSummary = "";

    public ExplorerNodeViewModel? Find(string path) => _all.FirstOrDefault(n => n.Path == path);

    partial void OnSearchTextChanged(string value) => ApplyFilter(value.Trim());

    internal void Select(ExplorerNodeViewModel node)
    {
        SelectedNode = node;
        if (node.IsProperty && node.EditText.Length == 0)
        {
            node.EditText = node.CurrentValue;
        }
    }

    /// <summary>Relit les propriétés affichées (ancêtres dépliés), par tranches pour ménager la liaison.</summary>
    public async Task RefreshVisibleAsync(CancellationToken ct = default)
    {
        List<ExplorerNodeViewModel> shown = _all.Where(n => n.IsProperty && IsShown(n)).ToList();
        if (SelectedNode is { IsProperty: true } selected && !shown.Contains(selected))
        {
            shown.Add(selected);
        }
        if (shown.Count == 0)
        {
            return;
        }

        int count = Math.Min(shown.Count, MaxReadsPerRefresh);
        for (int i = 0; i < count; i++)
        {
            ExplorerNodeViewModel node = shown[(_refreshCursor + i) % shown.Count];
            await ReadAsync(node, ct);
        }
        _refreshCursor = (_refreshCursor + count) % shown.Count;
    }

    internal Task RefreshNodeAsync(ExplorerNodeViewModel node) =>
        node.IsProperty ? _session.RunAsync($"Lecture de {node.Path}", () => ReadAsync(node, default)) : Task.CompletedTask;

    internal async Task WriteAsync(ExplorerNodeViewModel node)
    {
        if (node.Node is not PropertyNode property)
        {
            return;
        }
        if (!property.IsWritable)
        {
            node.Message = "Propriété en lecture seule.";
            return;
        }

        object value;
        try
        {
            value = ValueText.Parse(node.EditText, property.Type, node.EnumType);
        }
        catch (FormatException e)
        {
            node.Message = e.Message;
            return;
        }

        node.Message = "";
        await _session.RunAsync($"Écriture de {node.Path}", async () =>
        {
            if (await _session.WriteGuardedAsync(property, value))
            {
                await ReadAsync(node, default);
                _session.Status($"{node.Path} = {node.CurrentValue}{(node.Unit.Length > 0 ? " " + node.Unit : "")}");
            }
        });
    }

    internal async Task CallAsync(ExplorerNodeViewModel node)
    {
        if (node.Node is not FunctionNode function)
        {
            return;
        }

        object[] arguments = new object[node.Arguments.Count];
        try
        {
            for (int i = 0; i < arguments.Length; i++)
            {
                FunctionArgumentViewModel argument = node.Arguments[i];
                arguments[i] = ValueText.Parse(argument.Text, argument.Node.Type, argument.EnumType);
            }
        }
        catch (FormatException e)
        {
            node.Message = e.Message;
            return;
        }

        // Fonctions qui font redémarrer la carte : mêmes règles que les boutons du tableau de bord.
        if (function.Path is "reboot" or "save_configuration" or "erase_configuration" or "enter_dfu_mode")
        {
            SafetyDecision decision = await SafetyGuard.AuthorizeConfigurationChangeAsync(_session.Device);
            if (!decision.IsAllowed)
            {
                node.Message = decision.Reason ?? "Opération refusée.";
                return;
            }
            if (!_session.Dialogs.Confirm(function.Path + "()", $"Appeler {function.Path}() ? La carte va redémarrer."))
            {
                return;
            }
        }

        node.Message = "";
        await _session.RunAsync($"Appel de {node.Path}()", async () =>
        {
            IReadOnlyList<object> outputs = await _session.Device.CallAsync(function, arguments);
            FunctionMetadata? metadata = ODriveInterface.FindFunction(function.Path);
            node.Message = outputs.Count == 0
                ? "Appel effectué."
                : string.Join(", ", outputs.Select((o, i) =>
                    $"{function.Outputs[i].Name} = {ValueText.Format(o, metadata?.Outputs.ElementAtOrDefault(i)?.EnumType is { } t ? ODriveEnums.Find(t) : null)}"));
            _session.Status($"{node.Path}() : {node.Message}");
        });
    }

    private async Task ReadAsync(ExplorerNodeViewModel node, CancellationToken ct)
    {
        object value = await _session.Device.ReadAsync((PropertyNode)node.Node, ct);
        node.CurrentValue = ValueText.Format(value, node.EnumType);
    }

    private static bool IsShown(ExplorerNodeViewModel node)
    {
        if (!node.IsVisible)
        {
            return false;
        }
        for (ExplorerNodeViewModel? parent = node.Parent; parent is not null; parent = parent.Parent)
        {
            if (!parent.IsExpanded || !parent.IsVisible)
            {
                return false;
            }
        }
        return true;
    }

    // Recherche plein texte sur le chemin, la documentation et l'unité ; les branches qui contiennent un
    // résultat sont dépliées.
    private void ApplyFilter(string filter)
    {
        if (filter.Length == 0)
        {
            foreach (ExplorerNodeViewModel node in _all)
            {
                node.IsVisible = true;
            }
            MatchSummary = "";
            return;
        }

        string[] terms = filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int matches = 0;
        foreach (ExplorerNodeViewModel root in Roots)
        {
            Visit(root, false);
        }
        MatchSummary = matches == 0 ? "aucun résultat" : $"{matches} résultat(s)";

        bool Visit(ExplorerNodeViewModel node, bool ancestorMatches)
        {
            bool self = terms.All(term =>
                node.Path.Contains(term, StringComparison.OrdinalIgnoreCase)
                || node.Documentation.Contains(term, StringComparison.OrdinalIgnoreCase));
            if (self && !node.IsObject)
            {
                matches++;
            }

            bool anyChild = false;
            foreach (ExplorerNodeViewModel child in node.Children)
            {
                anyChild |= Visit(child, ancestorMatches || self);
            }

            // Un objet trouvé par son nom garde tout son contenu visible.
            node.IsVisible = self || anyChild || ancestorMatches;
            if (anyChild)
            {
                node.IsExpanded = true;
            }
            return self || anyChild;
        }
    }
}
