using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ODrive.Model;
using ODrive.Protocol;
using ODrive.Protocol.Dfu;
using ODriveStudio.App.Services;

namespace ODriveStudio.App.ViewModels;

/// <summary>Une ligne du journal des écritures, prête à afficher.</summary>
public sealed record JournalLineViewModel(string Time, string Board, string Kind, string Path, string OldValue, string NewValue)
{
    public static JournalLineViewModel From(WriteJournalEntry entry)
    {
        PropertyMetadata? metadata = entry.Kind == JournalEntryKind.Write ? ODriveInterface.FindProperty(entry.Path) : null;
        EnumMetadata? enumType = metadata?.EnumType is { } t ? ODriveEnums.Find(t) : null;
        return new JournalLineViewModel(
            entry.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            entry.SerialNumber.ToString("X", CultureInfo.InvariantCulture),
            entry.Kind == JournalEntryKind.Write ? "écriture" : "appel",
            entry.Kind == JournalEntryKind.Write ? entry.Path : entry.Path + "()",
            ValueText.Format(entry.OldValue, enumType),
            ValueText.Format(entry.NewValue, enumType));
    }
}

/// <summary>Une carte connectée et ses vues.</summary>
public sealed partial class DeviceViewModel : ObservableObject, IDisposable
{
    private readonly CancellationTokenSource _polling = new();

    public DeviceViewModel(DeviceSession session)
    {
        Session = session;
        Dashboard = new DashboardViewModel(session);
        Explorer = new ExplorerViewModel(session);
        Commissioning = new CommissioningViewModel(session);
        Jog = new JogViewModel(session);
        Profiles = new ProfilesViewModel(session);
        SerialNumber = session.Device.SerialNumber.ToString("X", CultureInfo.InvariantCulture);
    }

    public DeviceSession Session { get; }
    public ODriveDevice Device => Session.Device;
    public DashboardViewModel Dashboard { get; }
    public ExplorerViewModel Explorer { get; }
    public CommissioningViewModel Commissioning { get; }
    public JogViewModel Jog { get; }
    public ProfilesViewModel Profiles { get; }
    public string SerialNumber { get; }

    /// <summary>Faux pour une carte dont la zone OTP n'a pas été programmée par ODrive Robotics (clone) : odrivetool l'appelle « dev0 ».</summary>
    [ObservableProperty] private bool _isGenuine = true;
    [ObservableProperty] private string _firmwareVersion = "";
    [ObservableProperty] private string _hardwareVersion = "";
    [ObservableProperty] private string _title = "";

    /// <summary>Vrai quand l'onglet Explorateur est affiché : ses valeurs ne sont relues que dans ce cas.</summary>
    public bool ExplorerActive { get; set; }

    /// <summary>Vrai quand l'onglet Mise en service est affiché : ses valeurs ne sont relues que dans ce cas.</summary>
    public bool CommissioningActive { get; set; }

    /// <summary>Lit l'identité de la carte (versions, <c>otp_valid</c>).</summary>
    public async Task LoadIdentityAsync(CancellationToken ct = default)
    {
        ODrive3 odrv = Session.Odrv;
        byte fwMajor = await odrv.FwVersionMajor.GetAsync(ct);
        byte fwMinor = await odrv.FwVersionMinor.GetAsync(ct);
        byte fwRevision = await odrv.FwVersionRevision.GetAsync(ct);
        byte hwMajor = await odrv.HwVersionMajor.GetAsync(ct);
        byte hwMinor = await odrv.HwVersionMinor.GetAsync(ct);
        byte hwVariant = await odrv.HwVersionVariant.GetAsync(ct);
        FirmwareVersion = string.Create(CultureInfo.InvariantCulture, $"{fwMajor}.{fwMinor}.{fwRevision}");
        HardwareVersion = string.Create(CultureInfo.InvariantCulture, $"v{hwMajor}.{hwMinor}-{hwVariant}V");
        // tools/odrive/shell.py:86-88 : odrivetool nomme « dev » une carte dont otp_valid est faux.
        IsGenuine = Device.Schema.FindProperty("otp_valid") is null || await odrv.OtpValid.GetAsync(ct);
        Title = $"ODrive {SerialNumber} — {HardwareVersion}, firmware {FirmwareVersion}"
            + (IsGenuine ? "" : " — carte non officielle");
    }

