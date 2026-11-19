using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ODrive.Model;
using ODrive.Protocol;
using ODrive.Tuning.Commissioning;
using ODrive.Tuning.Profiles;

namespace ODriveStudio.App.ViewModels;

/// <summary>Une valeur proposée par l'assistant : modifiable, comparée à celle de la carte.</summary>
public sealed partial class SettingRowViewModel : ObservableObject
{
    private readonly EnumMetadata? _enum;

    public SettingRowViewModel(CommissioningSetting setting, PropertyNode? property)
    {
        Setting = setting;
        Property = property;
        _enum = ODriveInterface.FindProperty(setting.Path)?.EnumType is { } type ? ODriveEnums.Find(type) : null;
        _editText = Display(property is null ? setting.Value : HoverboardPreset.ToEndpointValue(property.Type, setting.Value));
        StepName = setting.Step switch
        {
            CommissioningStep.Power => "1. Alimentation",
            CommissioningStep.Motor => "2. Moteur",
            CommissioningStep.Encoder => "3. Capteur",
            CommissioningStep.Controller => "4. Régulation",
            CommissioningStep.Limits => "5. Limites",
            _ => "6. Communication",
        };
        if (property is null)
        {
            _message = "absente de cette carte";
        }
    }

    public CommissioningSetting Setting { get; }
    public PropertyNode? Property { get; }
    public string StepName { get; }
    public string Label => Setting.Label;
    public string Path => Setting.Path;
    public string Unit => Setting.Unit;
    public string Note => Setting.NeedsReboot && Setting.Note.Length == 0 ? "redémarrage nécessaire" : Setting.Note;

    /// <summary>Valeur à écrire, modifiable (nombre, true/false, ou nom d'enum).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDifferent))]
    private string _editText;

    /// <summary>Valeur actuelle de la carte.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDifferent))]
    private string _currentValue = "";

    [ObservableProperty] private string _message = "";

    /// <summary>Vrai si la carte n'a pas (encore) la valeur proposée.</summary>
    public bool IsDifferent => Property is not null && !SameValue(EditText, CurrentValue);

    /// <summary>Valeur saisie, typée pour l'endpoint.</summary>
    /// <exception cref="FormatException">Saisie invalide ; le message est affichable.</exception>
    public object Parse() => ValueText.Parse(EditText, Property!.Type, _enum);

    public void SetCurrent(object value) => CurrentValue = Display(value);

    private string Display(object value)
    {
        if (_enum is not null && !_enum.IsFlags && value is byte or ushort or uint or ulong
            && _enum.Decode(Convert.ToUInt64(value, CultureInfo.InvariantCulture)).FirstOrDefault() is { } named)
        {
            return named.Name;
        }
        return value is float f ? f.ToString("G6", CultureInfo.InvariantCulture) : ConfigProfile.FormatValue(value);
    }

