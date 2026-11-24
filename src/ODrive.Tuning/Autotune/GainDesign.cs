namespace ODrive.Tuning.Autotune;

/// <summary>Raideur visée, en fraction de la bande passante maximale admissible.</summary>
public enum Stiffness
{
    /// <summary>Un quart du maximum : réponse douce, très tolérante.</summary>
    Soft,

    /// <summary>La moitié du maximum.</summary>
    Normal,

    /// <summary>Le maximum admissible par l'encodeur et la boucle de courant.</summary>
    Stiff,
}

/// <summary>Gains des boucles de position et de vitesse, en unités firmware (controller.hpp:27-30).</summary>
/// <param name="PosGain">(tour/s)/tour.</param>
/// <param name="VelGain">Nm/(tour/s).</param>
/// <param name="VelIntegratorGain">Nm/((tour/s)·s).</param>
/// <param name="Bandwidth">Pulsation de coupure de la boucle de vitesse qui a servi au calcul, en rad/s.</param>
public sealed record LoopGains(double PosGain, double VelGain, double VelIntegratorGain, double Bandwidth);

/// <summary>Calcul des gains à partir de l'inertie identifiée (docs/AUTOTUNE.md, étape 2 ; fonction pure).</summary>
public static class GainDesign
{
    /// <summary>
    /// <c>vel_gain = J·ωc</c>, sans facteur 2π : le firmware calcule le couple à partir d'une erreur de vitesse
    /// en tours/s (controller.cpp:381), et J est en Nm/(tour/s²), donc la boucle fermée
    /// <c>J·dω/dt = vel_gain·(ω_ref − ω)</c> coupe à <c>vel_gain/J</c> rad/s. Intégrateur à ωc/4 (marge de phase
    /// d'environ 60°), position à ωc/4 pour rester bien en dessous de la boucle de vitesse.
    /// </summary>
    public static LoopGains Compute(double inertia, double bandwidth)
    {
        if (!(inertia > 0) || !(bandwidth > 0))
        {
            throw new ArgumentOutOfRangeException(nameof(inertia), "L'inertie et la bande passante doivent être positives.");
        }
        double velGain = inertia * bandwidth;
        return new LoopGains(bandwidth / 4, velGain, velGain * bandwidth / 4, bandwidth);
    }

    /// <summary>
    /// Bande passante maximale de la boucle de vitesse, en rad/s : le quart de la plus lente des deux boucles qui
    /// l'alimentent, l'estimateur de l'encodeur (<c>encoder.config.bandwidth</c>) et la boucle de courant
    /// (<c>motor.config.current_control_bandwidth</c>).
    /// </summary>
    public static double MaxBandwidth(double encoderBandwidth, double currentBandwidth) =>
        Math.Min(encoderBandwidth, currentBandwidth) / 4;

    public static double Bandwidth(Stiffness stiffness, double maxBandwidth) => stiffness switch
    {
        Stiffness.Soft => maxBandwidth / 4,
        Stiffness.Normal => maxBandwidth / 2,
        _ => maxBandwidth,
    };
}
