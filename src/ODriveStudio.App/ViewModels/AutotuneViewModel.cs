using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ODrive.Model;
using ODrive.Tuning.Autotune;
using ODrive.Tuning.Commissioning;

namespace ODriveStudio.App.ViewModels;

/// <summary>Un gain : valeur de la carte et valeur proposée par l'autotune.</summary>
public sealed partial class GainRowViewModel(string name, string unit, string current, string proposed) : ObservableObject
{
    public string Name { get; } = name;
    public string Unit { get; } = unit;
    public string Proposed { get; } = proposed;

    [ObservableProperty] private string _current = current;
}

/// <summary>
/// Écran « Autotune » (SPEC §7, docs/AUTOTUNE.md) : identification de l'inertie et des frottements, gains de
/// position et de vitesse proposés pour une raideur choisie, validation par échelons. Rien n'est écrit dans la
/// carte sans « Appliquer » ; rien n'est sauvegardé sans « Sauvegarder dans la carte ».
/// </summary>
public sealed partial class AutotuneViewModel : ObservableObject, IDisposable
{
    private readonly DeviceSession _session;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private CancellationTokenSource? _run;
    private AutotuneResult? _result;

    public AutotuneViewModel(DeviceSession session) => _session = session;

    /// <summary>Attente entre lectures et horloge des relevés (remplacées dans les tests par celles du simulateur).</summary>
    public DelayAsync Delay { get; set; } = Task.Delay;
    public Func<TimeSpan>? Clock { get; set; }
    public TimeSpan SamplePeriod { get; set; } = TimeSpan.Zero;

    public IReadOnlyList<int> Axes { get; } = [0, 1];
    public IReadOnlyList<string> Stiffnesses { get; } = ["Souple", "Normale", "Raide"];

    [ObservableProperty] private int _axis;
    [ObservableProperty] private string _stiffness = "Normale";

    /// <summary>Vitesse à laquelle un palier de couple est coupé, en tours/s.</summary>
    [ObservableProperty] private double _maxSpeed = 2;

    /// <summary>Couple des paliers d'identification, en % du couple disponible.</summary>
    [ObservableProperty] private double _testTorquePercent = 10;

    /// <summary>Échelon de validation en vitesse, en tours/s.</summary>
    [ObservableProperty] private double _velocityStep = 0.5;

    /// <summary>Échelon de validation en position, en tours.</summary>
    [ObservableProperty] private double _positionStep = 0.1;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(CancelCommand), nameof(ApplyCommand), nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isRunning;

    public bool IsIdle => !IsRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _hasResult;

    [ObservableProperty] private string _message = "Roue soulevée ou libre de tourner : l'autotune fait tourner le moteur.";
    [ObservableProperty] private string _inertia = "—";
    [ObservableProperty] private string _friction = "—";
    [ObservableProperty] private string _fitQuality = "—";
    [ObservableProperty] private string _validation = "";

