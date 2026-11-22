using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ODrive.Model;
using ODrive.Protocol;

namespace ODriveStudio.App.ViewModels;

/// <summary>État d'un axe sur le tableau de bord (SPEC §2). Les unités sont toujours affichées.</summary>
public sealed partial class AxisDashboardViewModel : ObservableObject
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly DeviceSession _session;
    private readonly Axis _axis;

    public AxisDashboardViewModel(DeviceSession session, Axis axis)
    {
        _session = session;
        _axis = axis;
        Name = axis.Path;
    }

    public string Name { get; }

    [ObservableProperty] private string _currentState = "—";
    [ObservableProperty] private bool _isArmed;
    [ObservableProperty] private string _controlMode = "—";
    [ObservableProperty] private string _inputMode = "—";
    [ObservableProperty] private string _setpoint = "—";
    [ObservableProperty] private string _velocity = "—";
    [ObservableProperty] private string _position = "—";
    [ObservableProperty] private string _iqMeasured = "—";
    [ObservableProperty] private string _iqSetpoint = "—";
    [ObservableProperty] private string _fetTemperature = "—";
    [ObservableProperty] private string _motorTemperature = "—";

    public async Task RefreshAsync(CancellationToken ct)
    {
        AxisState state = await _axis.CurrentState.GetAsync(ct);
        CurrentState = ValueText.Format((byte)state, ODriveEnums.Get<AxisState>());
        IsArmed = await _axis.Motor.IsArmed.GetAsync(ct);

        ControlMode mode = await _axis.Controller.Config.ControlMode.GetAsync(ct);
        ControlMode = ValueText.Format((byte)mode, ODriveEnums.Get<ControlMode>());
        InputMode = ValueText.Format((byte)await _axis.Controller.Config.InputMode.GetAsync(ct), ODriveEnums.Get<InputMode>());

        // La consigne affichée est celle du mode de contrôle en cours.
        Setpoint = mode switch
        {
            ODrive.Model.ControlMode.PositionControl => Format(await _axis.Controller.InputPos.GetAsync(ct), "F3", "tours"),
            ODrive.Model.ControlMode.VelocityControl => Format(await _axis.Controller.InputVel.GetAsync(ct), "F3", "tours/s"),
            ODrive.Model.ControlMode.TorqueControl => Format(await _axis.Controller.InputTorque.GetAsync(ct), "F3", "Nm"),
            _ => "—",
        };

        Velocity = Format(await _axis.Encoder.VelEstimate.GetAsync(ct), "F3", "tours/s");
        Position = Format(await _axis.Encoder.PosEstimate.GetAsync(ct), "F3", "tours");
        IqMeasured = Format(await _axis.Motor.CurrentControl.IqMeasured.GetAsync(ct), "F2", "A");
        IqSetpoint = Format(await _axis.Motor.CurrentControl.IqSetpoint.GetAsync(ct), "F2", "A");
        FetTemperature = Format(await _axis.Motor.FetThermistor.Temperature.GetAsync(ct), "F1", "°C");
        MotorTemperature = await _axis.Motor.MotorThermistor.Config.Enabled.GetAsync(ct)
            ? Format(await _axis.Motor.MotorThermistor.Temperature.GetAsync(ct), "F1", "°C")
            : "thermistance non configurée";
    }

    [RelayCommand]
    private Task IdleAsync() => _session.RunAsync($"{Name} → IDLE", async () =>
    {
        await _axis.RequestedState.SetAsync(AxisState.Idle);
        _session.Status($"{Name} : IDLE demandé.");
    });

    [RelayCommand]
    private Task ClosedLoopAsync() => _session.RunAsync($"{Name} → CLOSED_LOOP_CONTROL", async () =>
    {
        if (await _session.WriteGuardedAsync(_axis.RequestedState.Node, AxisState.ClosedLoopControl))
        {
            _session.Status($"{Name} : CLOSED_LOOP_CONTROL demandé.");
        }
    });

    private static string Format(float value, string format, string unit) =>
        float.IsFinite(value) ? $"{value.ToString(format, Inv)} {unit}" : $"{ValueText.Format(value, null)} {unit}";
}