    /// <summary>Un cycle de rafraîchissement des vues.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        await Dashboard.RefreshAsync(ct);
        if (ExplorerActive)
        {
            await Explorer.RefreshVisibleAsync(ct);
        }
        if (CommissioningActive && !Commissioning.IsCalibrating)
        {
            await Commissioning.RefreshAsync(ct);
        }
    }

    /// <summary>Boucle de rafraîchissement, jusqu'à la perte de la liaison ou <see cref="Dispose"/>.</summary>
    public async Task RunPollingAsync(TimeSpan period)
    {
        CancellationToken ct = _polling.Token;
        try
        {
            await LoadIdentityAsync(ct);
            while (!ct.IsCancellationRequested)
            {
                await RefreshAsync(ct);
                await Task.Delay(period, ct);
            }
        }
        catch (Exception e) when (e is ODriveConnectionLostException or OperationCanceledException or ODriveProtocolException)
        {
            // Liaison perdue : le gestionnaire de cartes retire la carte et la reconnecte.
        }
    }

    /// <summary>Arrête la boucle de rafraîchissement.</summary>
    public void Dispose()
    {
        Jog.Dispose();
        Commissioning.Dispose();
        _polling.Cancel();
        _polling.Dispose();
    }
}

/// <summary>Un message de la barre d'état ; <paramref name="Details"/> porte la pile d'appels d'une erreur imprévue.</summary>
public sealed record MessageLineViewModel(string Time, string Text, string Details);

