using System.Buffers.Binary;
using FluentAssertions;
using ODrive.Protocol.Dfu;
using Xunit;

namespace ODrive.Protocol.Tests;

public class ElfImageTests
{
    private const uint Load = 1;

    /// <summary>ELF32 petit-boutiste minimal : en-tête, table des segments, puis les données.</summary>
    private static byte[] Elf(ushort machine, params (uint Type, uint VirtualAddress, uint PhysicalAddress, byte[] Data)[] segments)
    {
        const int headerSize = 52, entrySize = 32;
        int dataOffset = headerSize + entrySize * segments.Length;
        byte[] file = new byte[dataOffset + segments.Sum(s => s.Data.Length)];
        new byte[] { 0x7F, (byte)'E', (byte)'L', (byte)'F', 1, 1, 1 }.CopyTo(file, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(16), 2);            // e_type = EXEC
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(18), machine);      // e_machine
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(28), headerSize);   // e_phoff
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(42), entrySize);    // e_phentsize
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(44), (ushort)segments.Length);

        int offset = dataOffset;
        for (int i = 0; i < segments.Length; i++)
        {
            Span<byte> entry = file.AsSpan(headerSize + i * entrySize, entrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(entry, segments[i].Type);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], segments[i].VirtualAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], segments[i].PhysicalAddress);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[16..], (uint)segments[i].Data.Length);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[20..], (uint)segments[i].Data.Length + 64); // p_memsz
            segments[i].Data.CopyTo(file, offset);
            offset += segments[i].Data.Length;
        }
        return file;
    }

    [Fact]
    public void Loads_segments_at_their_physical_address()
    {
        // .data s'exécute en RAM (adresse virtuelle) mais est stockée en flash (adresse physique) : c'est
        // l'adresse physique qu'il faut écrire.
        byte[] elf = Elf(40,
            (Load, 0x0800_0000, 0x0800_0000, [1, 2, 3, 4]),
            (Load, 0x2000_0000, 0x0800_0004, [5, 6]));

        FirmwareImage image = FirmwareImage.FromElf(elf);

        image.Segments.Should().ContainSingle();
        image.Segments[0].Address.Should().Be(0x0800_0000);
        image.Segments[0].Data.Should().Equal(1, 2, 3, 4, 5, 6);
    }

    [Fact]
    public void Ignores_segments_that_are_not_loaded_or_have_no_file_content()
    {
        byte[] elf = Elf(40,
            (Load, 0x0800_0000, 0x0800_0000, [1, 2]),
            (6, 0x0800_1000, 0x0800_1000, [9, 9]),  // PT_PHDR
            (Load, 0x2000_1000, 0x2000_1000, []));   // .bss : rien dans le fichier

        FirmwareImage.FromElf(elf).Segments.Should().ContainSingle().Which.Data.Should().Equal(1, 2);
    }

    [Fact]
    public void Rejects_a_file_that_is_not_elf()
    {
        Action act = () => FirmwareImage.FromElf(new byte[128]);

        act.Should().Throw<InvalidDataException>().WithMessage("*ELF*");
    }

    [Fact]
    public void Rejects_an_elf_for_another_processor()
    {
        Action act = () => FirmwareImage.FromElf(Elf(62, (Load, 0, 0, [1]))); // x86-64

        act.Should().Throw<InvalidDataException>().WithMessage("*ARM*");
    }

    [Fact]
    public void Rejects_a_truncated_elf()
    {
        byte[] elf = Elf(40, (Load, 0x0800_0000, 0x0800_0000, [1, 2, 3, 4]));

        Action act = () => FirmwareImage.FromElf(elf.AsSpan(..^2));

        act.Should().Throw<InvalidDataException>().WithMessage("*tronqué*");
    }
}
