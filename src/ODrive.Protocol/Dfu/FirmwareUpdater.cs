namespace ODrive.Protocol.Dfu;

/// <summary>
/// Sauvegarde et écriture de la flash par le bootloader, d'après <c>ref/ODrive/tools/odrive/dfu.py</c>
/// (<c>update_device</c>, lignes 412-463). Opérations bloquantes : à lancer hors du fil d'interface.
/// </summary>
public static class FirmwareUpdater
{
    private const uint OtpAddress = 0x1FFF_7800; // dfu.py:265

    /// <summary>
    /// Lit toute la flash interne (firmware et configuration). Le résultat, écrit tel quel dans un fichier
    /// <c>.bin</c>, se restaure avec <see cref="Flash"/>.
    /// </summary>
    /// <exception cref="DfuException">Flash protégée en lecture, ou erreur du bootloader.</exception>
    public static byte[] ReadFlash(DfuSeDevice device, IProgress<DfuProgress>? progress, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        IReadOnlyList<DfuSector> sectors = device.InternalFlash;
        byte[] flash = new byte[sectors.Sum(s => s.Length)];
        int offset = 0;
        for (int i = 0; i < sectors.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new DfuProgress("Lecture", i, sectors.Count));
            device.ReadSector(sectors[i]).CopyTo(flash, offset);
            offset += sectors[i].Length;
        }
        progress?.Report(new DfuProgress("Lecture", sectors.Count, sectors.Count));
        return flash;
    }

    /// <summary>
    /// Efface toute la flash interne (configuration comprise), écrit l'image puis la relit pour la vérifier.
    /// La carte reste dans le bootloader : appeler <see cref="DfuSeDevice.Leave"/> pour démarrer le firmware.
    /// <para>
    /// L'annulation n'est prise en compte qu'avant l'effacement : une fois commencée, l'écriture va au bout.
    /// Si elle échoue, la carte n'a plus de firmware mais son bootloader, en ROM, reste utilisable.
    /// </para>
    /// </summary>
    /// <exception cref="InvalidDataException">L'image n'est pas un firmware ou sort de la flash ; rien n'est effacé.</exception>
    /// <exception cref="DfuException">Erreur du bootloader ou vérification en échec.</exception>
    public static void Flash(DfuSeDevice device, FirmwareImage image, IProgress<DfuProgress>? progress,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(image);
        IReadOnlyList<DfuSector> sectors = device.InternalFlash;

        if (!image.LooksLikeFirmware(out string? reason))
        {
            throw new InvalidDataException("Ce fichier n'est pas un firmware pour cette carte : " + reason);
        }
        uint flashStart = sectors[0].Address;
        long flashEnd = sectors[^1].Address + (long)sectors[^1].Length;
        foreach (FirmwareSegment segment in image.Segments)
        {
            if (segment.Address < flashStart || segment.End > flashEnd || segment.End < segment.Address)
            {
                throw new InvalidDataException(
                    $"L'image contient des données hors de la flash interne (0x{segment.Address:X8}, {segment.Data.Length} octets).");
            }
        }

        var touched = new List<(DfuSector Sector, byte[] Data)>();
        foreach (DfuSector sector in sectors)
        {
            if (image.Overlaps(sector.Address, sector.Length))
            {
                byte[] data = image.Read(sector.Address, sector.Length);
                if (data.AsSpan().ContainsAnyExcept((byte)0xFF))
                {
                    touched.Add((sector, data));
                }
            }
        }

        ct.ThrowIfCancellationRequested();

        for (int i = 0; i < sectors.Count; i++)
        {
            progress?.Report(new DfuProgress("Effacement", i, sectors.Count));
            device.EraseSector(sectors[i]);
        }
        progress?.Report(new DfuProgress("Effacement", sectors.Count, sectors.Count));

        for (int i = 0; i < touched.Count; i++)
        {
            progress?.Report(new DfuProgress("Écriture", i, touched.Count));
            device.WriteSector(touched[i].Sector, touched[i].Data);
        }
        progress?.Report(new DfuProgress("Écriture", touched.Count, touched.Count));

        for (int i = 0; i < touched.Count; i++)
        {
            progress?.Report(new DfuProgress("Vérification", i, touched.Count));
            (DfuSector sector, byte[] expected) = touched[i];
            byte[] actual = device.ReadSector(sector);
            int mismatch = expected.AsSpan().CommonPrefixLength(actual);
            if (mismatch != expected.Length)
            {
                mismatch -= mismatch % 16;
                throw new DfuException(
                    $"Vérification en échec vers 0x{sector.Address + (uint)mismatch:X8} : "
                    + $"attendu {Convert.ToHexString(expected, mismatch, 16)}, "
                    + $"relu {Convert.ToHexString(actual, mismatch, 16)}. Ne pas quitter le mode DFU : relancer l'écriture.");
            }
        }
        progress?.Report(new DfuProgress("Vérification", touched.Count, touched.Count));
    }

    /// <summary>
    /// Version matérielle inscrite en mémoire OTP par ODrive Robotics (dfu.py:260-272), ou <c>null</c> si
    /// l'OTP est vierge : c'est le cas des cartes non officielles, pour lesquelles l'outil d'origine refuse
    /// de flasher (dfu.py:395-403).
    /// </summary>
    public static (int Major, int Minor, int Variant)? ReadHardwareVersion(DfuSeDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        DfuSector? sector = device.Sectors.FirstOrDefault(s => s.Region == DfuSector.OtpMemory && s.Address == OtpAddress);
        if (sector is null)
        {
            return null;
        }

        ReadOnlySpan<byte> otp = device.ReadSector(sector);
        if (otp[0] == 0)
        {
            otp = otp[16..];
        }
        return otp[0] == 0xFE ? (otp[3], otp[4], otp[5]) : null;
    }
}
