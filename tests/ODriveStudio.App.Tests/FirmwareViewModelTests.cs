using System.IO;
using FluentAssertions;
using ODrive.Protocol;
using ODrive.Protocol.Dfu;
using ODrive.Protocol.Simulation;
using ODriveStudio.App.ViewModels;
using Xunit;

namespace ODriveStudio.App.Tests;

public sealed class FirmwareViewModelTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"odrive-fw-{Guid.NewGuid():N}");
    private readonly SimulatedDfuDevice _sim = new();
    private readonly SimulatedDfuSource _source;
    private readonly FakeDialogs _dialogs = new();
    private readonly List<string> _status = [];
    private ODriveDevice? _device;
    private string? _hardware = "v3.6-56V";

    public FirmwareViewModelTests()
    {
        Directory.CreateDirectory(_folder);
        _source = new SimulatedDfuSource(_sim) { IsPresent = true };
        ValidImage(0x10).CopyTo(_sim.Flash, 0);
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private FirmwareViewModel Create() =>
        new(_source, _dialogs, () => _device, () => _hardware, _status.Add);

    private static byte[] ValidImage(byte seed, int length = 48 * 1024)
    {
        byte[] data = new byte[length];
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = (byte)(i * 3 + seed);
        }
        BitConverter.TryWriteBytes(data.AsSpan(0), 0x2002_0000u);
        BitConverter.TryWriteBytes(data.AsSpan(4), 0x0800_0101u);
        return data;
    }

    private string WriteFile(string name, byte[] content)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void Shows_nothing_to_do_when_no_board_is_in_dfu_mode()
    {
        _source.IsPresent = false;
        FirmwareViewModel firmware = Create();

        firmware.Refresh();

        firmware.IsDfuReady.Should().BeFalse();
        firmware.BackupCommand.CanExecute(null).Should().BeFalse();
        firmware.FlashCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Describes_a_clone_board_in_dfu_mode()
    {
        FirmwareViewModel firmware = Create();

        firmware.Refresh();

        firmware.IsDfuReady.Should().BeTrue();
        firmware.DfuSummary.Should().Contain(SimulatedDfuDevice.DefaultSerialNumber).And.Contain("OTP vierge");
    }

    [Fact]
    public void Describes_an_official_board_by_its_otp()
    {
        new byte[] { 0xFE, 0, 0, 3, 6, 24 }.CopyTo(_sim.Otp, 0);
        FirmwareViewModel firmware = Create();

        firmware.Refresh();

        firmware.DfuSummary.Should().Contain("v3.6-24V");
    }

    [Fact]
    public void Shows_the_driver_hint_when_the_bootloader_cannot_be_opened()
    {
        _source.Hint = "Pilote : utiliser Zadig.";
        FirmwareViewModel firmware = Create();

        firmware.Refresh();

        firmware.IsDfuReady.Should().BeFalse();
        firmware.DfuSummary.Should().Contain("Zadig");
    }

    [Fact]
    public async Task Backup_writes_the_whole_flash_to_the_chosen_file()
    {
        FirmwareViewModel firmware = Create();
        firmware.Refresh();
        _dialogs.SavePath = Path.Combine(_folder, "sauvegarde.bin");

        await firmware.BackupCommand.ExecuteAsync(null);

        File.ReadAllBytes(_dialogs.SavePath).Should().Equal(_sim.Flash);
        firmware.LastBackupPath.Should().Be(_dialogs.SavePath);
        firmware.Log.Should().Contain(line => line.Contains("SHA-256"));
        firmware.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Backup_reports_a_read_protected_flash_and_writes_no_file()
    {
        _sim.ReadProtected = true;
        FirmwareViewModel firmware = Create();
        firmware.Refresh();
        _dialogs.SavePath = Path.Combine(_folder, "sauvegarde.bin");

        await firmware.BackupCommand.ExecuteAsync(null);

        File.Exists(_dialogs.SavePath).Should().BeFalse();
        firmware.LastBackupPath.Should().BeNull();
        firmware.Log.Should().Contain(line => line.Contains("protégée en lecture"));
    }

    [Fact]
    public void Choosing_a_file_that_is_not_firmware_is_refused()
    {
        FirmwareViewModel firmware = Create();
        firmware.Refresh();
        _dialogs.OpenPath = WriteFile("vide.bin", new byte[4096]);

        firmware.ChooseFileCommand.Execute(null);

        firmware.HasImage.Should().BeFalse();
        firmware.ImageSummary.Should().Contain("n'est pas un firmware");
        firmware.FlashCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public void Choosing_a_valid_file_enables_flashing()
    {
        FirmwareViewModel firmware = Create();
        firmware.Refresh();
        _dialogs.OpenPath = WriteFile("ODriveFirmware_v3.6-56V.bin", ValidImage(0x20));

        firmware.ChooseFileCommand.Execute(null);

        firmware.HasImage.Should().BeTrue();
        firmware.ImageSummary.Should().Contain("v3.6-56V");
        firmware.FlashCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public async Task Flash_does_nothing_when_the_user_declines()
    {
        FirmwareViewModel firmware = Create();
        firmware.Refresh();
        _dialogs.OpenPath = WriteFile("ODriveFirmware_v3.6-56V.bin", ValidImage(0x20));
        firmware.ChooseFileCommand.Execute(null);
        byte[] before = [.. _sim.Flash];
        _dialogs.Answer = false;

        await firmware.FlashCommand.ExecuteAsync(null);

        _sim.Flash.Should().Equal(before);
        _dialogs.Questions.Should().ContainSingle();
    }

    [Fact]
    public async Task Flash_warns_that_no_backup_was_made_and_that_everything_is_erased()
    {
        FirmwareViewModel firmware = Create();
        firmware.Refresh();
        _dialogs.OpenPath = WriteFile("ODriveFirmware_v3.6-56V.bin", ValidImage(0x20));
        firmware.ChooseFileCommand.Execute(null);
        _dialogs.Answer = false;

        await firmware.FlashCommand.ExecuteAsync(null);

        _dialogs.Questions[0].Should().Contain("AUCUNE SAUVEGARDE").And.Contain("configuration");
    }

    [Fact]
    public async Task Flash_writes_the_image_then_starts_the_firmware()
    {
        FirmwareViewModel firmware = Create();
        firmware.Refresh();
        byte[] image = ValidImage(0x20);
        _dialogs.OpenPath = WriteFile("ODriveFirmware_v3.6-56V.bin", image);
        firmware.ChooseFileCommand.Execute(null);

        await firmware.FlashCommand.ExecuteAsync(null);

        _sim.Flash.AsSpan(0, image.Length).ToArray().Should().Equal(image);
        _sim.Flash.AsSpan(image.Length).ToArray().Should().OnlyContain(b => b == 0xFF);
        _sim.HasLeft.Should().BeTrue();
        firmware.Log.Should().Contain(line => line.Contains("vérifiée"));
        firmware.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task Flash_mentions_the_backup_made_during_the_session()
    {
        FirmwareViewModel firmware = Create();
        firmware.Refresh();
        _dialogs.SavePath = Path.Combine(_folder, "sauvegarde.bin");
        await firmware.BackupCommand.ExecuteAsync(null);
        _dialogs.OpenPath = WriteFile("ODriveFirmware_v3.6-56V.bin", ValidImage(0x20));
        firmware.ChooseFileCommand.Execute(null);
        _dialogs.Answer = false;

        await firmware.FlashCommand.ExecuteAsync(null);

        _dialogs.Questions[^1].Should().Contain("sauvegarde.bin").And.NotContain("AUCUNE SAUVEGARDE");
    }

    [Fact]
    public async Task Flash_is_refused_when_the_file_targets_another_board_than_the_otp_says()
    {
        new byte[] { 0xFE, 0, 0, 3, 6, 24 }.CopyTo(_sim.Otp, 0);
        FirmwareViewModel firmware = Create();
        firmware.Refresh();
        _dialogs.OpenPath = WriteFile("ODriveFirmware_v3.6-56V.bin", ValidImage(0x20));
        firmware.ChooseFileCommand.Execute(null);
        byte[] before = [.. _sim.Flash];

        await firmware.FlashCommand.ExecuteAsync(null);

        _sim.Flash.Should().Equal(before);
        _dialogs.Questions.Should().BeEmpty();
        firmware.Log.Should().Contain(line => line.Contains("v3.6-24V") && line.Contains("v3.6-56V"));
    }

    [Fact]
    public async Task Flash_warns_when_the_file_variant_differs_from_what_the_clone_firmware_reported()
    {
        _hardware = "v3.6-56V";
        FirmwareViewModel firmware = Create();
        firmware.Refresh();
        _dialogs.OpenPath = WriteFile("ODriveFirmware_v3.6-24V.bin", ValidImage(0x20));
        firmware.ChooseFileCommand.Execute(null);
        _dialogs.Answer = false;

        await firmware.FlashCommand.ExecuteAsync(null);

        _dialogs.Questions[0].Should().Contain("v3.6-24V").And.Contain("v3.6-56V").And.Contain("ATTENTION");
    }

    [Fact]
    public async Task Leave_starts_the_firmware()
    {
        FirmwareViewModel firmware = Create();
        firmware.Refresh();

        await firmware.LeaveDfuCommand.ExecuteAsync(null);

        _sim.HasLeft.Should().BeTrue();
    }

    [Fact]
    public async Task Entering_dfu_mode_asks_first_and_calls_the_board()
    {
        _source.IsPresent = false;
        SimulatedODrive board = SimulatedODrive.CreateReference();
        int calls = 0;
        board.OnCall("enter_dfu_mode", _ => calls++);
        _device = await ODriveDevice.ConnectAsync(new SimulatedTransport(board));
        FirmwareViewModel firmware = Create();
        firmware.Refresh();
        _dialogs.Answer = false;

        await firmware.EnterDfuCommand.ExecuteAsync(null);
        calls.Should().Be(0);

        _dialogs.Answer = true;
        await firmware.EnterDfuCommand.ExecuteAsync(null);
        calls.Should().Be(1);
        await _device.DisposeAsync();
    }

    [Fact]
    public async Task Entering_dfu_mode_is_refused_while_an_axis_is_active()
    {
        _source.IsPresent = false;
        SimulatedODrive board = SimulatedODrive.CreateReference();
        int calls = 0;
        board.OnCall("enter_dfu_mode", _ => calls++);
        board.Set("axis0.current_state", 8); // CLOSED_LOOP_CONTROL
        _device = await ODriveDevice.ConnectAsync(new SimulatedTransport(board));
        FirmwareViewModel firmware = Create();
        firmware.Refresh();

        await firmware.EnterDfuCommand.ExecuteAsync(null);

        calls.Should().Be(0);
        firmware.Log.Should().Contain(line => line.Contains("IDLE"));
        await _device.DisposeAsync();
    }
}
