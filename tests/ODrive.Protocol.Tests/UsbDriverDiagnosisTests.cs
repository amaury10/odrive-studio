using FluentAssertions;
using ODrive.Protocol.Transport.WinUsb;
using Xunit;

namespace ODrive.Protocol.Tests;

public class UsbDriverDiagnosisTests
{
    private const string Instance = @"USB\VID_1209&PID_0D32&MI_02\9&221D82B1&0&0002";
    private static readonly Guid FirmwareGuid = WinUsbEnumerator.DeviceInterfaceGuid;

    [Fact]
    public void No_hint_when_every_present_interface_is_usable()
    {
        var present = new[] { new NativeInterfaceStatus(Instance, "WINUSB", [FirmwareGuid]) };

        UsbDriverDiagnosis.Explain(present, usableCount: 1).Should().BeNull();
    }

    [Fact]
    public void No_hint_when_no_board_is_plugged()
    {
        UsbDriverDiagnosis.Explain([], usableCount: 0).Should().BeNull();
    }

    [Fact]
    public void Winusb_without_interface_guid_is_reported_as_missing_guid_not_as_wrong_driver()
    {
        // Cas réel relevé sur une v3.6 en 0.5.6 : Service = WINUSB (winusb.inf), aucun DeviceInterfaceGUIDs.
        var present = new[] { new NativeInterfaceStatus(Instance, "WINUSB", []) };

        string? hint = UsbDriverDiagnosis.Explain(present, usableCount: 0);

        hint.Should().Contain("GUID").And.Contain("fix-usb");
        hint.Should().NotContain("autre pilote");
    }

    [Fact]
    public void Service_name_is_compared_without_case()
    {
        var present = new[] { new NativeInterfaceStatus(Instance, "WinUSB", []) };

        UsbDriverDiagnosis.Explain(present, usableCount: 0).Should().Contain("fix-usb");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void No_driver_at_all_is_repaired_by_the_tool(string? service)
    {
        // Cas réel après passage au firmware 0.5.6 officiel : installation en échec, aucun pilote.
        var status = new NativeInterfaceStatus(Instance, service, []);

        string? hint = UsbDriverDiagnosis.Explain([status], usableCount: 0);

        hint.Should().Contain("aucun pilote").And.Contain("fix-usb");
        hint.Should().NotContain("Zadig");
        WinUsbEnumerator.NeedsRepair(status).Should().BeTrue();
    }

    [Fact]
    public void A_board_with_another_driver_is_left_alone_by_the_repair()
    {
        WinUsbEnumerator.NeedsRepair(new NativeInterfaceStatus(Instance, "libusb0", [])).Should().BeFalse();
        WinUsbEnumerator.NeedsRepair(new NativeInterfaceStatus(Instance, "WINUSB", [])).Should().BeTrue();
        WinUsbEnumerator.NeedsRepair(new NativeInterfaceStatus(Instance, "WINUSB", [FirmwareGuid])).Should().BeFalse();
    }

    [Theory]
    [InlineData("libusb0")]
    [InlineData("libusbK")]
    public void Another_driver_points_to_zadig(string? service)
    {
        var present = new[] { new NativeInterfaceStatus(Instance, service, []) };

        string? hint = UsbDriverDiagnosis.Explain(present, usableCount: 0);

        hint.Should().Contain("Zadig");
        hint.Should().NotContain("fix-usb");
    }

    [Fact]
    public void Winusb_with_a_registered_guid_but_not_enumerated_asks_for_a_replug()
    {
        // Le GUID vient d'être écrit : WinUSB ne le publie qu'au prochain démarrage du périphérique.
        var present = new[] { new NativeInterfaceStatus(Instance, "WINUSB", [FirmwareGuid]) };

        UsbDriverDiagnosis.Explain(present, usableCount: 0).Should().Contain("rebrancher");
    }

    [Fact]
    public void Parses_a_multi_string_registry_value()
    {
        object value = new[] { "{EA0BD5C3-50F3-4888-84B4-74E50E1649DB}", "{11111111-2222-3333-4444-555555555555}" };

        UsbDriverDiagnosis.ParseGuids(value).Should().Equal(
            FirmwareGuid, new Guid("11111111-2222-3333-4444-555555555555"));
    }

    [Fact]
    public void Parses_a_single_string_registry_value()
    {
        UsbDriverDiagnosis.ParseGuids("{ea0bd5c3-50f3-4888-84b4-74e50e1649db}").Should().Equal(FirmwareGuid);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pas un guid")]
    [InlineData(42)]
    public void Ignores_missing_or_malformed_registry_values(object? value)
    {
        UsbDriverDiagnosis.ParseGuids(value).Should().BeEmpty();
    }

    [Theory]
    [InlineData(@"\\?\usb#vid_1209&pid_0d32&mi_02#9&221d82b1&0&0002#{ea0bd5c3-50f3-4888-84b4-74e50e1649db}", true)]
    [InlineData(@"\\?\USB#VID_1209&PID_0D32&MI_02#9&221D82B1&0&0002#{EA0BD5C3-50F3-4888-84B4-74E50E1649DB}", true)]
    [InlineData(@"\\?\usb#vid_1209&pid_0d32&mi_00#9&221d82b1&0&0000#{ea0bd5c3-50f3-4888-84b4-74e50e1649db}", false)]
    [InlineData(@"\\?\usb#vid_1d50&pid_606f#0001#{ea0bd5c3-50f3-4888-84b4-74e50e1649db}", false)]
    public void Recognises_the_native_interface_from_its_device_path(string path, bool expected)
    {
        // Un GUID installé par Zadig peut être partagé avec d'autres périphériques : le chemin fait foi.
        UsbDriverDiagnosis.IsNativeInterfacePath(path).Should().Be(expected);
    }
}
