using System.Buffers.Binary;
using static ODrive.Protocol.Dfu.DfuConstants;

namespace ODrive.Protocol.Dfu;

/// <summary>
/// Bootloader ROM du STM32 vu par DfuSe : effacement, écriture et lecture de secteurs, démarrage de
/// l'application. Séquences reprises de <c>ref/ODrive/tools/odrive/dfuse/DfuDevice.py</c>, l'outil avec
/// lequel les cartes ODrive v3 sont mises à jour.
/// </summary>
public sealed class DfuSeDevice : IDisposable
{
    private readonly IDfuTransport _transport;
    private int _alternate = -1;

    public DfuSeDevice(IDfuTransport transport)
    {
        ArgumentNullException.ThrowIfNull(transport);
        _transport = transport;
        Sectors = DfuMemoryLayout.Parse(transport.AlternateNames);
        InternalFlash = [.. Sectors.Where(s => s.Region == DfuSector.InternalFlash)];
        if (InternalFlash.Count == 0)
        {
            throw new DfuException("Le bootloader n'annonce aucune flash interne : ce n'est pas un STM32 en mode DFU.");
        }
    }

    public string SerialNumber => _transport.SerialNumber;

    /// <summary>Tous les secteurs annoncés (flash, octets d'option, OTP…).</summary>
    public IReadOnlyList<DfuSector> Sectors { get; }

    /// <summary>Secteurs de la flash interne, par adresse croissante.</summary>
    public IReadOnlyList<DfuSector> InternalFlash { get; }

    public void Dispose() => _transport.Dispose();

    /// <summary>Efface un secteur (DfuDevice.py:199-204).</summary>
    public void EraseSector(DfuSector sector)
    {
        ArgumentNullException.ThrowIfNull(sector);
        Select(sector);
        Span<byte> command = stackalloc byte[5];
        command[0] = CommandErase;
        BinaryPrimitives.WriteUInt32LittleEndian(command[1..], sector.Address);
        Command(command, $"l'effacement du secteur 0x{sector.Address:X8}");
    }

    /// <summary>
    /// Écrit un secteur entier, déjà effacé (DfuDevice.py:206-219). Les blocs entièrement à 0xFF ne sont pas
    /// envoyés : c'est l'état d'une flash effacée.
    /// </summary>
    public void WriteSector(DfuSector sector, ReadOnlySpan<byte> data)
    {
        ArgumentNullException.ThrowIfNull(sector);
        if (data.Length != sector.Length)
        {
            throw new ArgumentException("Les données doivent couvrir tout le secteur.", nameof(data));
        }

        Select(sector);
        SetAddress(sector.Address);
        int size = TransferSize(sector);
        for (int block = 0; block * size < data.Length; block++)
        {
            ReadOnlySpan<byte> chunk = data.Slice(block * size, size);
            if (!chunk.ContainsAnyExcept((byte)0xFF))
            {
                continue;
            }
            // AN3156 : adresse = pointeur + (wBlockNum − 2) × taille du transfert.
            _transport.ControlOut(RequestDnload, (ushort)(block + 2), chunk);
            WaitWhileBusy($"l'écriture à 0x{sector.Address + (uint)(block * size):X8}");
        }
        ToIdle();
    }

    /// <summary>Lit un secteur entier (DfuDevice.py:221-239).</summary>
    public byte[] ReadSector(DfuSector sector)
    {
        ArgumentNullException.ThrowIfNull(sector);
        Select(sector);
        SetAddress(sector.Address);
        int size = TransferSize(sector);
        byte[] data = new byte[sector.Length];
        for (int block = 0; block * size < data.Length; block++)
        {
            int read;
            try
            {
                read = _transport.ControlIn(RequestUpload, (ushort)(block + 2), data.AsSpan(block * size, size));
            }
            catch (IOException e) when (e is not DfuException)
            {
                // Requête refusée : l'état du bootloader dit pourquoi (flash protégée en lecture…).
                (byte status, byte state, _) = GetStatus();
                throw Error($"la lecture à 0x{sector.Address + (uint)(block * size):X8}", status, state, e);
            }
            if (read != size)
            {
                (byte status, byte state, _) = GetStatus();
                throw Error($"la lecture à 0x{sector.Address + (uint)(block * size):X8}", status, state, null);
            }
        }
        ToIdle();
        return data;
    }