/// <summary>Fenêtre principale : cartes connectées, arrêt d'urgence, journal et barre d'état.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly DeviceManager _manager;
    private readonly WriteJournal _journal;
    private readonly SafetyGuard _guard;
    private readonly IUserDialogs _dialogs;
    private readonly SynchronizationContext? _ui;
    private readonly Func<string?> _driverHint;
    private readonly IUsbDriverRepair? _usbRepair;

    /// <param name="driverHint">Diagnostic du pilote USB quand aucune carte n'est accessible (SPEC §1), ou <c>null</c>.</param>
    /// <param name="usbRepair">Correction du GUID d'interface WinUSB manquant, ou <c>null</c> si indisponible.</param>
    public MainViewModel(DeviceManager manager, WriteJournal journal, SafetyGuard guard, IUserDialogs dialogs,
        Func<string?>? driverHint = null, IUsbDriverRepair? usbRepair = null, IDfuDeviceSource? dfuSource = null)
    {
        // Sans source DFU (tests) : un bootloader simulé qui n'est jamais présent.
        Firmware = new FirmwareViewModel(dfuSource ?? new SimulatedDfuSource(new SimulatedDfuDevice()), dialogs,
            () => SelectedDevice?.Device, () => SelectedDevice?.HardwareVersion,
            message => StatusMessage = message);
        _usbRepair = usbRepair;
        _manager = manager;
        _journal = journal;
        _guard = guard;
        _dialogs = dialogs;
        _driverHint = driverHint ?? (() => null);
        _ui = SynchronizationContext.Current;

        _manager.DeviceConnected += (_, device) => OnUi(() => AddDevice(device));
        _manager.DeviceDisconnected += (_, device) => OnUi(() => RemoveDevice(device));
        _journal.EntryAdded += (_, entry) => OnUi(() => Journal.Add(JournalLineViewModel.From(entry)));
        foreach (ODriveDevice device in _manager.Devices)
        {
            AddDevice(device);
        }
        UpdateConnectionHint();
    }

    public ObservableCollection<DeviceViewModel> Devices { get; } = [];
    public ObservableCollection<JournalLineViewModel> Journal { get; } = [];

    /// <summary>Onglet « Firmware » : mode DFU, sauvegarde et écriture de la flash.</summary>
    public FirmwareViewModel Firmware { get; }

    /// <summary>Période de rafraîchissement des vues d'une carte.</summary>
    public TimeSpan PollingPeriod { get; init; } = TimeSpan.FromMilliseconds(200);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDevice))]
    private DeviceViewModel? _selectedDevice;

    [ObservableProperty] private string _statusMessage = "Recherche d'une carte ODrive…";
    [ObservableProperty] private string _connectionHint = "";

    /// <summary>Vrai quand la correction du pilote USB est proposée (aucune carte ouverte, GUID manquant).</summary>
    [ObservableProperty] private bool _canRepairUsb;
    [ObservableProperty] private int _selectedTabIndex;

    public bool HasDevice => SelectedDevice is not null;

    partial void OnSelectedTabIndexChanged(int value) => UpdateActiveViews();
    partial void OnSelectedDeviceChanged(DeviceViewModel? value) => UpdateActiveViews();

    /// <summary>
    /// Arrêt d'urgence (docs/SAFETY.md) : IDLE sur tous les axes de toutes les cartes, en tête de file.
    /// Toujours disponible : bouton permanent et touche Échap.
    /// </summary>
    [RelayCommand]
    private async Task EmergencyStopAsync()
    {
        IReadOnlyList<ODriveDevice> devices = _manager.Devices;
        IReadOnlyList<EmergencyStopFailure> failures = await EmergencyStop.StopAsync(devices);
        StatusMessage = failures.Count == 0
            ? $"ARRÊT D'URGENCE : IDLE demandé sur {devices.Count} carte(s)."
            : "ARRÊT D'URGENCE : ÉCHEC sur " + string.Join(", ",
                failures.Select(f => $"{f.Device.SerialNumber:X} {f.Path}").Distinct())
              + " — couper l'alimentation.";
    }

    /// <summary>Messages de la barre d'état depuis le lancement, erreurs comprises (les plus récents en bas).</summary>
    public ObservableCollection<MessageLineViewModel> Messages { get; } = [];

    partial void OnStatusMessageChanged(string value) => AddMessage(value, null);

    /// <summary>Erreur imprévue : affichée dans la barre d'état, gardée avec sa pile d'appels pour la copie.</summary>
    public void ReportUnexpected(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string text = "Erreur inattendue : " + exception.Message;
        _pendingDetails = exception.ToString();
        if (StatusMessage == text)
        {
            // Même erreur deux fois de suite : la propriété ne change pas, la ligne doit quand même exister.
            AddMessage(text, null);
        }
        else
        {
            StatusMessage = text;
        }
    }

    private string? _pendingDetails;

    private void AddMessage(string text, string? details)
    {
        details ??= _pendingDetails;
        _pendingDetails = null;
        if (text.Length == 0)
        {
            return;
        }
        var line = new MessageLineViewModel(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), text, details ?? "");
        OnUi(() =>
        {
            Messages.Add(line);
            while (Messages.Count > 1000)
            {
                Messages.RemoveAt(0);
            }
        });
    }

    /// <summary>Copie tous les messages (et le détail des erreurs) dans le presse-papiers.</summary>
    [RelayCommand]
    private void CopyMessages()
    {
        var text = new StringBuilder();
        foreach (MessageLineViewModel line in Messages)
        {
            text.Append(line.Time).Append("  ").AppendLine(line.Text);
            if (line.Details.Length > 0)
            {
                text.AppendLine(line.Details);
            }
        }
        _dialogs.CopyText(text.ToString());
    }

    [RelayCommand]
    private void ExportJournal()
    {
        string? path = _dialogs.PickSaveFile("Exporter le journal des écritures",
            $"odrive-journal-{DateTime.Now:yyyyMMdd-HHmmss}.csv", "CSV (*.csv)|*.csv");
        if (path is null)
        {
            return;
        }

        try
        {
            // BOM : Excel n'ouvre un CSV en UTF-8 que s'il en trouve un.
            using var writer = new StreamWriter(path, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            _journal.ExportCsv(writer);
            StatusMessage = "Journal exporté : " + path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            StatusMessage = "Export du journal impossible : " + e.Message;
        }
    }

    /// <summary>
    /// Enregistre le GUID d'interface WinUSB manquant. L'application reste sans privilèges : seule cette
    /// opération demande les droits administrateur (invite UAC), après accord de l'utilisateur.
    /// </summary>
    [RelayCommand]
    private async Task RepairUsbAsync()
    {
        if (_usbRepair is null)
        {
            return;
        }
        if (!_dialogs.Confirm("Corriger le pilote USB",
            "Windows ne sait pas ouvrir l'interface native de la carte : pilote absent, ou pilote WinUSB sans "
            + "GUID d'interface (défauts du firmware).\n\nLa correction associe le pilote WinUSB fourni avec "
            + "Windows à la carte, écrit une valeur dans le registre (DeviceInterfaceGUIDs) puis redémarre le "
            + "périphérique USB. Windows va demander les droits "
            + "administrateur pour cette seule opération.\n\nContinuer ?"))
        {
            return;
        }

        StatusMessage = "Correction du pilote USB en cours…";
        StatusMessage = await _usbRepair.RunElevatedAsync() switch
        {
            UsbRepairOutcome.Repaired => "Pilote USB corrigé : la carte va être détectée (sinon, rebrancher son câble USB).",
            UsbRepairOutcome.Declined => "Correction du pilote USB annulée : élévation de droits refusée.",
            _ => "La correction du pilote USB a échoué. Essayer « odrive-cli fix-usb » dans un terminal administrateur.",
        };
        UpdateConnectionHint();
    }

    /// <summary>Met à jour l'aide affichée quand aucune carte n'est connectée (erreurs d'ouverture, pilote).</summary>
    public void UpdateConnectionHint()
    {
        if (Devices.Count > 0)
        {
            ConnectionHint = "";
            CanRepairUsb = false;
            return;
        }

        CanRepairUsb = _usbRepair?.IsNeeded == true;

        var lines = new List<string> { "Aucune carte ODrive connectée. Brancher la carte en USB : elle est détectée automatiquement." };
        lines.AddRange(_manager.LastErrors.Values.Distinct().Select(e => "• " + e));
        if (_driverHint() is { } hint)
        {
            lines.Add(hint);
        }
        ConnectionHint = string.Join("\n", lines);
    }

    private void AddDevice(ODriveDevice device)
    {
        var session = new DeviceSession(device, _guard, _dialogs, message => OnUi(() => StatusMessage = message));
        var viewModel = new DeviceViewModel(session);
        Devices.Add(viewModel);
        SelectedDevice ??= viewModel;
        StatusMessage = $"Carte {viewModel.SerialNumber} connectée.";
        UpdateConnectionHint();
        UpdateActiveViews();
        _ = viewModel.RunPollingAsync(PollingPeriod);
    }

    private void RemoveDevice(ODriveDevice device)
    {
        DeviceViewModel? viewModel = Devices.FirstOrDefault(d => ReferenceEquals(d.Device, device));
        if (viewModel is null)
        {
            return;
        }

        viewModel.Dispose();
        Devices.Remove(viewModel);
        if (ReferenceEquals(SelectedDevice, viewModel))
        {
            SelectedDevice = Devices.FirstOrDefault();
        }
        StatusMessage = $"Carte {viewModel.SerialNumber} déconnectée — reconnexion automatique dès qu'elle réapparaît.";
        UpdateConnectionHint();
    }

    private void UpdateActiveViews()
    {
        foreach (DeviceViewModel device in Devices)
        {
            device.ExplorerActive = ReferenceEquals(device, SelectedDevice) && SelectedTabIndex == 1;
            device.CommissioningActive = ReferenceEquals(device, SelectedDevice) && SelectedTabIndex == 2;
        }
    }

    private void OnUi(Action action)
    {
        if (_ui is null || SynchronizationContext.Current == _ui)
        {
            action();
        }
        else
        {
            _ui.Post(_ => action(), null);
        }
    }
}
