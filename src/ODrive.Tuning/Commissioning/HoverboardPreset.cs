using System.Globalization;
using ODrive.Model;
using ODrive.Protocol;

namespace ODrive.Tuning.Commissioning;

/// <summary>Étapes de l'assistant de mise en service (SPEC §4).</summary>
public enum CommissioningStep
{
    Power,
    Motor,
    Encoder,
    Controller,
    Limits,
    Communication,
}

/// <summary>Une valeur proposée par l'assistant, modifiable avant écriture.</summary>
/// <param name="Path">Chemin complet de la propriété sur la carte.</param>
/// <param name="Value">Valeur typée (enum généré, bool, nombre).</param>
/// <param name="Unit">Unité affichée, vide s'il n'y en a pas.</param>
/// <param name="NeedsReboot">Vrai si la valeur ne prend effet qu'après sauvegarde et redémarrage.</param>
public sealed record CommissioningSetting(CommissioningStep Step, string Path, object Value, string Label,
    string Unit = "", string Note = "", bool NeedsReboot = false);

/// <summary>Écriture d'une valeur de l'assistant en échec.</summary>
public sealed record CommissioningFailure(string Path, string Message);

/// <summary>Choix de l'utilisateur pour un moteur roue d'hoverboard.</summary>
public sealed record HoverboardOptions
{
    /// <summary>Axe à configurer : 0 ou 1.</summary>
    public int Axis { get; init; }

    /// <summary>Constante de vitesse du moteur, en tr/min par volt ; typiquement 16 (docs/HOVERBOARD.md).</summary>
    public float Kv { get; init; } = 16f;

    /// <summary>Paires de pôles : nombre d'aimants / 2.</summary>
    public int PolePairs { get; init; } = 15;

    public bool BrakeResistorConnected { get; init; }

    /// <summary>Résistance de freinage, en ohms.</summary>
    public float BrakeResistance { get; init; } = 2f;

    /// <summary>Courant de recharge accepté par l'alimentation, en ampères (négatif).</summary>
    public float DcMaxNegativeCurrent { get; init; } = -0.01f;

    /// <summary>Limite de courant moteur, en ampères.</summary>
    public float CurrentLimit { get; init; } = 10f;

    /// <summary>Courant de calibration, en ampères.</summary>
    public float CalibrationCurrent { get; init; } = 10f;

    /// <summary>Limite de vitesse, en tours/s.</summary>
    public float VelocityLimit { get; init; } = 10f;

    /// <summary>Rampe de vitesse, en tours/s².</summary>
    public float VelocityRampRate { get; init; } = 5f;

    /// <summary>Watchdog de l'axe : à n'activer que si un contrôleur envoie des trames CAN en continu.</summary>
    public bool EnableWatchdog { get; init; }

    /// <summary>Délai du watchdog, en secondes.</summary>
    public float WatchdogTimeout { get; init; } = 0.2f;

    /// <summary>Identifiant CAN de l'axe ; par défaut le numéro de l'axe.</summary>
    public uint? CanNodeId { get; init; }
}

/// <summary>
/// Préréglage « moteur roue d'hoverboard » : valeurs et formules de <c>docs/HOVERBOARD.md</c>, lui-même tiré
/// de <c>ref/ODrive/docs/hoverboard.rst</c>.
/// </summary>
public static class HoverboardPreset
{
    /// <summary>Constante de couple, en Nm/A : 8,27 / Kv.</summary>
    public static float TorqueConstant(float kv) => 8.27f / kv;

    /// <summary>Points par tour vus par la carte avec des capteurs Hall : 6 par paire de pôles.</summary>
    public static int HallCpr(int polePairs) => 6 * polePairs;

    /// <summary>Gain de vitesse de départ du guide : 0,02 × constante de couple × cpr, en Nm/(tour/s).</summary>
    public static float VelGain(float kv, int polePairs) => 0.02f * TorqueConstant(kv) * HallCpr(polePairs);

    /// <summary>Gain intégral de départ du guide : 0,1 × constante de couple × cpr.</summary>
    public static float VelIntegratorGain(float kv, int polePairs) => 0.1f * TorqueConstant(kv) * HallCpr(polePairs);