    private bool SameValue(string edited, string current)
    {
        if (string.Equals(edited.Trim(), current, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        try
        {
            return Display(Parse()) == current;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

/// <summary>Un point de la liste de contrôle de la mise en service.</summary>
public sealed partial class ChecklistItemViewModel(string text) : ObservableObject
{
    public string Text { get; } = text;

    [ObservableProperty] private bool _isDone;
}

/// <summary>
/// Assistant de mise en service (SPEC §4) pour un moteur roue d'hoverboard : valeurs proposées par étape
/// (docs/HOVERBOARD.md), toutes modifiables avant écriture, puis sauvegarde, calibrations avec contrôle de
/// plausibilité, et mémorisation de la calibration. Chaque étape peut être refaite dans n'importe quel ordre.
/// </summary>
public sealed partial class CommissioningViewModel : ObservableObject, IDisposable
{
    private readonly DeviceSession _session;
    private CancellationTokenSource? _calibration;
    private bool _savedSinceLastWrite = true;

    public CommissioningViewModel(DeviceSession session)
    {
        _session = session;
        Checklist =
        [
            new ChecklistItemViewModel("Configuration écrite dans la carte"),
            new ChecklistItemViewModel("Configuration sauvegardée et carte redémarrée"),
            new ChecklistItemViewModel("Moteur calibré (résistance et inductance)"),
            new ChecklistItemViewModel("Capteur calibré (polarité Hall et offset)"),
            new ChecklistItemViewModel("Calibration mémorisée (pre_calibrated)"),
        ];
        Propose();
    }

    public ObservableCollection<SettingRowViewModel> Settings { get; } = [];
    public ObservableCollection<ChecklistItemViewModel> Checklist { get; }
    public ObservableCollection<string> Log { get; } = [];

    /// <summary>Attente entre deux lectures d'état pendant une calibration (remplacée dans les tests).</summary>
    public DelayAsync Delay { get; set; } = Task.Delay;

    public IReadOnlyList<int> Axes { get; } = [0, 1];

    [ObservableProperty] private int _axis;

    /// <summary>Constante de vitesse du moteur, en tr/min par volt.</summary>
    [ObservableProperty] private float _kv = 16f;
    [ObservableProperty] private int _polePairs = 15;
    [ObservableProperty] private bool _brakeResistorConnected;

    /// <summary>Résistance de freinage, en ohms.</summary>
    [ObservableProperty] private float _brakeResistance = 2f;

    /// <summary>
    /// Courant que l'alimentation accepte en retour au freinage, en ampères (positif ; 0 pour une alimentation
    /// de laboratoire). Devient <c>config.dc_max_negative_current</c>.
    /// </summary>
    [ObservableProperty] private float _regenCurrent;

    /// <summary>Avertissement sur le freinage, vide si la configuration tient debout.</summary>
    [ObservableProperty] private string _supplyWarning = "";

    /// <summary>Limite de courant moteur, en ampères.</summary>
    [ObservableProperty] private float _currentLimit = 10f;

    /// <summary>Limite de vitesse, en tours/s.</summary>
    [ObservableProperty] private float _velocityLimit = 10f;

    /// <summary>Rampe de vitesse, en tours/s².</summary>
    [ObservableProperty] private float _velocityRampRate = 5f;
    [ObservableProperty] private bool _enableWatchdog;

    /// <summary>Démarrage automatique en boucle fermée à la mise sous tension.</summary>
    [ObservableProperty] private bool _startupClosedLoop;

    [ObservableProperty] private string _optionsMessage = "";
    [ObservableProperty] private bool _rebootNeeded;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCalibrationCommand), nameof(CancelCalibrationCommand), nameof(ApplyCommand),
        nameof(SaveAndRebootCommand), nameof(MarkPreCalibratedCommand))]
    private bool _isCalibrating;

    [ObservableProperty] private string _calibrationState = "";

    /// <summary>Recalcule les valeurs proposées à partir des choix (préréglage hoverboard).</summary>
    [RelayCommand]
    private void Propose()
    {
        IReadOnlyList<CommissioningSetting> settings;
        try
        {
            settings = HoverboardPreset.Build(new HoverboardOptions
            {
                Axis = Axis,
                Kv = Kv,
                PolePairs = PolePairs,
                BrakeResistorConnected = BrakeResistorConnected,
                BrakeResistance = BrakeResistance,
                // Jamais plus près de zéro que le défaut du firmware (odrive_main.h:111).
                DcMaxNegativeCurrent = -Math.Max(Math.Abs(RegenCurrent), 0.01f),
                CurrentLimit = CurrentLimit,
                VelocityLimit = VelocityLimit,
                VelocityRampRate = VelocityRampRate,
                EnableWatchdog = EnableWatchdog,
            });
        }
        catch (ArgumentOutOfRangeException)
        {
            OptionsMessage = "Kv et le nombre de paires de pôles doivent être positifs.";
            return;
        }

        OptionsMessage = "";
        // low_level.cpp:374 : le moindre courant renvoyé vers l'alimentation au-delà de dc_max_negative_current
        // désarme les deux axes. Sans résistance de freinage, il faut donc que l'alimentation en accepte.
        SupplyWarning = BrakeResistorConnected || Math.Abs(RegenCurrent) >= 0.5f
            ? ""
            : "Sans résistance de freinage, l'axe tombera en DC_BUS_OVER_REGEN_CURRENT au premier freinage. "
              + "Sur batterie : indiquer le courant de recharge qu'elle accepte. Sur alimentation de laboratoire "
              + "(aucune recharge possible) : brancher une résistance de freinage.";
        Settings.Clear();
        // Dans l'ordre des étapes de l'assistant (tri stable : l'ordre du préréglage est gardé dans une étape).
        foreach (CommissioningSetting setting in settings.OrderBy(s => s.Step))
        {
            Settings.Add(new SettingRowViewModel(setting, _session.Device.Schema.FindProperty(setting.Path)));
        }
    }

    partial void OnAxisChanged(int value) => Propose();

    /// <summary>Relit sur la carte les valeurs des lignes et l'état d'avancement.</summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        foreach (SettingRowViewModel row in Settings.ToList())
        {
            if (row.Property is { } property)
            {
                row.SetCurrent(await _session.Device.ReadAsync(property, ct));
            }
        }

        Checklist[0].IsDone = Settings.Count > 0 && Settings.All(row => row.Property is null || !row.IsDifferent);
        Checklist[1].IsDone = Checklist[0].IsDone && _savedSinceLastWrite && !RebootNeeded;
        Checklist[2].IsDone = await ReadAsync<bool>("motor.is_calibrated", ct);
        Checklist[3].IsDone = await ReadAsync<bool>("encoder.is_ready", ct);
        Checklist[4].IsDone = await ReadAsync<bool>("motor.config.pre_calibrated", ct)
            && await ReadAsync<bool>("encoder.config.pre_calibrated", ct);
    }

    /// <summary>Annule une calibration en cours si la vue disparaît (carte débranchée, fermeture).</summary>
    public void Dispose() => _calibration?.Cancel();

    private bool NotCalibrating() => !IsCalibrating;

    /// <summary>Écrit dans la carte les valeurs qui diffèrent (rien n'est sauvegardé en flash).</summary>
    [RelayCommand(CanExecute = nameof(NotCalibrating))]
    private async Task ApplyAsync()
    {
        await _session.RunAsync("Écriture de la configuration", async () =>
        {
            if (!await IdleAsync("Écriture de la configuration"))
            {
                return;
            }

            await RefreshAsync();
            int written = 0, invalid = 0;
            foreach (SettingRowViewModel row in Settings)
            {
                row.Message = row.Property is null ? "absente de cette carte" : "";
                if (row.Property is not { } property || !row.IsDifferent)
                {
                    continue;
                }

                try
                {
                    await _session.Device.WriteAsync(property, row.Parse());
                    written++;
                    _savedSinceLastWrite = false;
                    RebootNeeded |= row.Setting.NeedsReboot;
                }
                catch (FormatException e)
                {
                    row.Message = e.Message;
                    invalid++;
                }
            }

            await RefreshAsync();
            Say(invalid > 0
                ? $"{written} valeur(s) écrite(s), {invalid} saisie(s) invalide(s) à corriger."
                : written == 0
                    ? "La carte a déjà toutes ces valeurs."
                    : $"{written} valeur(s) écrite(s)."
                      + (RebootNeeded ? " Certaines ne prennent effet qu'après sauvegarde et redémarrage." : ""));
        });
    }

    /// <summary>Sauvegarde la configuration en flash ; la carte redémarre.</summary>
    [RelayCommand(CanExecute = nameof(NotCalibrating))]
    private async Task SaveAndRebootAsync()
    {
        await _session.RunAsync("Sauvegarde", async () =>
        {
            if (!await IdleAsync("Sauvegarde")
                || !_session.Dialogs.Confirm("Sauvegarder dans la carte",
                    "La configuration actuelle va être écrite en mémoire permanente, puis la carte redémarre.\n\nContinuer ?"))
            {
                return;
            }

            if (await _session.Device.SaveConfigurationAsync())
            {
                _savedSinceLastWrite = true;
                RebootNeeded = false;
                Say("Configuration sauvegardée : la carte redémarre et se reconnecte.");
            }
            else
            {
                Say("La carte a refusé la sauvegarde : un moteur est armé.");
            }
        });
    }

    /// <summary>
    /// Lance une calibration. Paramètre : <c>motor</c>, <c>hall</c>, <c>offset</c> ou <c>full</c> (séquence
    /// complète : moteur, polarité Hall, offset).
    /// </summary>
    [RelayCommand(CanExecute = nameof(NotCalibrating))]
    private async Task RunCalibrationAsync(string? kind)
    {
        (AxisState state, string name, double timeout) = kind switch
        {
            "motor" => (AxisState.MotorCalibration, "Calibration du moteur", 20d),
            "hall" => (AxisState.EncoderHallPolarityCalibration, "Calibration de la polarité Hall", 30d),
            "offset" => (AxisState.EncoderOffsetCalibration, "Calibration de l'offset du capteur", 40d),
            _ => (AxisState.FullCalibrationSequence, "Calibration complète", 90d),
        };
        if (RebootNeeded)
        {
            Say($"{name} : sauvegarder et redémarrer d'abord, des réglages écrits ne sont pas encore actifs.");
            return;
        }

        IsCalibrating = true;
        _calibration = new CancellationTokenSource();
        var progress = new Progress<AxisState>(current => CalibrationState = StateName(current));
        try
        {
            await _session.RunAsync(name, async () =>
            {
                AxisRunResult result = await AxisStateRunner.RunAsync(_session.Device, Axis, state,
                    TimeSpan.FromSeconds(timeout), _session.Guard,
                    question => _session.Dialogs.Confirm("Mise en mouvement", question), progress, Delay,
                    _calibration.Token);
                if (!result.Success)
                {
                    Say($"{name} : {result.Message}");
                    return;
                }
                await ReportCalibrationAsync(state, name);
            });
        }
        catch (OperationCanceledException)
        {
            Say($"{name} annulée : l'axe a été remis en IDLE.");
        }
        finally
        {
            _calibration.Dispose();
            _calibration = null;
            IsCalibrating = false;
            CalibrationState = "";
        }
        await _session.RunAsync("Relecture", () => RefreshAsync());
    }

    private bool CanCancel() => IsCalibrating;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void CancelCalibration() => _calibration?.Cancel();

    /// <summary>Mémorise la calibration (<c>pre_calibrated</c>) pour qu'elle survive au redémarrage.</summary>
    [RelayCommand(CanExecute = nameof(NotCalibrating))]
    private async Task MarkPreCalibratedAsync()
    {
        await _session.RunAsync("Mémorisation de la calibration", async () =>
        {
            if (!await ReadAsync<bool>("motor.is_calibrated") || !await ReadAsync<bool>("encoder.is_ready"))
            {
                Say("Mémorisation refusée : le moteur et le capteur doivent d'abord être calibrés sans erreur.");
                return;
            }

            await WriteAsync("motor.config.pre_calibrated", true);
            await WriteAsync("encoder.config.pre_calibrated", true);
            await WriteAsync("config.startup_closed_loop_control", StartupClosedLoop);
            _savedSinceLastWrite = false;
            Say("Calibration mémorisée" + (StartupClosedLoop ? ", démarrage automatique en boucle fermée activé" : "")
                + ". Sauvegarder dans la carte pour la conserver.");
            await RefreshAsync();
        });
    }

    /// <summary>Exporte la configuration de la carte dans un profil JSON.</summary>
    [RelayCommand]
    private async Task ExportProfileAsync()
    {
        string? path = _session.Dialogs.PickSaveFile("Exporter le profil de la carte",
            $"odrive-profil-{DateTime.Now:yyyyMMdd-HHmmss}.json", "Profil ODrive Studio (*.json)|*.json");
        if (path is null)
        {
            return;
        }

        await _session.RunAsync("Export du profil", async () =>
        {
            ConfigProfile profile = await ConfigProfile.CaptureAsync(_session.Device,
                $"Assistant hoverboard, axe {Axis}, Kv {Kv.ToString(CultureInfo.InvariantCulture)}");
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

    // La séquence est revenue en IDLE sans erreur : vérifier qu'elle est allée au bout (un arrêt d'urgence
    // ramène aussi en IDLE sans erreur), puis juger les mesures.
    private async Task ReportCalibrationAsync(AxisState state, string name)
    {
        bool motorDone = await ReadAsync<bool>("motor.is_calibrated");
        bool encoderDone = await ReadAsync<bool>("encoder.is_ready");
        bool complete = state switch
        {
            AxisState.MotorCalibration => motorDone,
            AxisState.EncoderHallPolarityCalibration => await ReadAsync<bool>("encoder.config.hall_polarity_calibrated"),
            AxisState.EncoderOffsetCalibration => encoderDone,
            _ => motorDone && encoderDone,
        };
        if (!complete)
        {
            Say($"{name} interrompue avant la fin (arrêt d'urgence ?) : à relancer.");
            return;
        }

        Say($"{name} terminée sans erreur.");
        if (state is AxisState.MotorCalibration or AxisState.FullCalibrationSequence)
        {
            float resistance = await ReadAsync<float>("motor.config.phase_resistance");
            float inductance = await ReadAsync<float>("motor.config.phase_inductance");
            Say(string.Create(CultureInfo.InvariantCulture,
                $"Résistance de phase : {resistance:F3} Ω, inductance de phase : {inductance * 1000:F3} mH."));
            Say(CalibrationChecks.HoverboardMotor(resistance, inductance) ?? "Mesures plausibles pour une roue d'hoverboard.");
        }
        if (state is AxisState.EncoderOffsetCalibration or AxisState.FullCalibrationSequence)
        {
            float offset = await ReadAsync<float>("encoder.config.phase_offset_float");
            Say(string.Create(CultureInfo.InvariantCulture, $"Offset de phase du capteur : {offset:F3}."));
            Say(CalibrationChecks.HallPhaseOffset(offset) ?? "Offset plausible pour des capteurs Hall (0,5 modulo 1).");
        }
    }

    private async Task<bool> IdleAsync(string what)
    {
        SafetyDecision decision = await SafetyGuard.AuthorizeConfigurationChangeAsync(_session.Device);
        if (!decision.IsAllowed)
        {
            Say($"{what} refusée : {decision.Reason}");
        }
        return decision.IsAllowed;
    }

    private string AxisPath(string path) => string.Create(CultureInfo.InvariantCulture, $"axis{Axis}.{path}");

    private Task<T> ReadAsync<T>(string path, CancellationToken ct = default) =>
        _session.Device.ReadAsync<T>(_session.Device.Schema.GetProperty(AxisPath(path)), ct);

    private Task WriteAsync(string path, object value) =>
        _session.Device.WriteAsync(_session.Device.Schema.GetProperty(AxisPath(path)), value);

    private static string StateName(AxisState state) => state switch
    {
        AxisState.MotorCalibration => "Calibration du moteur en cours…",
        AxisState.EncoderHallPolarityCalibration => "Calibration de la polarité Hall en cours…",
        AxisState.EncoderOffsetCalibration => "Calibration de l'offset du capteur en cours (le moteur tourne)…",
        AxisState.Idle => "",
        _ => state + "…",
    };

    private void Say(string message)
    {
        Log.Add(string.Create(CultureInfo.InvariantCulture, $"{DateTime.Now:HH:mm:ss}  {message}"));
        _session.Status(message);
    }
}
