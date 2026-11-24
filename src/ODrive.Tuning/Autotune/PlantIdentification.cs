namespace ODrive.Tuning.Autotune;

/// <summary>Un relevé pendant l'identification : couple appliqué (Nm) et vitesse mesurée (tours/s) à un instant (s).</summary>
public sealed record MotionSample(double Time, double Torque, double Velocity);

/// <summary>
/// Modèle mécanique identifié, en unités firmware (docs/AUTOTUNE.md, étape 1) :
/// <c>couple = J·accélération + b·vitesse + τc·signe(vitesse)</c>.
/// </summary>
/// <param name="Inertia">J, en Nm/(tour/s²).</param>
/// <param name="Viscous">b, frottement visqueux, en Nm/(tour/s).</param>
/// <param name="Coulomb">τc, frottement sec, en Nm.</param>
/// <param name="RSquared">Qualité de l'ajustement (1 = parfait).</param>
/// <param name="SampleCount">Relevés retenus pour l'ajustement.</param>
public sealed record PlantEstimate(double Inertia, double Viscous, double Coulomb, double RSquared, int SampleCount);

/// <summary>Identification de l'inertie et des frottements à partir de paliers de couple (fonction pure).</summary>
public static class PlantIdentification
{
    /// <summary>Demi-largeur de la fenêtre de dérivation, en relevés.</summary>
    private const int HalfWindow = 10;

    /// <summary>Vitesse en dessous de laquelle un relevé est écarté (adhérence, bruit), en tours/s.</summary>
    private const double MinimumSpeed = 0.05;

    /// <summary>
    /// Ajuste J, b et τc par moindres carrés. L'accélération est la pente d'une droite ajustée sur une fenêtre
    /// glissante de relevés (Savitzky–Golay de degré 1, valable pour des instants irréguliers comme une lecture
    /// USB). Sont écartés les relevés presque à l'arrêt et ceux dont la fenêtre chevauche un changement de couple.
    /// </summary>
    /// <exception cref="InvalidOperationException">Le moteur a trop peu bougé pour identifier quoi que ce soit.</exception>
    public static PlantEstimate Fit(IReadOnlyList<MotionSample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        MotionSample[] s = [.. samples.OrderBy(sample => sample.Time)];
        double peak = s.Length == 0 ? 0 : s.Max(sample => Math.Abs(sample.Velocity));
        double threshold = Math.Max(MinimumSpeed, 0.05 * peak);

        // Équations normales de [a, ω, signe(ω)] · [J, b, τc] = τ.
        var ata = new double[3, 3];
        var atb = new double[3];
        var rows = new List<(double[] X, double Y)>();
        for (int i = HalfWindow; i < s.Length - HalfWindow; i++)
        {
            if (Math.Abs(s[i].Velocity) < threshold || !ConstantTorque(s, i))
            {
                continue;
            }
            double[] x = [Slope(s, i), s[i].Velocity, Math.Sign(s[i].Velocity)];
            rows.Add((x, s[i].Torque));
            for (int r = 0; r < 3; r++)
            {
                atb[r] += x[r] * s[i].Torque;
                for (int c = 0; c < 3; c++)
                {
                    ata[r, c] += x[r] * x[c];
                }
            }
        }

        if (rows.Count < 50)
        {
            throw new InvalidOperationException(
                $"Identification impossible : pas assez de mouvement ({rows.Count} relevés utiles au-dessus de {threshold:0.###} tour/s). "
                + "Augmenter le couple d'essai.");
        }

        double[] theta = Solve(ata, atb);
        double mean = rows.Average(row => row.Y);
        double total = rows.Sum(row => (row.Y - mean) * (row.Y - mean));
        double residual = rows.Sum(row =>
        {
            double predicted = theta[0] * row.X[0] + theta[1] * row.X[1] + theta[2] * row.X[2];
            return (row.Y - predicted) * (row.Y - predicted);
        });
        double rSquared = total > 0 ? 1 - residual / total : 0;
        return new PlantEstimate(theta[0], theta[1], theta[2], rSquared, rows.Count);
    }

    private static bool ConstantTorque(MotionSample[] s, int i)
    {
        for (int k = i - HalfWindow; k <= i + HalfWindow; k++)
        {
            if (s[k].Torque != s[i].Torque)
            {
                return false;
            }
        }
        return true;
    }

    // Pente de la droite des moindres carrés vitesse(temps) sur la fenêtre centrée en i.
    private static double Slope(MotionSample[] s, int i)
    {
        double tMean = 0, vMean = 0;
        int n = 2 * HalfWindow + 1;
        for (int k = i - HalfWindow; k <= i + HalfWindow; k++)
        {
            tMean += s[k].Time;
            vMean += s[k].Velocity;
        }
        tMean /= n;
        vMean /= n;
        double num = 0, den = 0;
        for (int k = i - HalfWindow; k <= i + HalfWindow; k++)
        {
            double dt = s[k].Time - tMean;
            num += dt * (s[k].Velocity - vMean);
            den += dt * dt;
        }
        return den > 0 ? num / den : 0;
    }

    // Élimination de Gauss avec pivot partiel, système 3 × 3.
    private static double[] Solve(double[,] a, double[] b)
    {
        int n = b.Length;
        var m = new double[n, n + 1];
        for (int r = 0; r < n; r++)
        {
            for (int c = 0; c < n; c++)
            {
                m[r, c] = a[r, c];
            }
            m[r, n] = b[r];
        }
        for (int col = 0; col < n; col++)
        {
            int pivot = col;
            for (int r = col + 1; r < n; r++)
            {
                if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col]))
                {
                    pivot = r;
                }
            }
            if (Math.Abs(m[pivot, col]) < 1e-12)
            {
                throw new InvalidOperationException(
                    "Identification impossible : les essais ne distinguent pas l'inertie des frottements (pas assez de paliers différents).");
            }
            for (int c = 0; c <= n; c++)
            {
                (m[col, c], m[pivot, c]) = (m[pivot, c], m[col, c]);
            }
            for (int r = 0; r < n; r++)
            {
                if (r == col)
                {
                    continue;
                }
                double factor = m[r, col] / m[col, col];
                for (int c = col; c <= n; c++)
                {
                    m[r, c] -= factor * m[col, c];
                }
            }
        }
        var x = new double[n];
        for (int r = 0; r < n; r++)
        {
            x[r] = m[r, n] / m[r, r];
        }
        return x;
    }
}
