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
    /// <summary>
    /// Demi-largeur de la fenêtre de dérivation, en secondes. Une durée et non un nombre de relevés : à
    /// 100 000 lectures/s (simulateur), 21 relevés ne couvrent que 0,2 ms et le bruit de la pente écrase l'inertie.
    /// </summary>
    private const double HalfWindow = 0.005;

    /// <summary>Relevés minimum dans une fenêtre de dérivation.</summary>
    private const int MinimumWindowSamples = 9;

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

        double[] slopes = Slopes(s, out bool[] valid);

        // Équations normales de [a, ω, signe(ω)] · [J, b, τc] = τ.
        var ata = new double[3, 3];
        var atb = new double[3];
        var rows = new List<(double[] X, double Y)>();
        for (int i = 0; i < s.Length; i++)
        {
            if (!valid[i] || Math.Abs(s[i].Velocity) < threshold)
            {
                continue;
            }
            double[] x = [slopes[i], s[i].Velocity, Math.Sign(s[i].Velocity)];
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

    // Pente de la droite des moindres carrés vitesse(temps) sur la fenêtre [t − W, t + W] de chaque relevé,
    // par sommes cumulées (temps linéaire). Fenêtre valable si elle tient dans l'enregistrement, contient assez
    // de relevés et ne chevauche pas de changement de couple.
    private static double[] Slopes(MotionSample[] s, out bool[] valid)
    {
        int n = s.Length;
        var slopes = new double[n];
        valid = new bool[n];
        if (n == 0)
        {
            return slopes;
        }

        double origin = s[0].Time;
        var segment = new int[n];
        var st = new double[n + 1];
        var sv = new double[n + 1];
        var stt = new double[n + 1];
        var stv = new double[n + 1];
        for (int i = 0; i < n; i++)
        {
            segment[i] = i == 0 ? 0 : segment[i - 1] + (s[i].Torque != s[i - 1].Torque ? 1 : 0);
            double t = s[i].Time - origin;
            st[i + 1] = st[i] + t;
            sv[i + 1] = sv[i] + s[i].Velocity;
            stt[i + 1] = stt[i] + t * t;
            stv[i + 1] = stv[i] + t * s[i].Velocity;
        }

        int lo = 0, hi = 0;
        double first = s[0].Time, last = s[n - 1].Time;
        for (int i = 0; i < n; i++)
        {
            double t = s[i].Time;
            while (s[lo].Time < t - HalfWindow)
            {
                lo++;
            }
            while (hi + 1 < n && s[hi + 1].Time <= t + HalfWindow)
            {
                hi++;
            }
            int count = hi - lo + 1;
            if (t - HalfWindow < first || t + HalfWindow > last || count < MinimumWindowSamples
                || segment[lo] != segment[i] || segment[hi] != segment[i])
            {
                continue;
            }
            double sumT = st[hi + 1] - st[lo], sumV = sv[hi + 1] - sv[lo];
            double sumTT = stt[hi + 1] - stt[lo], sumTV = stv[hi + 1] - stv[lo];
            double den = count * sumTT - sumT * sumT;
            if (den > 0)
            {
                slopes[i] = (count * sumTV - sumT * sumV) / den;
                valid[i] = true;
            }
        }
        return slopes;
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
