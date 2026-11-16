using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ODrive.Model;
using ODrive.Protocol;
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
        SerialNumber = session.Device.SerialNumber.ToString("X", CultureInfo.InvariantCulture);
    }

    public DeviceSession Session { get; }
    public ODriveDevice Device => Session.Device;
    public DashboardViewModel Dashboard { get; }
    public ExplorerViewModel Explorer { get; }
    public string SerialNumber { get; }

    /// <summary>Faux pour une carte dont la zone OTP n'a pas été programmée par ODrive Robotics (clone) : odrivetool l'appelle « dev0 ».</summary>
    [ObservableProperty] private bool _isGenuine = true;
    [ObservableProperty] private string _firmwareVersion = "";
    [ObservableProperty] private string _hardwareVersion = "";
    [ObservableProperty] private string _title = "";

    /// <summary>Vrai quand l'onglet Explorateur est affiché : ses valeurs ne sont relues que dans ce cas.</summary>
    public bool ExplorerActive { get; set; }

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
        _polling.Cancel();
        _polling.Dispose();
    }
}

/// <summary>Fenêtre principale : cartes connectées, arrêt d'urgence, journal et barre d'état.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly DeviceManager _manager;
    private readonly WriteJournal _journal;
    private readonly SafetyGuard _guard;
    private readonly IUserDialogs _dialogs;
    private readonly SynchronizationContext? _ui;
    private readonly Func<string?> _driverHint;

    /// <param name="driverHint">Diagnostic du pilote USB quand aucune carte n'est accessible (SPEC §1), ou <c>null</c>.</param>
    public MainViewModel(DeviceManager manager, WriteJournal journal, SafetyGuard guard, IUserDialogs dialogs,
        Func<string?>? driverHint = null)
    {
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

    /// <summary>Période de rafraîchissement des vues d'une carte.</summary>
    public TimeSpan PollingPeriod { get; init; } = TimeSpan.FromMilliseconds(200);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDevice))]
    private DeviceViewModel? _selectedDevice;

    [ObservableProperty] private string _statusMessage = "Recherche d'une carte ODrive…";
    [ObservableProperty] private string _connectionHint = "";
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

    /// <summary>Met à jour l'aide affichée quand aucune carte n'est connectée (erreurs d'ouverture, pilote).</summary>
    public void UpdateConnectionHint()
    {
        if (Devices.Count > 0)
        {
            ConnectionHint = "";
            return;
        }

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