    /// <summary>
    /// Quitte le bootloader et démarre l'application (DfuDevice.py:241-251). La carte disparaît du bus puis
    /// réapparaît en mode normal, sauf si son interrupteur est sur DFU.
    /// </summary>
    public void Leave()
    {
        Select(InternalFlash[0]);
        SetAddress(FlashBase);
        _transport.ControlOut(RequestDnload, 0, []);
        try
        {
            GetStatus(); // déclenche le « manifest » : la carte redémarre
        }
        catch (IOException)
        {
            // Attendu si la carte a déjà quitté le bus.
        }
    }

    private static int TransferSize(DfuSector sector)
    {
        // DfuDevice.py:210 : pgcd(taille du secteur, 2048).
        int a = sector.Length, b = MaxTransferSize;
        while (b != 0)
        {
            (a, b) = (b, a % b);
        }
        return a;
    }

    private void Select(DfuSector sector)
    {
        if (_alternate != sector.Alternate)
        {
            _transport.SetAlternate(sector.Alternate);
            _alternate = sector.Alternate;
        }
        ToIdle();
    }

    private void SetAddress(uint address)
    {
        Span<byte> command = stackalloc byte[5];
        command[0] = CommandSetAddress;
        BinaryPrimitives.WriteUInt32LittleEndian(command[1..], address);
        Command(command, $"le positionnement à 0x{address:X8}");
        ToIdle(); // DfuDevice.py:192-196 : ABORT pour revenir de dfuDNLOAD-IDLE à dfuIDLE
    }

    private void Command(ReadOnlySpan<byte> command, string what)
    {
        _transport.ControlOut(RequestDnload, 0, command);
        WaitWhileBusy(what);
    }

    private void WaitWhileBusy(string what)
    {
        // Le premier GETSTATUS lance l'opération et répond dfuDNBUSY avec la durée à attendre.
        (byte status, byte state, int poll) = GetStatus();
        while (state == StateDnBusy)
        {
            if (poll > 0)
            {
                Thread.Sleep(poll);
            }
            (status, state, poll) = GetStatus();
        }
        if (state != StateDnloadIdle)
        {
            throw Error(what, status, state, null);
        }
    }

    private void ToIdle()
    {
        (_, byte state, _) = GetStatus();
        if (state == StateIdle)
        {
            return;
        }

        _transport.ControlOut(state == StateError ? RequestClrStatus : RequestAbort, 0, []);
        (byte status, state, _) = GetStatus();
        if (state != StateIdle)
        {
            throw Error("le retour à l'état de repos", status, state, null);
        }
    }

    private (byte Status, byte State, int PollMilliseconds) GetStatus()
    {
        Span<byte> buffer = stackalloc byte[6];
        int read = _transport.ControlIn(RequestGetStatus, 0, buffer);
        if (read != 6)
        {
            throw new DfuException($"Réponse GETSTATUS de {read} octets au lieu de 6.");
        }
        return (buffer[0], buffer[4], buffer[1] | (buffer[2] << 8) | (buffer[3] << 16));
    }

    private static DfuException Error(string what, byte status, byte state, Exception? inner)
    {
        string message = status == StatusErrVendor
            ? $"Le bootloader a refusé {what} : la flash est protégée en lecture (RDP). La lever efface toute "
              + "la flash, firmware d'origine compris."
            : $"Le bootloader a refusé {what} (code DFU 0x{status:X2}, état {state}).";
        return inner is null ? new DfuException(message) : new DfuException(message, inner);
    }
}
