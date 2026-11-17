using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ODrive.Model;
using ODrive.Protocol;
using ODrive.Protocol.Dfu;
using ODriveStudio.App.Services;

namespace ODriveStudio.App.ViewModels;

/// <summary>
/// Onglet « Firmware » : passage en mode DFU, sauvegarde complète de la flash, écriture d'un firmware ou
/// restauration d'une sauvegarde, retour au firmware. Tout passe par le bootloader ROM du STM32, qui ne
/// peut pas être effacé : une écriture ratée se recommence.
/// </summary>
public sealed partial class FirmwareViewModel : ObservableObject
{
    private const string FileFilter = "Firmware ou sauvegarde (*.elf;*.hex;*.bin)|*.elf;*.hex;*.bin|Tous les fichiers (*.*)|*.*";

    private readonly IDfuDeviceSource _source;
    private readonly IUserDialogs _dialogs;
    private readonly Func<ODriveDevice?> _currentDevice;
    private readonly Func<string?> _currentHardware;
    private readonly Action<string> _status;
    private FirmwareImage? _image;
    private string? _imagePath;
    private (int Major, int Minor, int Variant)? _otpHardware;
    private string? _reportedHardware;
    private bool _described;

    /// <param name="currentDevice">Carte sélectionnée en mode normal, ou <c>null</c>.</param>
    /// <param name="currentHardware">Version matérielle annoncée par son firmware (« v3.6-56V »), ou <c>null</c>.</param>
    public FirmwareViewModel(IDfuDeviceSource source, IUserDialogs dialogs, Func<ODriveDevice?> currentDevice,
        Func<string?> currentHardware, Action<string> status)
    {
        _source = source;
        _dialogs = dialogs;
        _currentDevice = currentDevice;
        _currentHardware = currentHardware;
        _status = status;
    }

    /// <summary>Compte rendu des opérations, la plus récente en dernier.</summary>
    public ObservableCollection<string> Log { get; } = [];

    /// <summary>Vrai quand un bootloader est ouvert par l'outil : sauvegarde et écriture possibles.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackupCommand), nameof(FlashCommand), nameof(LeaveDfuCommand), nameof(EnterDfuCommand))]
    private bool _isDfuReady;

    [ObservableProperty]
    private string _dfuSummary = "";

