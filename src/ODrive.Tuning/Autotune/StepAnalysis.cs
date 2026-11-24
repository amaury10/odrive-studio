namespace ODrive.Tuning.Autotune;

/// <summary>Mesures d'une réponse indicielle, rapportées à l'amplitude de l'échelon.</summary>
/// <param name="RiseTime">Temps de montée de 10 % à 90 %, en s ; <c>null</c> si 90 % n'est jamais atteint.</param>
/// <param name="Overshoot">Dépassement au-delà de la consigne, en fraction de l'échelon (0,2 = 20 %).</param>
/// <param name="SettlingTime">Temps d'établissement à ±5 %, en s ; <c>null</c> si la réponse n'est pas établie à la fin.</param>
/// <param name="SteadyStateError">Écart moyen sur le dernier cinquième de l'enregistrement, en fraction de l'échelon.</param>
/// <param name="Oscillating">Oscillation encore présente en fin d'enregistrement.</param>
public sealed record StepMetrics(double? RiseTime, double Overshoot, double? SettlingTime, double SteadyStateError, bool Oscillating);

/// <summary>Analyse d'une réponse à un échelon (SPEC §6 ; fonction pure).</summary>
public static class StepAnalysis
{
    private const double SettlingBand = 0.05;

    /// <param name="samples">Instants (s) et valeurs mesurées, l'échelon étant appliqué au premier instant.</param>
    /// <param name="from">Valeur de départ.</param>
    /// <param name="to">Consigne après l'échelon.</param>
    public static StepMetrics Analyze(IReadOnlyList<(double Time, double Value)> samples, double from, double to)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count < 10 || from == to)
        {
            throw new ArgumentException("Il faut au moins 10 relevés et un échelon d'amplitude non nulle.", nameof(samples));
        }

        double t0 = samples[0].Time;
        double span = to - from;
        double[] t = [.. samples.Select(sample => sample.Time - t0)];
        double[] y = [.. samples.Select(sample => (sample.Value - from) / span)];

        double? t10 = FirstAtOrAbove(t, y, 0.1);
        double? t90 = FirstAtOrAbove(t, y, 0.9);
        double? rise = t10 is { } a && t90 is { } b ? b - a : null;
        double overshoot = Math.Max(0, y.Max() - 1);

        int lastOutside = -1;
        for (int i = 0; i < y.Length; i++)
        {
            if (Math.Abs(y[i] - 1) > SettlingBand)
            {
                lastOutside = i;
            }
        }
        double? settling = lastOutside == y.Length - 1 ? null : t[lastOutside + 1];

        int tailStart = (int)(y.Length * 0.8);
        double tailMean = y.Skip(tailStart).Average();
        double steadyStateError = Math.Abs(tailMean - 1);

        // Oscillation entretenue : sur les 40 % finaux, une amplitude encore notable qui traverse la consigne.
        double[] last = [.. y.Skip((int)(y.Length * 0.6))];
        int crossings = 0;
        for (int i = 1; i < last.Length; i++)
        {
            if (Math.Sign(last[i] - tailMean) != Math.Sign(last[i - 1] - tailMean))
            {
                crossings++;
            }
        }
        bool oscillating = last.Max() - last.Min() > 0.1 && crossings >= 2;

        return new StepMetrics(rise, overshoot, settling, steadyStateError, oscillating);
    }

    private static double? FirstAtOrAbove(double[] t, double[] y, double level)
    {
        for (int i = 0; i < y.Length; i++)
        {
            if (y[i] >= level)
            {
                return t[i];
            }
        }
        return null;
    }
}