    public ObservableCollection<string> Log { get; } = [];
    public ObservableCollection<GainRowViewModel> Gains { get; } = [];

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        IsRunning = true;
        HasResult = false;
        _result = null;
        Gains.Clear();
        Log.Clear();
        _run = new CancellationTokenSource();
        var options = new AutotuneOptions
        {
            Axis = Axis,
            Stiffness = Stiffness switch { "Souple" => ODrive.Tuning.Autotune.Stiffness.Soft, "Raide" => ODrive.Tuning.Autotune.Stiffness.Stiff, _ => ODrive.Tuning.Autotune.Stiffness.Normal },
            MaxSpeed = MaxSpeed,
            TestTorqueFraction = TestTorquePercent / 100,
            VelocityStep = VelocityStep,
            PositionStep = PositionStep,
            SamplePeriod = SamplePeriod,
            Delay = Delay,
            Clock = Clock,
            Progress = new Progress(this),
        };
        try
        {
            await _session.RunAsync("Autotune", async () =>
            {
                Say("Autotune en cours : garder les mains loin de la roue.");
                (AutotuneResult? result, string? refusal) = await Autotuner.RunAsync(_session.Device, options, _session.Guard,
                    question => _session.Dialogs.Confirm("Mise en mouvement", question), _run.Token);
                if (result is null)
                {
                    Say(refusal ?? "Autotune refusé.");
                    return;
                }
                Show(result);
            });
        }
        catch (OperationCanceledException)
        {
            Say("Autotune annulé : moteur arrêté, réglages d'origine restaurés.");
        }
        finally
        {
            _run.Dispose();
            _run = null;
            IsRunning = false;
        }
    }

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _run?.Cancel();

    private bool CanApply() => HasResult && !IsRunning;

    /// <summary>Écrit les gains proposés dans la carte (sans sauvegarder).</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        if (_result is not { } result)
        {
            return;
        }
        await _session.RunAsync("Application des gains", async () =>
        {
            await Autotuner.ApplyAsync(_session.Device, Axis, result.Proposed);
            foreach (GainRowViewModel row in Gains)
            {
                row.Current = row.Proposed;
            }
            Say("Gains appliqués (non sauvegardés) : « Sauvegarder dans la carte » pour les garder après redémarrage.");
        });
    }

    private bool CanSave() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        await _session.RunAsync("Sauvegarde", async () =>
        {
            SafetyDecision decision = await SafetyGuard.AuthorizeConfigurationChangeAsync(_session.Device);
            if (!decision.IsAllowed)
            {
                Say("Sauvegarde refusée : " + decision.Reason);
                return;
            }
            if (!_session.Dialogs.Confirm("Sauvegarder dans la carte",
                    "La configuration actuelle va être écrite en mémoire permanente, puis la carte redémarre.\n\nContinuer ?"))
            {
                return;
            }
            Say(await _session.Device.SaveConfigurationAsync()
                ? "Sauvegarde envoyée : la carte redémarre. Vérifier les gains après reconnexion."
                : "La carte a refusé la sauvegarde : un moteur est armé.");
        });
    }

    /// <summary>Annule un autotune en cours si la vue disparaît (carte débranchée, fermeture).</summary>
    public void Dispose() => _run?.Cancel();

    private void Show(AutotuneResult result)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        _result = result;
        Inertia = result.Plant.Inertia.ToString("G4", inv) + " Nm/(tour/s²)";
        Friction = string.Create(inv, $"sec {result.Plant.Coulomb:G3} Nm, visqueux {result.Plant.Viscous:G3} Nm/(tour/s)");
        FitQuality = string.Create(inv, $"R² = {result.Plant.RSquared:F3} sur {result.Plant.SampleCount} relevés");
        Gains.Clear();
        Gains.Add(new GainRowViewModel("vel_gain", "Nm/(tour/s)", Format(result.Initial.VelGain), Format(result.Proposed.VelGain)));
        Gains.Add(new GainRowViewModel("vel_integrator_gain", "Nm/((tour/s)·s)", Format(result.Initial.VelIntegratorGain),
            Format(result.Proposed.VelIntegratorGain)));
        Gains.Add(new GainRowViewModel("pos_gain", "(tour/s)/tour", Format(result.Initial.PosGain), Format(result.Proposed.PosGain)));
        Validation = string.Create(inv,
            $"Échelon de vitesse : dépassement {result.VelocityStep.Overshoot:P0}, établi en {Seconds(result.VelocityStep.SettlingTime)}"
            + $"{(result.VelocityStep.Oscillating ? ", OSCILLE" : "")}.\n"
            + $"Échelon de position : dépassement {result.PositionStep.Overshoot:P0}, établi en {Seconds(result.PositionStep.SettlingTime)}"
            + $"{(result.PositionStep.Oscillating ? ", OSCILLE" : "")}.\n"
            + $"Bande passante de la boucle de vitesse : {result.Proposed.Bandwidth:F1} rad/s (actuelle : {result.Initial.Bandwidth:F1} rad/s).");
        HasResult = true;
        Say(result.Validated
            ? "Autotune terminé : gains proposés et validés. « Appliquer » pour les écrire dans la carte."
            : "Autotune terminé, mais la validation n'est pas satisfaisante : appliquer avec prudence.");
    }

    private static string Format(double value) => value.ToString("G4", CultureInfo.InvariantCulture);

    private static string Seconds(double? value) =>
        value is { } s ? s.ToString("F3", CultureInfo.InvariantCulture) + " s" : "non établi";

    private void Say(string message)
    {
        Message = message;
        _session.Status(message);
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

    private sealed class Progress(AutotuneViewModel owner) : IProgress<string>
    {
        public void Report(string value) => owner.OnUi(() =>
        {
            owner.Log.Add(DateTime.Now.ToString("HH:mm:ss  ", CultureInfo.InvariantCulture) + value);
            owner.Message = value;
        });
    }
}
