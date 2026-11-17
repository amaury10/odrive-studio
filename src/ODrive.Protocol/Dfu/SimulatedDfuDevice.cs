using System.Buffers.Binary;
using static ODrive.Protocol.Dfu.DfuConstants;

namespace ODrive.Protocol.Dfu;

/// <summary>
/// Bootloader DfuSe d'un STM32F405 simulé : mêmes régions, mêmes états et mêmes règles que le vrai
/// (une écriture ne peut que passer des bits de 1 à 0, il faut effacer avant ; lecture refusée si la flash
/// est protégée). Sert aux tests et à l'application lancée avec <c>--sim</c>.
/// </summary>
#pragma warning disable CA1819 // mémoires exposées en tableaux bruts pour les tests
public sealed class SimulatedDfuDevice : IDfuTransport
{
    public const string DefaultSerialNumber = "SIM405DFU000";

    private static readonly string[] Names =
    [
        "@Internal Flash  /0x08000000/04*016Kg,01*064Kg,07*128Kg",
        "@Option Bytes  /0x1FFFC000/01*016 e",
        "@OTP Memory /0x1FFF7800/01*512 e,01*016 e",
        "@Device Feature/0xFFFF0000/01*004 e",
    ];

    private readonly IReadOnlyList<DfuSector> _sectors = DfuMemoryLayout.Parse(Names);
    private readonly byte[] _optionBytes = Filled(16);
    private readonly byte[] _feature = new byte[4];
    private int _alternate;
    private byte _state = StateIdle;
    private byte _status = StatusOk;
    private uint _pointer = FlashBase;
    private Action? _pending;

    /// <summary>Flash interne de 1 Mio, vierge (0xFF) au départ.</summary>
    public byte[] Flash { get; } = Filled(1024 * 1024);

    /// <summary>Mémoire OTP (512 octets) suivie de ses 16 octets de verrou ; vierge = carte non officielle.</summary>
    public byte[] Otp { get; } = Filled(528);

    /// <summary>Protection en lecture (RDP niveau 1) : lecture, effacement et écriture de la flash refusés.</summary>
    public bool ReadProtected { get; set; }

    /// <summary>Adresse où une écriture est volontairement faussée, pour tester la vérification.</summary>
    public uint? CorruptWritesAt { get; set; }

    /// <summary>Vrai après la demande de démarrage de l'application.</summary>
    public bool HasLeft { get; private set; }

    /// <summary>Levé quand le bootloader démarre l'application.</summary>
    public event EventHandler? Left;

    /// <summary>Remet le bootloader sur le bus, comme après un nouveau passage en mode DFU.</summary>
    public void Reenter()
    {
        HasLeft = false;
        _state = StateIdle;
        _status = StatusOk;
        _pending = null;
        _alternate = 0;
    }

    public string SerialNumber { get; init; } = DefaultSerialNumber;

    public IReadOnlyList<string> AlternateNames => Names;

