using System.Globalization;

namespace ODrive.Protocol.Dfu;

/// <summary>
/// Accès USB au bootloader ROM du STM32 (DFU avec extensions ST « DfuSe », note d'application ST AN3156) :
/// transferts de contrôle de classe sur l'interface DFU. Les appels sont bloquants.
/// </summary>
public interface IDfuTransport : IDisposable
{
    /// <summary>Numéro de série USB du bootloader (identifiant du microcontrôleur).</summary>
    string SerialNumber { get; }

    /// <summary>
    /// Nom de chaque réglage alternatif de l'interface DFU, dans l'ordre. Chacun décrit une région mémoire,
    /// par exemple <c>@Internal Flash  /0x08000000/04*016Kg,01*064Kg,07*128Kg</c>.
    /// </summary>
    IReadOnlyList<string> AlternateNames { get; }

    void SetAlternate(int alternate);

    /// <summary>Requête de classe vers la carte (bmRequestType 0x21).</summary>
    /// <exception cref="IOException">La carte a refusé la requête (STALL) ou a disparu.</exception>
    void ControlOut(byte request, ushort value, ReadOnlySpan<byte> data);

    /// <summary>Requête de classe depuis la carte (bmRequestType 0xA1).</summary>
    /// <returns>Nombre d'octets reçus.</returns>
    /// <exception cref="IOException">La carte a refusé la requête (STALL) ou a disparu.</exception>
    int ControlIn(byte request, ushort value, Span<byte> buffer);
}

/// <summary>Erreur signalée par le bootloader, ou réponse inattendue.</summary>
public class DfuException : IOException
{
    public DfuException()
    {
    }

    public DfuException(string message)
        : base(message)
    {
    }

    public DfuException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Requêtes, états et codes d'état DFU 1.1 utilisés par le bootloader ST.</summary>
public static class DfuConstants
{
    public const byte RequestDnload = 1;
    public const byte RequestUpload = 2;
    public const byte RequestGetStatus = 3;
    public const byte RequestClrStatus = 4;
    public const byte RequestGetState = 5;
    public const byte RequestAbort = 6;

    public const byte StateIdle = 2;
    public const byte StateDnloadSync = 3;
    public const byte StateDnBusy = 4;
    public const byte StateDnloadIdle = 5;
    public const byte StateManifestSync = 6;
    public const byte StateManifest = 7;
    public const byte StateUploadIdle = 9;
    public const byte StateError = 10;

    public const byte StatusOk = 0x00;
    public const byte StatusErrTarget = 0x01;
    public const byte StatusErrWrite = 0x03;
    public const byte StatusErrErase = 0x04;
    public const byte StatusErrAddress = 0x08;
    public const byte StatusErrVendor = 0x0B; // flash protégée en lecture (RDP)
    public const byte StatusErrStalledPacket = 0x0F;

    /// <summary>Commandes DfuSe portées par un DNLOAD de bloc 0 (AN3156).</summary>
    public const byte CommandSetAddress = 0x21;
    public const byte CommandErase = 0x41;
    public const byte CommandReadUnprotect = 0x92;

    /// <summary>Début de la flash interne et de l'application (vecteurs d'interruption).</summary>
    public const uint FlashBase = 0x0800_0000;

    /// <summary>Taille de transfert maximale du bootloader (<c>tools/odrive/dfuse/DfuDevice.py:21</c>).</summary>
    public const int MaxTransferSize = 2048;
}

/// <summary>Un secteur d'une région mémoire du microcontrôleur.</summary>
/// <param name="Region">Nom de la région (« Internal Flash », « OTP Memory »…).</param>
/// <param name="Alternate">Réglage alternatif USB qui donne accès à la région.</param>
/// <param name="Mode">Lettre de droits ST : <c>a</c> à <c>g</c>, bits lecture (1), effacement (2), écriture (4).</param>
public sealed record DfuSector(string Region, int Alternate, uint Address, int Length, char Mode)
{
    public const string InternalFlash = "Internal Flash";
    public const string OtpMemory = "OTP Memory";

    private int Rights => Mode is >= 'a' and <= 'g' ? Mode - 'a' + 1 : 0;

    public bool IsReadable => (Rights & 1) != 0;
    public bool IsErasable => (Rights & 2) != 0;
    public bool IsWritable => (Rights & 4) != 0;
}

/// <summary>Avancement d'une opération DFU.</summary>
/// <param name="Stage">Étape en français (« Lecture », « Effacement », « Écriture », « Vérification »).</param>
public sealed record DfuProgress(string Stage, int Done, int Total);

/// <summary>Décodage des descripteurs de régions mémoire du bootloader ST.</summary>
public static class DfuMemoryLayout
{
    /// <summary>
    /// Liste les secteurs décrits par les noms des réglages alternatifs
    /// (format repris de <c>ref/ODrive/tools/odrive/dfuse/DfuDevice.py:143-178</c>).
    /// </summary>
    public static IReadOnlyList<DfuSector> Parse(IReadOnlyList<string> alternateNames)
    {
        ArgumentNullException.ThrowIfNull(alternateNames);
        var sectors = new List<DfuSector>();
        for (int alternate = 0; alternate < alternateNames.Count; alternate++)
        {
            string[] parts = alternateNames[alternate].Split('/');
            if (parts.Length != 3 || !TryParseAddress(parts[1], out uint address))
            {
                continue;
            }

            string region = parts[0].Trim().TrimStart('@').Trim();
            foreach (string group in parts[2].Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                // « 04*016Kg » : 4 secteurs de 16 Kio, droits g. « 01*512 e » : un secteur de 512 octets.
                int star = group.IndexOf('*', StringComparison.Ordinal);
                if (star <= 0 || group.Length < star + 4
                    || !int.TryParse(group.AsSpan(0, star), NumberStyles.None, CultureInfo.InvariantCulture, out int count)
                    || !int.TryParse(group.AsSpan(star + 1, group.Length - star - 3), NumberStyles.None,
                        CultureInfo.InvariantCulture, out int size))
                {
                    continue;
                }

                int multiplier = char.ToUpperInvariant(group[^2]) switch
                {
                    'K' => 1024,
                    'M' => 1024 * 1024,
                    _ => 1,
                };
                for (int i = 0; i < count; i++)
                {
                    sectors.Add(new DfuSector(region, alternate, address, size * multiplier, group[^1]));
                    address += (uint)(size * multiplier);
                }
            }
        }
        return sectors;
    }

    private static bool TryParseAddress(string text, out uint address)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            text = text[2..];
        }
        return uint.TryParse(text, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out address);
    }
}