    public static IReadOnlyList<CommissioningSetting> Build(HoverboardOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Axis is not (0 or 1))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "L'axe doit être 0 ou 1.");
        }
        if (options.Kv <= 0 || options.PolePairs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "Kv et le nombre de paires de pôles doivent être positifs.");
        }

        string axis = string.Create(CultureInfo.InvariantCulture, $"axis{options.Axis}.");
        // Board/v3/board.cpp:204-224 : GPIO9-11 = Hall de l'axe 0, GPIO12-14 = Hall de l'axe 1.
        int firstHallGpio = options.Axis == 0 ? 9 : 12;
        int cpr = HallCpr(options.PolePairs);
        var settings = new List<CommissioningSetting>
        {
            new(CommissioningStep.Power, "config.enable_brake_resistor", options.BrakeResistorConnected,
                "Résistance de freinage branchée", Note: "sauvegarde + redémarrage nécessaires", NeedsReboot: true),
            new(CommissioningStep.Power, "config.brake_resistance", options.BrakeResistance,
                "Valeur de la résistance de freinage", "Ω", "ajouter ~0,05 Ω en cas d'erreur au freinage"),
            new(CommissioningStep.Power, "config.dc_max_negative_current", options.DcMaxNegativeCurrent,
                "Courant de recharge accepté par l'alimentation", "A",
                "négatif ; sans résistance de freinage, c'est ce que la batterie accepte"),
        };
        for (int i = 0; i < 3; i++)
        {
            settings.Add(new CommissioningSetting(CommissioningStep.Encoder,
                string.Create(CultureInfo.InvariantCulture, $"config.gpio{firstHallGpio + i}_mode"), GpioMode.Digital,
                $"Entrée Hall {"ABC"[i]} (GPIO{firstHallGpio + i})", Note: "redémarrage nécessaire", NeedsReboot: true));
        }

        settings.AddRange(
        [
            new(CommissioningStep.Motor, axis + "motor.config.pole_pairs", options.PolePairs,
                "Paires de pôles", Note: "nombre d'aimants / 2 ; 15 pour une roue d'hoverboard"),
            new(CommissioningStep.Motor, axis + "motor.config.motor_type", MotorType.HighCurrent, "Type de moteur"),
            new(CommissioningStep.Motor, axis + "motor.config.resistance_calib_max_voltage", 4f,
                "Tension maximale de calibration", "V", "résistance de phase élevée"),
            new(CommissioningStep.Motor, axis + "motor.config.requested_current_range", 25f,
                "Plage de mesure de courant", "A", "sauvegarde + redémarrage nécessaires", NeedsReboot: true),
            new(CommissioningStep.Motor, axis + "motor.config.current_control_bandwidth", 100f,
                "Bande passante de la boucle de courant", "rad/s", "inductance élevée : le défaut de 1000 est trop haut"),
            new(CommissioningStep.Motor, axis + "motor.config.torque_constant", TorqueConstant(options.Kv),
                "Constante de couple", "Nm/A", "8,27 / Kv"),
            new(CommissioningStep.Motor, axis + "motor.config.calibration_current", options.CalibrationCurrent,
                "Courant de calibration", "A"),

            new(CommissioningStep.Encoder, axis + "encoder.config.mode", EncoderMode.Hall, "Type de capteur"),
            new(CommissioningStep.Encoder, axis + "encoder.config.cpr", cpr,
                "Points par tour", Note: "6 × paires de pôles"),
            new(CommissioningStep.Encoder, axis + "encoder.config.calib_scan_distance", 150f,
                "Distance de balayage de calibration", "rad électriques"),
            new(CommissioningStep.Encoder, axis + "encoder.config.bandwidth", 100f,
                "Bande passante de l'estimateur", "rad/s"),

            new(CommissioningStep.Controller, axis + "controller.config.control_mode", ControlMode.VelocityControl,
                "Mode de contrôle"),
            new(CommissioningStep.Controller, axis + "controller.config.input_mode", InputMode.VelRamp,
                "Mode d'entrée", Note: "rampe de vitesse ; le guide laisse PASSTHROUGH"),
            new(CommissioningStep.Controller, axis + "controller.config.pos_gain", 1f, "Gain de position", "(tour/s)/tour"),
            new(CommissioningStep.Controller, axis + "controller.config.vel_gain", VelGain(options.Kv, options.PolePairs),
                "Gain de vitesse", "Nm/(tour/s)", "0,02 × constante de couple × cpr"),
            new(CommissioningStep.Controller, axis + "controller.config.vel_integrator_gain",
                VelIntegratorGain(options.Kv, options.PolePairs),
                "Gain intégral de vitesse", "Nm/tour", "0,1 × constante de couple × cpr"),

            new(CommissioningStep.Limits, axis + "motor.config.current_lim", options.CurrentLimit,
                "Limite de courant moteur", "A", "10 à 20 A selon l'échauffement"),
            new(CommissioningStep.Limits, axis + "controller.config.vel_limit", options.VelocityLimit,
                "Limite de vitesse", "tours/s"),
            new(CommissioningStep.Limits, axis + "controller.config.vel_ramp_rate", options.VelocityRampRate,
                "Rampe de vitesse", "tours/s²"),

            new(CommissioningStep.Communication, axis + "config.can.node_id", options.CanNodeId ?? (uint)options.Axis,
                "Identifiant CAN de l'axe", Note: "0 = gauche, 1 = droite sur la mini-voiture"),
            new(CommissioningStep.Communication, axis + "config.watchdog_timeout", options.WatchdogTimeout,
                "Délai du watchdog", "s"),
            new(CommissioningStep.Communication, axis + "config.enable_watchdog", options.EnableWatchdog,
                "Watchdog de l'axe",
                Note: "l'axe s'arrête s'il ne reçoit plus de trame CAN ; l'USB seul ne le nourrit pas"),
        ]);
        return settings;
    }

    /// <summary>
    /// Écrit les valeurs dans la carte. Une propriété absente ou une valeur refusée n'arrête pas les autres.
    /// Rien n'est sauvegardé en flash.
    /// </summary>
    public static async Task<IReadOnlyList<CommissioningFailure>> ApplyAsync(ODriveDevice device,
        IEnumerable<CommissioningSetting> settings, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(settings);
        var failures = new List<CommissioningFailure>();
        foreach (CommissioningSetting setting in settings)
        {
            if (device.Schema.FindProperty(setting.Path) is not { } property)
            {
                failures.Add(new CommissioningFailure(setting.Path, "propriété absente de cette carte"));
                continue;
            }

            try
            {
                await device.WriteAsync(property, ToEndpointValue(property.Type, setting.Value), ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is FormatException or OverflowException or InvalidCastException)
            {
                failures.Add(new CommissioningFailure(setting.Path, "valeur incompatible avec le type de la propriété"));
            }
        }
        return failures;
    }

    /// <summary>Convertit une valeur de préréglage (enum généré compris) vers le type .NET de l'endpoint.</summary>
    public static object ToEndpointValue(EndpointValueType type, object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        object raw = value is Enum
            ? Convert.ChangeType(value, Enum.GetUnderlyingType(value.GetType()), CultureInfo.InvariantCulture)
            : value;
        return Convert.ChangeType(raw, EndpointValue.ClrType(type), CultureInfo.InvariantCulture);
    }
}