    public void SetAlternate(int alternate)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(alternate, Names.Length);
        _alternate = alternate;
    }

    public void ControlOut(byte request, ushort value, ReadOnlySpan<byte> data)
    {
        ThrowIfGone();
        switch (request)
        {
            case RequestClrStatus:
            case RequestAbort:
                _state = StateIdle;
                _status = StatusOk;
                _pending = null;
                break;

            case RequestDnload when _state is StateIdle or StateDnloadIdle:
                byte[] copy = data.ToArray();
                if (copy.Length == 0)
                {
                    _pending = null;
                    _state = StateManifestSync;
                    break;
                }
                if (value == 1)
                {
                    throw Stall(StatusErrTarget);
                }
                _pending = value == 0 ? () => Execute(copy) : () => Write(value, copy);
                _state = StateDnloadSync;
                break;

            default:
                throw Stall(StatusErrStalledPacket);
        }
    }

    public int ControlIn(byte request, ushort value, Span<byte> buffer)
    {
        ThrowIfGone();
        switch (request)
        {
            case RequestGetStatus:
                if (_state == StateDnloadSync)
                {
                    _state = StateDnBusy; // l'opération s'exécute après cette réponse
                }
                else if (_state == StateDnBusy)
                {
                    _state = StateDnloadIdle;
                    _pending?.Invoke();
                    _pending = null;
                }
                else if (_state == StateManifestSync)
                {
                    _state = StateManifest;
                }

                buffer[0] = _status;
                buffer[1] = buffer[2] = buffer[3] = 0; // bwPollTimeout
                buffer[4] = _state;
                buffer[5] = 0;
                if (_state == StateManifest)
                {
                    HasLeft = true;
                    Left?.Invoke(this, EventArgs.Empty);
                }
                return 6;

            case RequestGetState:
                buffer[0] = _state;
                return 1;

            case RequestUpload when _state is StateIdle or StateUploadIdle && value >= 2:
                uint address = _pointer + (uint)((value - 2) * buffer.Length);
                if (ReadProtected && _alternate == 0)
                {
                    throw Stall(StatusErrVendor);
                }
                if (!TryLocate(address, buffer.Length, out byte[] memory, out int offset))
                {
                    throw Stall(StatusErrAddress);
                }
                memory.AsSpan(offset, buffer.Length).CopyTo(buffer);
                _state = StateUploadIdle;
                return buffer.Length;

            default:
                throw Stall(StatusErrStalledPacket);
        }
    }

    public void Dispose()
    {
    }

    private static byte[] Filled(int length)
    {
        byte[] memory = new byte[length];
        Array.Fill(memory, (byte)0xFF);
        return memory;
    }

    private void ThrowIfGone()
    {
        if (HasLeft)
        {
            throw new IOException("Le bootloader simulé a démarré l'application : il n'est plus sur le bus.");
        }
    }

    private IOException Stall(byte status)
    {
        _state = StateError;
        _status = status;
        _pending = null;
        return new IOException("Requête DFU refusée (STALL).");
    }

    private void Fail(byte status)
    {
        _state = StateError;
        _status = status;
    }

    private void Execute(byte[] command)
    {
        switch (command[0])
        {
            case CommandSetAddress when command.Length == 5:
                uint address = BinaryPrimitives.ReadUInt32LittleEndian(command.AsSpan(1));
                if (!_sectors.Any(s => s.Alternate == _alternate && address >= s.Address && address < s.Address + (uint)s.Length))
                {
                    Fail(StatusErrAddress);
                    return;
                }
                _pointer = address;
                break;

            case CommandErase when command.Length == 5:
                uint start = BinaryPrimitives.ReadUInt32LittleEndian(command.AsSpan(1));
                DfuSector? sector = _sectors.FirstOrDefault(s => s.Alternate == _alternate && s.Address == start);
                if (sector is null || !sector.IsErasable)
                {
                    Fail(StatusErrAddress);
                    return;
                }
                if (ReadProtected)
                {
                    Fail(StatusErrVendor);
                    return;
                }
                Flash.AsSpan((int)(start - FlashBase), sector.Length).Fill(0xFF);
                break;

            case CommandReadUnprotect when command.Length == 1:
                // Lever la protection efface toute la flash.
                Flash.AsSpan().Fill(0xFF);
                ReadProtected = false;
                break;

            default:
                Fail(StatusErrTarget);
                break;
        }
    }

    private void Write(ushort block, byte[] data)
    {
        uint address = _pointer + (uint)((block - 2) * data.Length);
        if (ReadProtected && _alternate == 0)
        {
            Fail(StatusErrVendor);
            return;
        }
        if (!TryLocate(address, data.Length, out byte[] memory, out int offset))
        {
            Fail(StatusErrAddress);
            return;
        }

        for (int i = 0; i < data.Length; i++)
        {
            memory[offset + i] &= data[i]; // la flash ne sait que passer des bits à 0
        }
        if (CorruptWritesAt is { } corrupt && corrupt >= address && corrupt < address + (uint)data.Length)
        {
            memory[offset + (int)(corrupt - address)] ^= 0x80;
        }
    }

    private bool TryLocate(uint address, int length, out byte[] memory, out int offset)
    {
        (uint Base, byte[] Memory) region = _alternate switch
        {
            0 => (FlashBase, Flash),
            1 => (0x1FFF_C000, _optionBytes),
            2 => (0x1FFF_7800, Otp),
            _ => (0xFFFF_0000, _feature),
        };
        memory = region.Memory;
        offset = (int)(address - region.Base);
        return address >= region.Base && (long)address - region.Base + length <= memory.Length;
    }
}
#pragma warning restore CA1819
