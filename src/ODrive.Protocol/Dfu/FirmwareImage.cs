using System.Buffers.Binary;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ODrive.Protocol.Dfu;

/// <summary>Bloc contigu d'une image mémoire.</summary>
#pragma warning disable CA1819 // tableau brut : l'image est un tampon binaire, pas une collection
public sealed record FirmwareSegment(uint Address, byte[] Data)
#pragma warning restore CA1819
{
    public uint End => Address + (uint)Data.Length;
}

/// <summary>
/// Image à écrire dans la flash : firmware au format ELF (seul format publié par ODrive Robotics pour la
/// version 0.5.6) ou Intel HEX (versions plus anciennes), ou copie binaire brute de la flash (sauvegarde
/// faite par <see cref="FirmwareUpdater.ReadFlash"/>).
/// </summary>
public sealed partial class FirmwareImage
{
    private const byte Erased = 0xFF;

    private FirmwareImage(IReadOnlyList<FirmwareSegment> segments) => Segments = segments;

    /// <summary>Blocs de l'image, par adresse croissante, sans recouvrement.</summary>
    public IReadOnlyList<FirmwareSegment> Segments { get; }

    /// <summary>Charge un fichier <c>.elf</c>, <c>.hex</c> (Intel HEX) ou <c>.bin</c> (flash brute depuis 0x08000000).</summary>
    public static FirmwareImage Load(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        switch (Path.GetExtension(path).ToUpperInvariant())
        {
            case ".HEX":
                using (var reader = new StreamReader(path))
                {
                    return FromIntelHex(reader);
                }
            case ".ELF":
                return FromElf(File.ReadAllBytes(path));
            case ".BIN":
                return FromBinary(File.ReadAllBytes(path));
            default:
                throw new InvalidDataException(
                    "Format non pris en charge : choisir un fichier .elf ou .hex (firmware) ou .bin (sauvegarde).");
        }
    }

