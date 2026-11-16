using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ODrive.Protocol.Transport.WinUsb;

/// <summary>Interface native d'une carte ODrive, telle que Windows l'énumère.</summary>
/// <param name="DevicePath">Chemin d'interface à passer à <see cref="WinUsbTransport.Open"/>.</param>
public sealed record WinUsbDeviceInfo(string DevicePath);

/// <summary>Énumération des cartes ODrive par leur <c>DeviceInterfaceGUID</c> (SetupAPI).</summary>
public static class WinUsbEnumerator
{
    /// <summary><c>ref/ODrive/Firmware/Board/v3/Src/usbd_desc.c:94</c></summary>
    public const ushort VendorId = 0x1209;

    /// <summary><c>ref/ODrive/Firmware/Board/v3/Src/usbd_desc.c:97</c></summary>
    public const ushort ProductId = 0x0D32;

    /// <summary>Numéro de l'interface native dans le périphérique composite (<c>usbd_cdc.c:286</c>).</summary>
    public const int NativeInterfaceNumber = 2;

    /// <summary>
    /// GUID d'interface annoncé par les descripteurs MS OS du firmware
    /// (<c>ref/ODrive/Firmware/ThirdParty/STM32_USB_Device_Library/Class/CDC/Src/usbd_cdc.c:865-895</c>).
    /// Windows installe WinUSB tout seul et publie l'interface sous ce GUID.
    /// </summary>
    public static readonly Guid DeviceInterfaceGuid = new("EA0BD5C3-50F3-4888-84B4-74E50E1649DB");

    /// <summary>Cartes présentes dont l'interface native est pilotée par WinUSB.</summary>
    public static IReadOnlyList<WinUsbDeviceInfo> Enumerate()
    {
        var devices = new List<WinUsbDeviceInfo>();
        Guid guid = DeviceInterfaceGuid;
        IntPtr set = NativeMethods.SetupDiGetClassDevs(guid, null, IntPtr.Zero,
            NativeMethods.DigcfPresent | NativeMethods.DigcfDeviceInterface);
        if (set == NativeMethods.InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetupDiGetClassDevs a échoué.");
        }

        try
        {
            for (uint index = 0; ; index++)
            {
                var data = new NativeMethods.SpDeviceInterfaceData
                {
                    CbSize = (uint)Marshal.SizeOf<NativeMethods.SpDeviceInterfaceData>(),
                };
                if (!NativeMethods.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, guid, index, ref data))
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error == NativeMethods.ErrorNoMoreItems)
                    {
                        break;
                    }
                    throw new Win32Exception(error, "SetupDiEnumDeviceInterfaces a échoué.");
                }

                if (GetDevicePath(set, ref data) is { } path)
                {
                    devices.Add(new WinUsbDeviceInfo(path));
                }
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(set);
        }
        return devices;
    }

    /// <summary>
    /// Identifiants d'instance des interfaces natives ODrive présentes sur le bus USB, quel que soit leur
    /// pilote. S'il y en a plus que de cartes renvoyées par <see cref="Enumerate"/>, un autre pilote que
    /// WinUSB a été forcé sur l'interface native (SPEC §1 : expliquer la procédure Zadig).
    /// </summary>
    public static IReadOnlyList<string> FindNativeInterfaceInstances()
    {
        string prefix = $@"USB\VID_{VendorId:X4}&PID_{ProductId:X4}&MI_{NativeInterfaceNumber:X2}\";
        var instances = new List<string>();
        IntPtr set = NativeMethods.SetupDiGetClassDevsAll(IntPtr.Zero, "USB", IntPtr.Zero,
            NativeMethods.DigcfPresent | NativeMethods.DigcfAllClasses);
        if (set == NativeMethods.InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "SetupDiGetClassDevs a échoué.");
        }

        try
        {
            char[] buffer = new char[512];
            for (uint index = 0; ; index++)
            {
                var info = new NativeMethods.SpDevinfoData
                {
                    CbSize = (uint)Marshal.SizeOf<NativeMethods.SpDevinfoData>(),
                };
                if (!NativeMethods.SetupDiEnumDeviceInfo(set, index, ref info))
                {
                    break;
                }

                if (TryGetInstanceId(set, ref info, buffer, out uint length) && length > 0)
                {
                    string id = new(buffer, 0, (int)length - 1);
                    if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        instances.Add(id);
                    }
                }
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(set);
        }
        return instances;
    }

    private static unsafe bool TryGetInstanceId(IntPtr set, ref NativeMethods.SpDevinfoData info, char[] buffer,
        out uint length)
    {
        fixed (char* id = buffer)
        {
            return NativeMethods.SetupDiGetDeviceInstanceId(set, ref info, id, (uint)buffer.Length, out length);
        }
    }

    private static string? GetDevicePath(IntPtr set, ref NativeMethods.SpDeviceInterfaceData data)
    {
        NativeMethods.SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out uint size, IntPtr.Zero);
        if (Marshal.GetLastPInvokeError() != NativeMethods.ErrorInsufficientBuffer || size == 0)
        {
            return null;
        }

        IntPtr detail = Marshal.AllocHGlobal((int)size);
        try
        {
            // SP_DEVICE_INTERFACE_DETAIL_DATA_W : cbSize (DWORD) puis le chemin en UTF-16. cbSize vaut la
            // taille de la structure déclarée avec un seul caractère : 8 en 64 bits (alignement), 6 en 32 bits.
            Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
            if (!NativeMethods.SetupDiGetDeviceInterfaceDetail(set, ref data, detail, size, out _, IntPtr.Zero))
            {
                return null;
            }
            return Marshal.PtrToStringUni(detail + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
        }
    }
}