    /// <summary>Vrai quand une carte en mode normal peut être passée en mode DFU.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(EnterDfuCommand))]
    private bool _canEnterDfu;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(BackupCommand), nameof(FlashCommand), nameof(LeaveDfuCommand), nameof(EnterDfuCommand),
        nameof(ChooseFileCommand))]
    private bool _isBusy;

    [ObservableProperty]
    private string _progressText = "";

    /// <summary>Avancement de l'étape en cours, de 0 à 1.</summary>
    [ObservableProperty]
    private double _progressValue;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(FlashCommand))]
    private bool _hasImage;

    [ObservableProperty]
    private string _imageSummary = "Aucun fichier choisi.";

    /// <summary>Dernière sauvegarde faite pendant cette session, ou <c>null</c>.</summary>
    [ObservableProperty]
    private string? _lastBackupPath;

    /// <summary>À appeler périodiquement : suit l'apparition et la disparition du bootloader.</summary>
    public void Refresh()
    {
        if (IsBusy)
        {
            return;
        }

        if (_currentHardware() is { } hardware)
        {
            _reportedHardware = hardware; // retenu : la carte ne le dira plus une fois en mode DFU
        }

        DfuPresence presence = _source.Probe();
        CanEnterDfu = !presence.Present && _currentDevice() is not null;
        if (!presence.Present)
        {
            IsDfuReady = false;
            _described = false;
            DfuSummary = "Aucune carte en mode DFU.";
            return;
        }
        if (!presence.Usable)
        {
            IsDfuReady = false;
            _described = false;
            DfuSummary = presence.Hint ?? "Une carte est en mode DFU mais ne peut pas être ouverte.";
            return;
        }
        if (_described)
        {
            return;
        }

        try
        {
            using DfuSeDevice dfu = _source.Open();
            _otpHardware = FirmwareUpdater.ReadHardwareVersion(dfu);
            string hardware2 = _otpHardware is { } otp
                ? $"version matérielle en OTP : {Format(otp)}"
                : "OTP vierge : carte non officielle, la variante 24 V / 56 V est à vérifier soi-même";
            DfuSummary = $"Carte en mode DFU — bootloader STM32 n° {dfu.SerialNumber}, "
                + $"{dfu.InternalFlash.Sum(s => s.Length) / 1024} Kio de flash, {hardware2}.";
            IsDfuReady = true;
            _described = true;
        }
        catch (IOException e)
        {
            IsDfuReady = false;
            DfuSummary = "Carte en mode DFU, ouverture impossible : " + e.Message;
        }
    }

    private bool CanEnter() => CanEnterDfu && !IsBusy;

    private bool CanUseDfu() => IsDfuReady && !IsBusy;

    private bool CanFlash() => IsDfuReady && HasImage && !IsBusy;

    private bool NotBusy() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanEnter))]
    private async Task EnterDfuAsync()
    {
        if (_currentDevice() is not { } device)
        {
            return;
        }

        try
        {
            SafetyDecision decision = await SafetyGuard.AuthorizeConfigurationChangeAsync(device);
            if (!decision.IsAllowed)
            {
                Say("Passage en mode DFU refusé : " + decision.Reason);
                return;
            }
            if (!_dialogs.Confirm("Passer en mode DFU",
                "La carte va redémarrer dans son bootloader : les moteurs ne sont plus pilotés et elle disparaît "
                + "des autres onglets.\n\nPour en sortir : bouton « Redémarrer sur le firmware », ou couper puis "
                + "remettre l'alimentation.\n\nContinuer ?"))
            {
                return;
            }
            await device.EnterDfuModeAsync();
            Say("enter_dfu_mode() envoyé : la carte redémarre dans son bootloader.");
        }
        catch (Exception e) when (e is IOException or ODriveProtocolException or KeyNotFoundException)
        {
            Say("Passage en mode DFU impossible : " + e.Message
                + " Mettre l'interrupteur de la carte sur DFU puis couper et remettre l'alimentation.");
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseDfu))]
    private async Task BackupAsync()
    {
        if (!IsDfuReady || IsBusy)
        {
            return;
        }
        string? path = _dialogs.PickSaveFile("Sauvegarder la flash de la carte",
            $"odrive-flash-{DateTime.Now:yyyyMMdd-HHmmss}.bin", "Flash brute (*.bin)|*.bin");
        if (path is null)
        {
            return;
        }

        await RunAsync("Sauvegarde", progress =>
        {
            using DfuSeDevice dfu = _source.Open();
            byte[] flash = FirmwareUpdater.ReadFlash(dfu, progress);
            File.WriteAllBytes(path, flash);
            return flash;
        }, flash =>
        {
            LastBackupPath = path;
            Say($"Sauvegarde écrite : {path} ({flash.Length / 1024} Kio).");
            Say("SHA-256 : " + Convert.ToHexString(SHA256.HashData(flash)));
            if (!FirmwareImage.FromBinary(flash).LooksLikeFirmware(out string? reason))
            {
                Say("ATTENTION : cette sauvegarde ne ressemble pas à un firmware (" + reason + ")");
            }
        });
    }

    [RelayCommand(CanExecute = nameof(NotBusy))]
    private void ChooseFile()
    {
        string? path = _dialogs.PickOpenFile("Choisir un firmware (.elf, .hex) ou une sauvegarde (.bin)", FileFilter);
        if (path is null)
        {
            return;
        }

        _image = null;
        _imagePath = null;
        HasImage = false;
        try
        {
            FirmwareImage image = FirmwareImage.Load(path);
            if (!image.LooksLikeFirmware(out string? reason))
            {
                ImageSummary = $"{Path.GetFileName(path)} n'est pas un firmware pour cette carte : {reason}";
                return;
            }

            _image = image;
            _imagePath = path;
            string target = FirmwareImage.HardwareVersionFromFileName(path) is { } version
                ? "pour " + Format(version) + " d'après son nom"
                : "carte visée inconnue (le nom du fichier ne la donne pas)";
            ImageSummary = $"{Path.GetFileName(path)} — {image.Segments.Sum(s => s.Data.Length) / 1024} Kio, {target}.";
            HasImage = true;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ImageSummary = $"{Path.GetFileName(path)} : {e.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanFlash))]
    private async Task FlashAsync()
    {
        if (!IsDfuReady || IsBusy || _image is not { } image || _imagePath is not { } path)
        {
            return;
        }

        (int Major, int Minor, int Variant)? fileHardware = FirmwareImage.HardwareVersionFromFileName(path);
        if (_otpHardware is { } otp && fileHardware is { } wanted && otp != wanted)
        {
            Say($"Écriture refusée : ce fichier est prévu pour {Format(wanted)} alors que la carte est une {Format(otp)} "
                + "(version inscrite en usine).");
            return;
        }

        var lines = new List<string>
        {
            $"Écrire {Path.GetFileName(path)} dans la carte ?",
            "",
            "TOUTE la flash est effacée : le firmware actuel et la configuration (calibrations, gains, limites).",
            LastBackupPath is { } backup
                ? $"Sauvegarde de cette session : {backup}"
                : "AUCUNE SAUVEGARDE n'a été faite pendant cette session : sans elle, le firmware actuel est perdu.",
        };
        if (_otpHardware is null)
        {
            lines.Add("");
            lines.Add("Carte non officielle : rien ne permet à l'outil de vérifier que ce fichier lui convient.");
            if (fileHardware is { } target && _reportedHardware is { } reported && Format(target) != reported)
            {
                lines.Add($"ATTENTION : le fichier vise une {Format(target)}, alors que le firmware de la carte "
                    + $"s'annonçait {reported}.");
            }
            lines.Add("Après l'écriture, comparer vbus_voltage à un multimètre avant d'armer un moteur : une "
                + "mauvaise variante (24 V / 56 V) fausse la mesure de tension.");
        }
        lines.Add("");
        lines.Add("Ne pas débrancher pendant l'écriture. En cas d'échec la carte reste en mode DFU et l'écriture "
            + "peut être relancée.");
        if (!_dialogs.Confirm("Écrire le firmware", string.Join("\n", lines)))
        {
            return;
        }

        await RunAsync("Écriture", progress =>
        {
            using DfuSeDevice dfu = _source.Open();
            FirmwareUpdater.Flash(dfu, image, progress);
            dfu.Leave();
            return true;
        }, _ =>
        {
            Say($"Écriture de {Path.GetFileName(path)} vérifiée. La carte redémarre sur ce firmware.");
            Say("La configuration est à refaire (ou à restaurer) : la carte démarre avec les valeurs par défaut.");
        });
    }

    [RelayCommand(CanExecute = nameof(CanUseDfu))]
    private async Task LeaveDfuAsync()
    {
        if (!IsDfuReady || IsBusy)
        {
            return;
        }
        await RunAsync("Redémarrage", _ =>
        {
            using DfuSeDevice dfu = _source.Open();
            dfu.Leave();
            return true;
        }, _ => Say("Bootloader quitté : la carte redémarre sur son firmware."));
    }

    // Exécute une opération DFU bloquante hors du fil d'interface, avec avancement et compte rendu.
    private async Task RunAsync<T>(string name, Func<IProgress<DfuProgress>, T> work, Action<T> done)
    {
        IsBusy = true;
        ProgressValue = 0;
        ProgressText = name + "…";
        var progress = new Progress<DfuProgress>(step =>
        {
            ProgressText = $"{step.Stage} : {step.Done}/{step.Total}";
            ProgressValue = step.Total == 0 ? 0 : (double)step.Done / step.Total;
        });

        try
        {
            T result = await Task.Run(() => work(progress));
            done(result);
            ProgressText = name + " : terminé.";
            ProgressValue = 1;
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            ProgressText = name + " : échec.";
            Say($"{name} en échec : {e.Message}");
        }
        finally
        {
            IsBusy = false;
            _described = false; // la carte a pu quitter le mode DFU
        }
    }

    private void Say(string message)
    {
        Log.Add(string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss}  {message}"));
        _status(message);
    }

    private static string Format((int Major, int Minor, int Variant) version) =>
        string.Create(CultureInfo.InvariantCulture, $"v{version.Major}.{version.Minor}-{version.Variant}V");
}
