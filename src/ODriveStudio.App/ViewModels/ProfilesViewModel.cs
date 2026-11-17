using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ODrive.Model;
using ODrive.Tuning.Profiles;

namespace ODriveStudio.App.ViewModels;

/// <summary>Un écart entre le profil chargé et la carte, avec sa case à cocher.</summary>
public sealed partial class ProfileDifferenceViewModel(ProfileDifference difference) : ObservableObject
{
    public ProfileDifference Difference { get; } = difference;
    public string Path => Difference.Path;
    public string ProfileValue => Difference.ProfileValue ?? "(absent du profil)";
    public string DeviceValue => Difference.DeviceValue ?? "(absent de la carte)";
    public bool CanApply => Difference.CanApply;

    [ObservableProperty] private bool _isSelected;
}

/// <summary>
/// Onglet « Profils » (SPEC §9) : export de la configuration de la carte, chargement d'un profil, liste des
/// écarts avec la carte et application des seuls écarts cochés.
/// </summary>
public sealed partial class ProfilesViewModel(DeviceSession session) : ObservableObject
{
    private const string Filter = "Profil ODrive Studio (*.json)|*.json|Tous les fichiers (*.*)|*.*";

    private ConfigProfile? _profile;

    public ObservableCollection<ProfileDifferenceViewModel> Differences { get; } = [];

    [ObservableProperty] private string _profileSummary = "Aucun profil chargé.";
    [ObservableProperty] private string _warning = "";
    [ObservableProperty] private string _message = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CompareCommand), nameof(ApplySelectedCommand), nameof(SelectAllCommand),
        nameof(SelectNoneCommand))]
    private bool _hasProfile;

    /// <summary>Écrit toute la configuration de la carte dans un fichier.</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        string? path = session.Dialogs.PickSaveFile("Exporter le profil de la carte",
            $"odrive-profil-{DateTime.Now:yyyyMMdd-HHmmss}.json", Filter);
        if (path is null)
        {
            return;
        }

        await session.RunAsync("Export du profil", async () =>
        {
            ConfigProfile profile = await ConfigProfile.CaptureAsync(session.Device);
            try
            {
                await using FileStream stream = File.Create(path);
                profile.Save(stream);
                Say($"Profil exporté : {path} ({profile.Values.Count} valeurs).");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Say("Export du profil impossible : " + e.Message);
            }
        });
    }

    /// <summary>Charge un profil et liste ses écarts avec la carte.</summary>
    [RelayCommand]
    private async Task ImportAsync()
    {
        string? path = session.Dialogs.PickOpenFile("Charger un profil", Filter);
        if (path is null)
        {
            return;
        }

        try
        {
            await using FileStream stream = File.OpenRead(path);
            _profile = ConfigProfile.Load(stream);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Say($"{Path.GetFileName(path)} : {e.Message}");
            return;
        }

        HasProfile = true;
        ProfileSummary = $"{Path.GetFileName(path)} — firmware {_profile.Firmware}, {_profile.Hardware}, "
            + $"{_profile.Values.Count} valeurs" + (_profile.Note.Length > 0 ? $" — {_profile.Note}" : "");
        Warning = _profile.CompatibilityWarning(session.Device) ?? "";
        await CompareAsync();
    }

    private bool CanUseProfile() => HasProfile;

    /// <summary>Recalcule les écarts entre le profil chargé et la carte.</summary>
    [RelayCommand(CanExecute = nameof(CanUseProfile))]
    private async Task CompareAsync()
    {
        if (_profile is not { } profile)
        {
            return;
        }

        await session.RunAsync("Comparaison du profil", async () =>
        {
            IReadOnlyList<ProfileDifference> differences = await profile.CompareToDeviceAsync(session.Device);
            Differences.Clear();
            foreach (ProfileDifference difference in differences)
            {
                Differences.Add(new ProfileDifferenceViewModel(difference));
            }
            Say(differences.Count == 0
                ? "La carte a exactement la configuration du profil."
                : $"{differences.Count} écart(s) entre le profil et la carte.");
        });
    }

    [RelayCommand(CanExecute = nameof(CanUseProfile))]
    private void SelectAll()
    {
        foreach (ProfileDifferenceViewModel row in Differences)
        {
            row.IsSelected = row.CanApply;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseProfile))]
    private void SelectNone()
    {
        foreach (ProfileDifferenceViewModel row in Differences)
        {
            row.IsSelected = false;
        }
    }

    /// <summary>Écrit dans la carte les valeurs de profil des écarts cochés (rien n'est sauvegardé en flash).</summary>
    [RelayCommand(CanExecute = nameof(CanUseProfile))]
    private async Task ApplySelectedAsync()
    {
        List<ProfileDifference> selected = [.. Differences.Where(row => row.IsSelected && row.CanApply).Select(row => row.Difference)];
        if (selected.Count == 0)
        {
            Say("Aucun écart coché.");
            return;
        }

        await session.RunAsync("Application du profil", async () =>
        {
            SafetyDecision decision = await SafetyGuard.AuthorizeConfigurationChangeAsync(session.Device);
            if (!decision.IsAllowed)
            {
                Say("Application refusée : " + decision.Reason);
                return;
            }
            if (!session.Dialogs.Confirm("Appliquer le profil",
                $"Écrire {selected.Count} valeur(s) du profil dans la carte ?\n\nRien n'est sauvegardé en mémoire "
                + "permanente : utiliser ensuite « Sauvegarder dans la carte » pour les conserver."))
            {
                return;
            }

            IReadOnlyList<ProfileApplyFailure> failures = await ConfigProfile.ApplyAsync(session.Device, selected);
            await CompareAsync();
            Say(failures.Count == 0
                ? $"{selected.Count} valeur(s) écrite(s). Sauvegarder dans la carte pour les conserver ; certaines "
                  + "ne prennent effet qu'après redémarrage."
                : $"{selected.Count - failures.Count} valeur(s) écrite(s), {failures.Count} refusée(s) : "
                  + string.Join(" ; ", failures.Select(f => $"{f.Path} ({f.Message})")));
        });
    }

    private void Say(string message)
    {
        Message = message;
        session.Status(message);
    }
}
