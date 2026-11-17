using FluentAssertions;
using ODrive.Protocol.Dfu;
using Xunit;

namespace ODrive.Protocol.Tests;

public class DfuMemoryLayoutTests
{
    [Fact]
    public void Parses_the_internal_flash_of_an_stm32f405()
    {
        IReadOnlyList<DfuSector> sectors = DfuMemoryLayout.Parse(
            ["@Internal Flash  /0x08000000/04*016Kg,01*064Kg,07*128Kg"]);

        sectors.Should().HaveCount(12);
        sectors[0].Should().Be(new DfuSector("Internal Flash", 0, 0x0800_0000, 16 * 1024, 'g'));
        sectors[4].Should().Be(new DfuSector("Internal Flash", 0, 0x0801_0000, 64 * 1024, 'g'));
        sectors[5].Address.Should().Be(0x0802_0000);
        sectors[11].Address.Should().Be(0x080E_0000);
        sectors.Sum(s => (long)s.Length).Should().Be(1024 * 1024);
        sectors[0].IsReadable.Should().BeTrue();
        sectors[0].IsErasable.Should().BeTrue();
        sectors[0].IsWritable.Should().BeTrue();
    }

    [Fact]
    public void Parses_byte_sized_regions_and_keeps_the_alternate_setting()
    {
        IReadOnlyList<DfuSector> sectors = DfuMemoryLayout.Parse(
        [
            "@Internal Flash  /0x08000000/04*016Kg,01*064Kg,07*128Kg",
            "@Option Bytes  /0x1FFFC000/01*016 e",
            "@OTP Memory /0x1FFF7800/01*512 e,01*016 e",
            "@Device Feature/0xFFFF0000/01*004 e",
        ]);

        DfuSector otp = sectors.Single(s => s.Region == "OTP Memory" && s.Address == 0x1FFF_7800);
        otp.Should().Be(new DfuSector("OTP Memory", 2, 0x1FFF_7800, 512, 'e'));
        otp.IsErasable.Should().BeFalse();
        sectors.Single(s => s.Address == 0x1FFF_7A00).Length.Should().Be(16);
        sectors.Single(s => s.Region == "Device Feature").Alternate.Should().Be(3);
    }

    [Fact]
    public void Ignores_alternates_that_do_not_describe_memory()
    {
        DfuMemoryLayout.Parse(["", "pas une région"]).Should().BeEmpty();
    }
}

public class FirmwareImageTests
{
    // Enregistrements écrits à la main : adresse étendue 0x0800, 4 octets à 0x0000, 2 octets à 0x4000.
    private const string Hex = """
        :020000040800F2
        :0400000000000220DA
        :02400000AABB59
        :00000001FF
        """;

    [Fact]
    public void Reads_intel_hex_with_extended_linear_addresses()
    {
        FirmwareImage image = FirmwareImage.FromIntelHex(new StringReader(Hex));

        image.Segments.Should().HaveCount(2);
        image.Segments[0].Address.Should().Be(0x0800_0000);
        image.Segments[0].Data.Should().Equal(0x00, 0x00, 0x02, 0x20);
        image.Segments[1].Address.Should().Be(0x0800_4000);
        image.Segments[1].Data.Should().Equal(0xAA, 0xBB);
    }

