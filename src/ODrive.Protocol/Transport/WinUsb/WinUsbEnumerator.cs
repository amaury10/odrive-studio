using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace ODrive.Protocol.Transport.WinUsb;

/// <summary>Interface native d'une carte ODrive, telle que Windows l'énumère.</summary>
/// <param name="DevicePath">Chemin d'interface à passer à <see cref="WinUsbTransport.Open"/>.</param>
public sealed record WinUsbDeviceInfo(string DevicePath);

/// <summary>Énumération des cartes ODrive par leurs GUID d'interface (SetupAPI + registre).</summary>
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
    /// ⚠ En pratique Windows ne le reçoit pas pour l'interface 2 (voir <see cref="UsbDriverDiagnosis"/>) :
    /// il n'est publié que s'il a été enregistré par <see cref="RegisterInterfaceGuid"/>.
    /// </summary>
    public static readonly Guid DeviceInterfaceGuid = new("EA0BD5C3-50F3-4888-84B4-74E50E1649DB");

    /// <summary>
    /// Cartes présentes dont l'interface native peut être ouverte par WinUSB. La recherche porte sur le GUID
    /// du firmware et sur tous les GUID enregistrés pour les interfaces natives présentes : un pilote WinUSB
    /// installé par Zadig publie l'interface sous un GUID qui lui est propre.
    /// </summary>
    public static IReadOnlyList<WinUsbDeviceInfo> Enumerate()
    {
        var guids = new List<Guid> { DeviceInterfaceGuid };
        foreach (NativeInterfaceStatus status in FindNativeInterfaces())
        {
            foreach (Guid guid in status.InterfaceGuids)
            {
                if (!guids.Contains(guid))
                {
                    guids.Add(guid);
                }
            }
        }

        var devices = new List<WinUsbDeviceInfo>();
        foreach (Guid guid in guids)
        {
            foreach (string path in EnumerateInterfacePaths(guid))
            {
                if (UsbDriverDiagnosis.IsNativeInterfacePath(path)
                    && !devices.Exists(d => string.Equals(d.DevicePath, path, StringComparison.OrdinalIgnoreCase)))
                {
                    devices.Add(new WinUsbDeviceInfo(path));
                }
            }
        }
        return devices;
    }

    private static List<string> EnumerateInterfacePaths(Guid guid)
    {
        var paths = new List<string>();
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
                    paths.Add(path);
                }
            }
        }
        finally
        {
            NativeMethods.SetupDiDestroyDeviceInfoList(set);
        }
        return paths;
    }

    /// <summary>
    /// Interfaces natives ODrive présentes sur le bus USB, quel que soit leur pilote, avec ce que Windows en
    /// sait (pilote, GUID d'interface). Sert à <see cref="UsbDriverDiagnosis.Explain"/>.
    /// </summary>
    public static IReadOnlyList<NativeInterfaceStatus> FindNativeInterfaces()
    {
        var statuses = new List<NativeInterfaceStatus>();
        foreach (string instanceId in FindNativeInterfaceInstances())
        {
            using RegistryKey? device = Registry.LocalMachine.OpenSubKey(EnumKeyPath(instanceId));
            using RegistryKey? parameters = device?.OpenSubKey("Device Parameters");
            var guids = new List<Guid>(UsbDriverDiagnosis.ParseGuids(parameters?.GetValue("DeviceInterfaceGUIDs")));
            foreach (Guid guid in UsbDriverDiagnosis.ParseGuids(parameters?.GetValue("DeviceInterfaceGUID")))
            {
                if (!guids.Contains(guid))
                {
                    guids.Add(guid);
                }
            }
            statuses.Add(new NativeInterfaceStatus(instanceId, device?.GetValue("Service") as string, guids));
        }
        return statuses;
    }

    /// <summary>
    /// Enregistre <see cref="DeviceInterfaceGuid"/> pour une interface native pilotée par WinUSB mais sans
    /// GUID (défaut du firmware, voir <see cref="UsbDriverDiagnosis"/>). Demande les droits administrateur.
    /// WinUSB ne publie le GUID qu'au démarrage suivant du périphérique (rebranchement ou
    /// <c>pnputil /restart-device</c>).
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">Le processus n'est pas administrateur.</exception>
    public static void RegisterInterfaceGuid(string instanceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        try
        {
            using RegistryKey parameters = Registry.LocalMachine.OpenSubKey(
                EnumKeyPath(instanceId) + @"\Device Parameters", writable: true)
                ?? throw new IOException($"Clé de registre introuvable pour {instanceId}.");
            parameters.SetValue("DeviceInterfaceGUIDs",
                new[] { DeviceInterfaceGuid.ToString("B").ToUpperInvariant() }, RegistryValueKind.MultiString);
        }
        catch (System.Security.SecurityException e)
        {
            throw new UnauthorizedAccessException("Droits administrateur requis.", e);
        }
    }

    /// <summary>Vrai si l'interface est pilotée par WinUSB mais n'a aucun GUID d'interface.</summary>
    public static bool NeedsInterfaceGuid(NativeInterfaceStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return status.IsWinUsb && status.InterfaceGuids.Count == 0;
    }

    /// <summary>
    /// Enregistre le GUID de toutes les interfaces natives présentes qui en manquent, puis redémarre chaque
    /// périphérique (<c>pnputil /restart-device</c>) pour que WinUSB le publie. Droits administrateur requis.
    /// </summary>
    /// <returns>Identifiants d'instance corrigés.</returns>
    /// <exception cref="UnauthorizedAccessException">Le processus n'est pas administrateur.</exception>
    public static IReadOnlyList<string> RepairMissingInterfaceGuids()
    {
        var repaired = new List<string>();
        foreach (NativeInterfaceStatus status in FindNativeInterfaces())
        {
            if (!NeedsInterfaceGuid(status))
            {
                continue;
            }

            RegisterInterfaceGuid(status.InstanceId);
            repaired.Add(status.InstanceId);

            var restart = new ProcessStartInfo("pnputil.exe") { UseShellExecute = false, CreateNoWindow = true };
            restart.ArgumentList.Add("/restart-device");
            restart.ArgumentList.Add(status.InstanceId);
            using Process? process = Process.Start(restart);
            process?.WaitForExit(10_000);
        }
        return repaired;
    }

    private static string EnumKeyPath(string instanceId) => @"SYSTEM\CurrentControlSet\Enum\" + instanceId;

    private static List<string> FindNativeInterfaceInstances()
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
