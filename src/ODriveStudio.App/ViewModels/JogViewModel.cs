using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ODrive.Tuning.Commissioning;
using ODrive.Tuning.Jog;

namespace ODriveStudio.App.ViewModels;

/// <summary>
/// Écran « Essai moteur » : jog en vitesse d'un axe, boutons à maintenir (le moteur s'arrête au relâchement)
/// ou vitesse continue. Les limites de l'essai sont temporaires et la carte retrouve ses réglages à la fin
/// (<see cref="JogSession"/>, docs/SAFETY.md).
/// </summary>
public sealed partial class JogViewModel : ObservableObject, IDisposable
{
    private readonly DeviceSession _session;
    private readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    private JogSession? _jog;

    public JogViewModel(DeviceSession session) => _session = session;

    /// <summary>Attente de la surveillance (remplacée dans les tests).</summary>
    public DelayAsync Delay { get; set; } = Task.Delay;

    public IReadOnlyList<int> Axes { get; } = [0, 1];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private int _axis;

    /// <summary>Grandeurs commandables : vitesse (par défaut) ou couple.</summary>
    public IReadOnlyList<string> Modes { get; } = ["Vitesse", "Couple"];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTorqueMode), nameof(IsVelocityMode), nameof(CommandUnit), nameof(CommandLimit),
        nameof(CommandText))]
    private string _mode = "Vitesse";

    public bool IsTorqueMode => Mode == Modes[1];

    public bool IsVelocityMode => !IsTorqueMode;

    /// <summary>Unité de la consigne dans le mode choisi.</summary>
    public string CommandUnit => IsTorqueMode ? "Nm" : "tours/s";

    /// <summary>Plus grande consigne acceptée dans le mode choisi (borne du curseur).</summary>
    public float CommandLimit => Math.Abs(IsTorqueMode ? MaxTorque : MaxVelocity);

    public string CommandText => string.Create(CultureInfo.InvariantCulture, $"Consigne : {Command:F2} {CommandUnit}");

    /// <summary>
    /// Vitesse maximale de l'essai, en tours/s. En mode couple, c'est la vitesse à laquelle le firmware réduit
    /// le couple : une roue libre s'y stabilise.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommandLimit))]
    private float _maxVelocity = 2f;

    /// <summary>Couple maximal commandable en mode couple, en Nm.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommandLimit))]
    private float _maxTorque = 1f;

    /// <summary>Rampe de la consigne de couple, en Nm/s.</summary>
    [ObservableProperty] private float _torqueRampRate = 2f;

    /// <summary>Couple des boutons à maintenir, en Nm.</summary>
    [ObservableProperty] private float _jogTorque = 0.5f;

    /// <summary>Limite de courant pendant l'essai, en ampères.</summary>
    [ObservableProperty] private float _maxCurrent = 10f;

    /// <summary>Accélération de la consigne, en tours/s².</summary>
    [ObservableProperty] private float _rampRate = 2f;

    /// <summary>Vitesse des boutons à maintenir, en tours/s.</summary>
    [ObservableProperty] private float _jogSpeed = 1f;

    /// <summary>Consigne en cours, en tours/s ou en Nm selon le mode.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommandText))]
    private float _command;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(StopCommand), nameof(ZeroCommand))]
    [NotifyPropertyChangedFor(nameof(IsStopped))]
    private bool _isRunning;

    /// <summary>Vrai hors essai : les limites ne se changent qu'à l'arrêt.</summary>
    public bool IsStopped => !IsRunning;

    [ObservableProperty] private string _message = "Essai arrêté.";
    [ObservableProperty] private string _velocity = "—";
    [ObservableProperty] private string _current = "—";
    [ObservableProperty] private string _busVoltage = "—";
    [ObservableProperty] private string _fetTemperature = "—";

    private bool CanStart() => !IsRunning;

    private bool CanStop() => IsRunning;

    /// <summary>Arme l'axe en boucle fermée, consigne nulle.</summary>
    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task StartAsync()
    {
        await _session.RunAsync("Essai moteur", async () =>
        {
            var limits = new JogLimits(MaxVelocity, MaxCurrent, RampRate)
            {
                Mode = IsTorqueMode ? JogMode.Torque : JogMode.Velocity,
                MaxTorque = MaxTorque,
                TorqueRampRate = TorqueRampRate,
            };
            (JogSession? jog, string? refusal) = await JogSession.StartAsync(_session.Device, Axis, limits,
                _session.Guard, question => _session.Dialogs.Confirm("Mise en mouvement", question), Delay);
            if (jog is null)
            {
                Say(refusal ?? "Essai refusé.");
                return;
            }

            _jog = jog;
            jog.ReadingAvailable += (_, reading) => OnUi(() => Show(reading));
            jog.Ended += (_, reason) => OnUi(() => OnEnded(jog, reason));
            Command = 0;
            IsRunning = jog.IsRunning;
            if (IsRunning)
            {
                Say(IsTorqueMode
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"Axe {Axis} armé en couple, consigne nulle. Limites de l'essai : {MaxTorque:G3} Nm, {MaxVelocity:G3} tours/s, {MaxCurrent:G3} A.")
                    : string.Create(CultureInfo.InvariantCulture,
                        $"Axe {Axis} armé, consigne nulle. Limites de l'essai : {MaxVelocity:G3} tours/s, {MaxCurrent:G3} A."));
            }
        });
    }

    /// <summary>Termine l'essai : axe en IDLE, réglages restaurés.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private async Task StopAsync()
    {
        if (_jog is { } jog)
        {
            await jog.DisposeAsync();
        }
    }

    /// <summary>Consigne nulle, l'axe reste armé.</summary>
    [RelayCommand(CanExecute = nameof(CanStop))]
    private Task ZeroAsync() => SetCommandAsync(0);

    /// <summary>Bouton à maintenir enfoncé : +1 = sens positif, −1 = sens négatif.</summary>
    public Task PressAsync(int direction) => SetCommandAsync(Math.Sign(direction) * Math.Abs(IsTorqueMode ? JogTorque : JogSpeed));

    /// <summary>Relâchement d'un bouton à maintenir : arrêt.</summary>
    public Task ReleaseAsync() => SetCommandAsync(0);

    /// <summary>Applique une consigne, en tours/s ou en Nm selon le mode (bornée par la limite de l'essai).</summary>
    public async Task SetCommandAsync(float value)
    {
        if (_jog is not { IsRunning: true } jog)
        {
            return;
        }
        Command = Math.Clamp(value, -CommandLimit, CommandLimit);
        if (IsTorqueMode)
        {
            await _session.RunAsync("Consigne de couple", () => jog.SetTorqueAsync(Command));
        }
        else
        {
            await _session.RunAsync("Consigne de vitesse", () => jog.SetVelocityAsync(Command));
        }
    }

    /// <summary>Arrête l'essai si la vue disparaît (carte débranchée, fermeture).</summary>
    public void Dispose()
    {
        if (_jog is { } jog)
        {
            _ = jog.DisposeAsync().AsTask();
        }
    }

    private void Show(JogReading reading)
    {
        CultureInfo inv = CultureInfo.InvariantCulture;
        Velocity = reading.Velocity.ToString("F2", inv) + " tours/s";
        Current = reading.Current.ToString("F2", inv) + " A";
        BusVoltage = reading.BusVoltage.ToString("F1", inv) + " V";
        FetTemperature = reading.FetTemperature.ToString("F0", inv) + " °C";
    }

    private void OnEnded(JogSession jog, string reason)
    {
        if (ReferenceEquals(_jog, jog))
        {
            _jog = null;
        }
        IsRunning = false;
        Command = 0;
        Say(reason);
    }

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
}