    [Fact]
    public void Merges_contiguous_records_into_one_segment()
    {
        const string contiguous = """
            :020000040800F2
            :020000000102FB
            :020002000304F5
            :00000001FF
            """;

        FirmwareImage image = FirmwareImage.FromIntelHex(new StringReader(contiguous));

        image.Segments.Should().ContainSingle().Which.Data.Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public void Rejects_a_bad_checksum()
    {
        Action act = () => FirmwareImage.FromIntelHex(new StringReader(":020000040800F3\n:00000001FF\n"));

        act.Should().Throw<InvalidDataException>().WithMessage("*ligne 1*");
    }

    [Fact]
    public void Rejects_a_file_without_end_record()
    {
        Action act = () => FirmwareImage.FromIntelHex(new StringReader(":020000040800F2\n"));

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Fills_gaps_with_erased_flash()
    {
        FirmwareImage image = FirmwareImage.FromIntelHex(new StringReader(Hex));

        image.Read(0x0800_0002, 4).Should().Equal(0x02, 0x20, 0xFF, 0xFF);
        image.Overlaps(0x0800_4000, 16).Should().BeTrue();
        image.Overlaps(0x0800_8000, 16).Should().BeFalse();
    }

    [Fact]
    public void Binary_image_starts_at_the_beginning_of_flash()
    {
        FirmwareImage image = FirmwareImage.FromBinary([1, 2, 3]);

        image.Segments.Should().ContainSingle();
        image.Segments[0].Address.Should().Be(0x0800_0000);
    }

    [Fact]
    public void Recognises_a_cortex_m_vector_table()
    {
        TestImages.Firmware(0x42).LooksLikeFirmware(out _).Should().BeTrue();
    }

    [Theory]
    [InlineData(0x0000_0000u, 0x0800_0101u)] // pile hors RAM
    [InlineData(0x2002_0000u, 0x0800_0100u)] // vecteur de reset sans bit Thumb
    [InlineData(0x2002_0000u, 0x2000_0101u)] // vecteur de reset hors flash
    public void Rejects_an_image_that_is_not_firmware(uint stackPointer, uint resetVector)
    {
        byte[] data = new byte[64];
        BitConverter.TryWriteBytes(data.AsSpan(0), stackPointer);
        BitConverter.TryWriteBytes(data.AsSpan(4), resetVector);

        FirmwareImage.FromBinary(data).LooksLikeFirmware(out string? reason).Should().BeFalse();
        reason.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("ODriveFirmware_v3.6-56V.hex", 3, 6, 56)]
    [InlineData("ODriveFirmware_v3.5-24V.elf.hex", 3, 5, 24)]
    [InlineData(@"C:\tmp\ODriveFirmware_v3.6-24V.hex", 3, 6, 24)]
    public void Reads_the_hardware_version_from_an_official_file_name(string name, int major, int minor, int variant)
    {
        FirmwareImage.HardwareVersionFromFileName(name).Should().Be((major, minor, variant));
    }

    [Fact]
    public void Unknown_file_names_give_no_hardware_version()
    {
        FirmwareImage.HardwareVersionFromFileName("sauvegarde.bin").Should().BeNull();
    }
}

internal static class TestImages
{
    /// <summary>Image de 40 Kio avec une table de vecteurs plausible, remplie d'un motif dépendant de seed.</summary>
    public static FirmwareImage Firmware(byte seed, int length = 40 * 1024)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i * 7 + seed);
        }
        BitConverter.TryWriteBytes(data.AsSpan(0), 0x2002_0000u);
        BitConverter.TryWriteBytes(data.AsSpan(4), 0x0800_0101u);
        return FirmwareImage.FromBinary(data);
    }
}

public class FirmwareUpdaterTests
{
    private static (SimulatedDfuDevice Sim, DfuSeDevice Dfu) Open()
    {
        var sim = new SimulatedDfuDevice();
        return (sim, new DfuSeDevice(sim));
    }

    [Fact]
    public void Reads_the_layout_from_the_device()
    {
        var (_, dfu) = Open();

        dfu.InternalFlash.Should().HaveCount(12);
        dfu.SerialNumber.Should().Be(SimulatedDfuDevice.DefaultSerialNumber);
    }

    [Fact]
    public void Backup_reads_the_whole_internal_flash()
    {
        var (sim, dfu) = Open();
        sim.Flash[0] = 0x11;
        sim.Flash[0x4000] = 0x22;
        sim.Flash[^1] = 0x33;
        var steps = new List<DfuProgress>();

        byte[] backup = FirmwareUpdater.ReadFlash(dfu, new SyncProgress(steps.Add));

        backup.Should().HaveCount(1024 * 1024);
        backup.Should().Equal(sim.Flash);
        steps.Should().NotBeEmpty();
        steps[^1].Done.Should().Be(steps[^1].Total);
    }

    [Fact]
    public void Backup_fails_clearly_when_the_flash_is_read_protected()
    {
        var (sim, dfu) = Open();
        sim.ReadProtected = true;

        Action act = () => FirmwareUpdater.ReadFlash(dfu, null);

        act.Should().Throw<DfuException>().WithMessage("*protégée en lecture*");
    }