    /// <summary>Image binaire brute, placée au début de la flash.</summary>
    public static FirmwareImage FromBinary(byte[] data, uint address = DfuConstants.FlashBase)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new FirmwareImage([new FirmwareSegment(address, data)]);
    }

    /// <summary>
    /// Lit un exécutable ELF 32 bits ARM : chaque segment <c>PT_LOAD</c> qui a un contenu dans le fichier est
    /// placé à son adresse physique (<c>p_paddr</c>), celle où il est stocké en flash. C'est ce que fait
    /// <c>objcopy -O ihex</c>, utilisé par le Makefile du firmware pour produire les .hex.
    /// </summary>
    /// <exception cref="InvalidDataException">Le fichier n'est pas un ELF 32 bits ARM, ou il est tronqué.</exception>
    public static FirmwareImage FromElf(ReadOnlySpan<byte> file)
    {
        const int headerSize = 52, entrySize = 32;
        const uint programLoad = 1;
        const ushort machineArm = 40;

        if (file.Length < headerSize || file[0] != 0x7F || file[1] != 'E' || file[2] != 'L' || file[3] != 'F')
        {
            throw new InvalidDataException("Ce fichier n'est pas un exécutable ELF.");
        }
        if (file[4] != 1 || file[5] != 1)
        {
            throw new InvalidDataException("ELF non pris en charge : il faut un ELF 32 bits petit-boutiste (ARM Cortex-M).");
        }
        if (BinaryPrimitives.ReadUInt16LittleEndian(file[18..]) != machineArm)
        {
            throw new InvalidDataException("Cet ELF n'est pas destiné à un processeur ARM.");
        }

        uint tableOffset = BinaryPrimitives.ReadUInt32LittleEndian(file[28..]);
        int size = BinaryPrimitives.ReadUInt16LittleEndian(file[42..]);
        int count = BinaryPrimitives.ReadUInt16LittleEndian(file[44..]);
        if (size < entrySize || tableOffset + (long)size * count > file.Length)
        {
            throw new InvalidDataException("ELF tronqué : table des segments incomplète.");
        }

        var chunks = new List<FirmwareSegment>();
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> entry = file.Slice((int)tableOffset + i * size, entrySize);
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(entry);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(entry[4..]);
            uint physicalAddress = BinaryPrimitives.ReadUInt32LittleEndian(entry[12..]);
            uint fileSize = BinaryPrimitives.ReadUInt32LittleEndian(entry[16..]);
            if (type != programLoad || fileSize == 0)
            {
                continue;
            }
            if (offset + (long)fileSize > file.Length)
            {
                throw new InvalidDataException($"ELF tronqué : le segment {i} dépasse la fin du fichier.");
            }
            chunks.Add(new FirmwareSegment(physicalAddress, file.Slice((int)offset, (int)fileSize).ToArray()));
        }
        return new FirmwareImage(Merge(chunks));
    }

    /// <summary>Lit un fichier Intel HEX (enregistrements 00, 01, 02 et 04 ; 03 et 05 ignorés).</summary>
    /// <exception cref="InvalidDataException">Fichier mal formé ; le message donne la ligne.</exception>
    public static FirmwareImage FromIntelHex(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var chunks = new List<FirmwareSegment>();
        uint baseAddress = 0;
        bool ended = false;
        int lineNumber = 0;

        while (!ended && reader.ReadLine() is { } line)
        {
            lineNumber++;
            line = line.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            byte[] record = ParseRecord(line, lineNumber);
            int count = record[0];
            uint offset = (uint)((record[1] << 8) | record[2]);
            ReadOnlySpan<byte> data = record.AsSpan(4, count);
            switch (record[3])
            {
                case 0x00:
                    chunks.Add(new FirmwareSegment(baseAddress + offset, data.ToArray()));
                    break;
                case 0x01:
                    ended = true;
                    break;
                case 0x02 when count == 2:
                    baseAddress = (uint)BinaryPrimitives.ReadUInt16BigEndian(data) << 4;
                    break;
                case 0x04 when count == 2:
                    baseAddress = (uint)BinaryPrimitives.ReadUInt16BigEndian(data) << 16;
                    break;
                case 0x03 or 0x05:
                    break; // adresse de démarrage : sans objet pour un Cortex-M
                default:
                    throw new InvalidDataException($"Intel HEX : enregistrement inconnu ou mal formé, ligne {lineNumber}.");
            }
        }

        if (!ended)
        {
            throw new InvalidDataException("Intel HEX : fichier tronqué (pas d'enregistrement de fin).");
        }
        return new FirmwareImage(Merge(chunks));
    }

    /// <summary>Contenu de la plage demandée ; ce que l'image ne définit pas vaut 0xFF (flash effacée).</summary>
    public byte[] Read(uint address, int length)
    {
        byte[] result = new byte[length];
        Array.Fill(result, Erased);
        long end = (long)address + length;
        foreach (FirmwareSegment segment in Segments)
        {
            long from = Math.Max(address, segment.Address);
            long to = Math.Min(end, segment.End);
            if (from < to)
            {
                segment.Data.AsSpan((int)(from - segment.Address), (int)(to - from))
                    .CopyTo(result.AsSpan((int)(from - address)));
            }
        }
        return result;
    }

    /// <summary>Vrai si l'image définit au moins un octet de la plage.</summary>
    public bool Overlaps(uint address, int length) =>
        Segments.Any(s => s.Address < (long)address + length && s.End > address);

    /// <summary>
    /// Contrôle de vraisemblance avant d'effacer une carte : la table de vecteurs Cortex-M au début de la
    /// flash doit donner une pile en RAM et un point d'entrée Thumb dans la flash.
    /// </summary>
    public bool LooksLikeFirmware(out string? reason)
    {
        if (!Overlaps(DfuConstants.FlashBase, 8))
        {
            reason = "l'image ne contient rien au début de la flash (0x08000000).";
            return false;
        }

        byte[] vectors = Read(DfuConstants.FlashBase, 8);
        uint stackPointer = BinaryPrimitives.ReadUInt32LittleEndian(vectors);
        uint resetVector = BinaryPrimitives.ReadUInt32LittleEndian(vectors.AsSpan(4));

        // STM32F405 : RAM 0x20000000-0x2001FFFF, CCM 0x10000000-0x1000FFFF (la pile peut pointer juste après).
        bool stackInRam = stackPointer is (> 0x2000_0000 and <= 0x2002_0000) or (> 0x1000_0000 and <= 0x1001_0000);
        if (!stackInRam)
        {
            reason = $"pointeur de pile initial invraisemblable (0x{stackPointer:X8}).";
            return false;
        }
        if ((resetVector & 1) == 0 || resetVector < DfuConstants.FlashBase || resetVector >= DfuConstants.FlashBase + 0x10_0000)
        {
            reason = $"vecteur de reset invraisemblable (0x{resetVector:X8}).";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Version matérielle visée par un fichier officiel, d'après son nom (<c>ODriveFirmware_v3.6-56V.hex</c>),
    /// comme <c>ref/ODrive/tools/odrive/dfu.py:150-154</c>. <c>null</c> si le nom ne le dit pas.
    /// </summary>
    public static (int Major, int Minor, int Variant)? HardwareVersionFromFileName(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        Match match = HardwareVersionRegex().Match(Path.GetFileName(path));
        return match.Success
            ? (int.Parse(match.Groups[1].ValueSpan, CultureInfo.InvariantCulture),
               int.Parse(match.Groups[2].ValueSpan, CultureInfo.InvariantCulture),
               int.Parse(match.Groups[3].ValueSpan, CultureInfo.InvariantCulture))
            : null;
    }

    [GeneratedRegex(@"v(\d+)\.(\d+)-(\d+)V", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HardwareVersionRegex();

    private static byte[] ParseRecord(string line, int lineNumber)
    {
        if (line[0] != ':' || line.Length < 11 || line.Length % 2 == 0)
        {
            throw new InvalidDataException($"Intel HEX : ligne {lineNumber} mal formée.");
        }

        byte[] record;
        try
        {
            record = Convert.FromHexString(line.AsSpan(1));
        }
        catch (FormatException e)
        {
            throw new InvalidDataException($"Intel HEX : caractère non hexadécimal, ligne {lineNumber}.", e);
        }

        if (record.Length != record[0] + 5)
        {
            throw new InvalidDataException($"Intel HEX : longueur incohérente, ligne {lineNumber}.");
        }
        byte sum = 0;
        foreach (byte value in record)
        {
            sum += value;
        }
        if (sum != 0)
        {
            throw new InvalidDataException($"Intel HEX : somme de contrôle fausse, ligne {lineNumber}.");
        }
        return record;
    }

    private static List<FirmwareSegment> Merge(List<FirmwareSegment> chunks)
    {
        var merged = new List<FirmwareSegment>();
        var pending = new List<byte>();
        uint start = 0;
        foreach (FirmwareSegment chunk in chunks.Where(c => c.Data.Length > 0).OrderBy(c => c.Address))
        {
            if (pending.Count > 0 && chunk.Address < start + (uint)pending.Count)
            {
                throw new InvalidDataException($"Données en double à l'adresse 0x{chunk.Address:X8}.");
            }
            if (pending.Count > 0 && chunk.Address != start + (uint)pending.Count)
            {
                merged.Add(new FirmwareSegment(start, [.. pending]));
                pending.Clear();
            }
            if (pending.Count == 0)
            {
                start = chunk.Address;
            }
            pending.AddRange(chunk.Data);
        }
        if (pending.Count > 0)
        {
            merged.Add(new FirmwareSegment(start, [.. pending]));
        }
        return merged;
    }
}