/// <summary>Tableau de bord d'une carte : bus, axes, erreurs décodées et commandes globales (SPEC §2).</summary>
public sealed partial class DashboardViewModel : ObservableObject
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly DeviceSession _session;

    public DashboardViewModel(DeviceSession session)
    {
        _session = session;
        ODrive3 odrv = session.Odrv;
        Axes = [new AxisDashboardViewModel(session, odrv.Axis0), new AxisDashboardViewModel(session, odrv.Axis1)];
    }

    public IReadOnlyList<AxisDashboardViewModel> Axes { get; }
    public ObservableCollection<ActiveError> Errors { get; } = [];

    [ObservableProperty] private string _busVoltage = "—";
    [ObservableProperty] private string _busCurrent = "—";
    [ObservableProperty] private string _brakeResistor = "—";
    [ObservableProperty] private bool _hasErrors;

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        ODrive3 odrv = _session.Odrv;
        BusVoltage = $"{(await odrv.VbusVoltage.GetAsync(ct)).ToString("F2", Inv)} V";
        BusCurrent = $"{(await odrv.Ibus.GetAsync(ct)).ToString("F2", Inv)} A";

        if (!await odrv.Config.EnableBrakeResistor.GetAsync(ct))
        {
            BrakeResistor = "désactivée";
        }
        else if (await odrv.BrakeResistorSaturated.GetAsync(ct))
        {
            BrakeResistor = "saturée";
        }
        else
        {
            BrakeResistor = await odrv.BrakeResistorArmed.GetAsync(ct) ? "armée" : "désarmée";
        }

        foreach (AxisDashboardViewModel axis in Axes)
        {
            await axis.RefreshAsync(ct);
        }

        IReadOnlyList<ActiveError> errors = await ErrorDecoder.ReadAllAsync(_session.Device, ct);
        if (!errors.SequenceEqual(Errors))
        {
            Errors.Clear();
            foreach (ActiveError error in errors)
            {
                Errors.Add(error);
            }
        }
        HasErrors = Errors.Count > 0;
    }

    [RelayCommand]
    private Task ClearErrorsAsync() => _session.RunAsync("Effacement des erreurs", async () =>
    {
        await _session.Odrv.ClearErrorsAsync();
        _session.Status("Erreurs effacées.");
    });

    /// <summary>
    /// Relevé complet de la carte : toutes les propriétés et le JSON d'endpoints, dans le même format que
    /// <c>odrive-cli dump</c>. Lecture seule ; sert de sauvegarde avant modification et de base de comparaison.
    /// </summary>
    [RelayCommand]
    private async Task DumpAsync()
    {
        string? path = _session.Dialogs.PickSaveFile("Relevé complet de la carte",
            $"odrive-{_session.Device.SerialNumber:X}-{DateTime.Now:yyyyMMdd-HHmmss}.dump.json",
            "Relevé ODrive (*.dump.json)|*.dump.json|JSON (*.json)|*.json");
        if (path is null)
        {
            return;
        }

        await _session.RunAsync("Relevé complet", async () =>
        {
            int total = _session.Device.Schema.Properties.Count;
            DeviceDump dump = await DeviceDump.CaptureAsync(_session.Device);
            try
            {
                await using FileStream stream = File.Create(path);
                dump.Save(stream);
                _session.Status($"Relevé complet écrit : {path} ({total} propriétés).");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                _session.Status("Relevé complet impossible à écrire : " + e.Message);
            }
        });
    }

    [RelayCommand]
    private Task RebootAsync() => ConfigurationChangeAsync("Redémarrer la carte",
        "Redémarrer la carte ? Les réglages non sauvegardés seront perdus.",
        async () =>
        {
            await _session.Device.RebootAsync();
            _session.Status("Redémarrage demandé : reconnexion automatique…");
        });

    [RelayCommand]
    private Task SaveConfigurationAsync() => ConfigurationChangeAsync("Sauvegarder la configuration",
        "Enregistrer la configuration dans la carte ? La carte redémarre ensuite.",
        async () =>
        {
            bool saved = await _session.Device.SaveConfigurationAsync();
            _session.Status(saved
                ? "Configuration sauvegardée : la carte redémarre, reconnexion automatique…"
                : "La carte a refusé la sauvegarde (un moteur est armé).");
        });

    [RelayCommand]
    private Task EraseConfigurationAsync() => ConfigurationChangeAsync("Effacer la configuration",
        "Effacer TOUTE la configuration de la carte et revenir aux réglages d'usine ?\n"
        + "Les calibrations seront perdues. La carte redémarre ensuite.",
        async () =>
        {
            await _session.Device.EraseConfigurationAsync();
            _session.Status("Configuration effacée : la carte redémarre, reconnexion automatique…");
        });

    // Save / Erase / Reboot : confirmation, et jamais pendant qu'un axe est actif (docs/SAFETY.md).
    private Task ConfigurationChangeAsync(string title, string question, Func<Task> action) =>
        _session.RunAsync(title, async () =>
        {
            SafetyDecision decision = await SafetyGuard.AuthorizeConfigurationChangeAsync(_session.Device);
            if (!decision.IsAllowed)
            {
                _session.Status(decision.Reason ?? "Opération refusée.");
                return;
            }
            if (_session.Dialogs.Confirm(title, question))
            {
                await action();
            }
        });
}