    [Fact]
    public void Flash_erases_writes_and_verifies()
    {
        var (sim, dfu) = Open();
        Array.Fill(sim.Flash, (byte)0x5A); // ancien firmware + ancienne configuration
        FirmwareImage image = TestImages.Firmware(1);

        FirmwareUpdater.Flash(dfu, image, null);

        sim.Flash.AsSpan(0, 40 * 1024).ToArray().Should().Equal(image.Segments[0].Data);
        // Tout le reste de la flash est effacé, configuration comprise.
        sim.Flash.AsSpan(40 * 1024).ToArray().Should().OnlyContain(b => b == 0xFF);
    }

    [Fact]
    public void A_backup_restores_the_board_exactly()
    {
        var (sim, dfu) = Open();
        byte[] original = TestImages.Firmware(9, 300 * 1024).Read(0x0800_0000, 1024 * 1024);
        original[0xE_0000] = 0x77; // donnée de configuration en fin de flash
        original.CopyTo(sim.Flash, 0);
        byte[] backup = FirmwareUpdater.ReadFlash(dfu, null);

        FirmwareUpdater.Flash(dfu, TestImages.Firmware(3), null);
        sim.Flash.Should().NotEqual(original);
        FirmwareUpdater.Flash(dfu, FirmwareImage.FromBinary(backup), null);

        sim.Flash.Should().Equal(original);
    }

    [Fact]
    public void Flash_refuses_an_image_that_is_not_firmware_and_leaves_the_board_untouched()
    {
        var (sim, dfu) = Open();
        Array.Fill(sim.Flash, (byte)0x5A);

        Action act = () => FirmwareUpdater.Flash(dfu, FirmwareImage.FromBinary(new byte[1024]), null);

        act.Should().Throw<InvalidDataException>();
        sim.Flash.Should().OnlyContain(b => b == 0x5A);
    }

    [Fact]
    public void Flash_refuses_an_image_larger_than_the_flash()
    {
        var (sim, dfu) = Open();
        Array.Fill(sim.Flash, (byte)0x5A);

        Action act = () => FirmwareUpdater.Flash(dfu, TestImages.Firmware(1, 1024 * 1024 + 4), null);

        act.Should().Throw<InvalidDataException>().WithMessage("*hors de la flash*");
        sim.Flash.Should().OnlyContain(b => b == 0x5A);
    }

    [Fact]
    public void Flash_reports_a_verification_failure()
    {
        var (sim, dfu) = Open();
        sim.CorruptWritesAt = 0x0800_0100;

        Action act = () => FirmwareUpdater.Flash(dfu, TestImages.Firmware(1), null);

        act.Should().Throw<DfuException>().WithMessage("*0x08000100*");
    }

    [Fact]
    public void Flash_can_be_cancelled_before_anything_is_erased()
    {
        var (sim, dfu) = Open();
        Array.Fill(sim.Flash, (byte)0x5A);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Action act = () => FirmwareUpdater.Flash(dfu, TestImages.Firmware(1), null, cancellation.Token);

        act.Should().Throw<OperationCanceledException>();
        sim.Flash.Should().OnlyContain(b => b == 0x5A);
    }

    [Fact]
    public void Leave_starts_the_application()
    {
        var (sim, dfu) = Open();

        dfu.Leave();

        sim.HasLeft.Should().BeTrue();
    }

    [Fact]
    public void Reads_an_empty_otp_as_unknown_hardware()
    {
        var (_, dfu) = Open();

        FirmwareUpdater.ReadHardwareVersion(dfu).Should().BeNull();
    }

    [Fact]
    public void Reads_the_hardware_version_from_the_otp_of_an_official_board()
    {
        var (sim, dfu) = Open();
        // tools/odrive/dfu.py:260-272 : 0xFE, puis la version aux octets 3 à 5.
        new byte[] { 0xFE, 0, 0, 3, 6, 56 }.CopyTo(sim.Otp, 0);

        FirmwareUpdater.ReadHardwareVersion(dfu).Should().Be((3, 6, 56));
    }

    [Fact]
    public void Reads_the_hardware_version_from_the_second_otp_block()
    {
        var (sim, dfu) = Open();
        sim.Otp.AsSpan(0, 16).Clear(); // premier bloc invalidé (dfu.py:267-268)
        new byte[] { 0xFE, 0, 0, 3, 5, 24 }.CopyTo(sim.Otp, 16);

        FirmwareUpdater.ReadHardwareVersion(dfu).Should().Be((3, 5, 24));
    }

    private sealed class SyncProgress(Action<DfuProgress> report) : IProgress<DfuProgress>
    {
        public void Report(DfuProgress value) => report(value);
    }
}
