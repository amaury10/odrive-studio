using System.ComponentModel;
using System.Runtime.InteropServices;

namespace ODrive.Protocol.Transport.WinUsb;

/// <summary>
/// Associe le pilote WinUSB fourni avec Windows (<c>%windir%\INF\winusb.inf</c>, entrée générique
/// « WinUsb Device ») à un périphérique qui n'a aucun pilote. C'est l'équivalent programmé de :
/// Gestionnaire de périphériques &gt; Mettre à jour le pilote &gt; Choisir dans une liste &gt; Périphériques USB
/// &gt; Périphérique WinUsb. Aucun fichier INF tiers, donc aucune signature à fournir. Droits administrateur
/// requis.
/// <para>
/// Nécessaire avec le firmware 0.5.6 officiel : constaté sur une v3.6, l'interface native n'y reçoit pas
/// l'identifiant compatible <c>USB\MS_COMP_WINUSB</c> (échec d'installation, STATUS_PNP_NO_COMPAT_DRIVERS),
/// alors que la table de <c>usbd_cdc.c:823-845</c> le prévoit.
/// </para>
/// </summary>
public static partial class WinUsbDriverInstaller
{
    private const uint DriverTypeClass = 1;                 // SPDIT_CLASSDRIVER
    private const uint FlagEnumSingleInf = 0x0001_0000;     // DI_ENUMSINGLEINF
    private const uint FlagExAllowExcluded = 0x0000_0800;   // DI_FLAGSEX_ALLOWEXCLUDEDDRVS
    private const int ErrorAccessDenied = 5;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorInsufficientBuffer = 122;

    // Tailles et décalages des structures SetupAPI en 64 bits (setupapi.h, alignement sur 8).
    private const int InstallParamsSize = 584;      // SP_DEVINSTALL_PARAMS_W
    private const int InstallParamsFlags = 4;
    private const int InstallParamsFlagsEx = 8;
    private const int InstallParamsDriverPath = 60;
    private const int DriverInfoSize = 1568;        // SP_DRVINFO_DATA_V2_W
    private const int DriverDetailSize = 1584;      // SP_DRVINFO_DETAIL_DATA_W
    private const int DriverDetailSectionName = 32;
    private const int DriverDetailBuffer = 8192;
    private const string WinUsbSection = "WINUSB";  // section d'installation de l'entrée USB\MS_COMP_WINUSB

    /// <summary>Installe le pilote WinUSB de Windows sur le périphérique donné.</summary>
    /// <exception cref="UnauthorizedAccessException">Le processus n'est pas administrateur.</exception>
    /// <exception cref="IOException">Windows a refusé l'installation ; le message dit à quelle étape.</exception>
    public static void InstallInboxWinUsb(string instanceId)
    {
        ArgumentException.ThrowIfNullOrEmpty(instanceId);
        if (IntPtr.Size != 8)
        {
            throw new PlatformNotSupportedException("Installation du pilote : processus 64 bits requis.");
        }

        string inf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "INF", "winusb.inf");
        IntPtr set = SetupDiCreateDeviceInfoList(IntPtr.Zero, IntPtr.Zero);
        if (set == NativeMethods.InvalidHandleValue)
        {
            throw Failure("SetupDiCreateDeviceInfoList");
        }

        IntPtr parameters = Marshal.AllocHGlobal(InstallParamsSize);
        IntPtr driver = Marshal.AllocHGlobal(DriverInfoSize);
        IntPtr detail = Marshal.AllocHGlobal(DriverDetailBuffer);
        try
        {
            var device = new NativeMethods.SpDevinfoData { CbSize = (uint)Marshal.SizeOf<NativeMethods.SpDevinfoData>() };
            if (!SetupDiOpenDeviceInfo(set, instanceId, IntPtr.Zero, 0, ref device))
            {
                throw Failure("SetupDiOpenDeviceInfo");
            }

            // Liste des pilotes tirée du seul winusb.inf, comme « Disque fourni » du Gestionnaire.
            Clear(parameters, InstallParamsSize);
            Marshal.WriteInt32(parameters, InstallParamsSize);
            if (!SetupDiGetDeviceInstallParams(set, ref device, parameters))
            {
                throw Failure("SetupDiGetDeviceInstallParams");
            }
            Marshal.WriteInt32(parameters, InstallParamsFlags,
                Marshal.ReadInt32(parameters, InstallParamsFlags) | unchecked((int)FlagEnumSingleInf));
            Marshal.WriteInt32(parameters, InstallParamsFlagsEx,
                Marshal.ReadInt32(parameters, InstallParamsFlagsEx) | unchecked((int)FlagExAllowExcluded));
            char[] path = (inf + '\0').ToCharArray();
            Marshal.Copy(path, 0, parameters + InstallParamsDriverPath, path.Length);
            if (!SetupDiSetDeviceInstallParams(set, ref device, parameters))
            {
                throw Failure("SetupDiSetDeviceInstallParams");
            }
            if (!SetupDiBuildDriverInfoList(set, ref device, DriverTypeClass))
            {
                throw Failure("SetupDiBuildDriverInfoList");
            }

            for (uint index = 0; ; index++)
            {
                Clear(driver, DriverInfoSize);
                Marshal.WriteInt32(driver, DriverInfoSize);
                if (!SetupDiEnumDriverInfo(set, ref device, DriverTypeClass, index, driver))
                {
                    int error = Marshal.GetLastPInvokeError();
                    throw error == ErrorNoMoreItems
                        ? new IOException($"Entrée « WinUsb Device » introuvable dans {inf} ({index} pilote(s) listé(s)).")
                        : Failure("SetupDiEnumDriverInfo", error);
                }

                Clear(detail, DriverDetailBuffer);
                Marshal.WriteInt32(detail, DriverDetailSize);
                if (!SetupDiGetDriverInfoDetail(set, ref device, driver, detail, DriverDetailBuffer, out _)
                    && Marshal.GetLastPInvokeError() != ErrorInsufficientBuffer)
                {
                    continue;
                }
                string? section = Marshal.PtrToStringUni(detail + DriverDetailSectionName);
                if (string.Equals(section, WinUsbSection, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }
            }

            if (!DiInstallDevice(IntPtr.Zero, set, ref device, driver, 0, out _))
            {
                throw Failure("DiInstallDevice");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(detail);
            Marshal.FreeHGlobal(driver);
            Marshal.FreeHGlobal(parameters);
            NativeMethods.SetupDiDestroyDeviceInfoList(set);
        }
    }

    private static unsafe void Clear(IntPtr buffer, int size) => new Span<byte>((void*)buffer, size).Clear();

    private static Exception Failure(string step) => Failure(step, Marshal.GetLastPInvokeError());

    private static Exception Failure(string step, int error) => error == ErrorAccessDenied
        ? new UnauthorizedAccessException($"Droits administrateur requis pour installer le pilote WinUSB (refus à l'étape {step}).")
        : new IOException($"Installation du pilote WinUSB : {step} a échoué (0x{error:X8} : {new Win32Exception(error).Message}).");

    [LibraryImport("setupapi.dll", SetLastError = true)]
    private static partial IntPtr SetupDiCreateDeviceInfoList(IntPtr classGuid, IntPtr hwndParent);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiOpenDeviceInfoW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiOpenDeviceInfo(IntPtr deviceInfoSet, string deviceInstanceId, IntPtr hwndParent,
        uint openFlags, ref NativeMethods.SpDevinfoData deviceInfoData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstallParamsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDeviceInstallParams(IntPtr deviceInfoSet,
        ref NativeMethods.SpDevinfoData deviceInfoData, IntPtr deviceInstallParams);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiSetDeviceInstallParamsW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiSetDeviceInstallParams(IntPtr deviceInfoSet,
        ref NativeMethods.SpDevinfoData deviceInfoData, IntPtr deviceInstallParams);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiBuildDriverInfoList(IntPtr deviceInfoSet,
        ref NativeMethods.SpDevinfoData deviceInfoData, uint driverType);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiEnumDriverInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiEnumDriverInfo(IntPtr deviceInfoSet, ref NativeMethods.SpDevinfoData deviceInfoData,
        uint driverType, uint memberIndex, IntPtr driverInfoData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDriverInfoDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetupDiGetDriverInfoDetail(IntPtr deviceInfoSet,
        ref NativeMethods.SpDevinfoData deviceInfoData, IntPtr driverInfoData, IntPtr driverInfoDetailData,
        uint driverInfoDetailDataSize, out uint requiredSize);

    [LibraryImport("newdev.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DiInstallDevice(IntPtr hwndParent, IntPtr deviceInfoSet,
        ref NativeMethods.SpDevinfoData deviceInfoData, IntPtr driverInfoData, uint flags,
        [MarshalAs(UnmanagedType.Bool)] out bool needReboot);
}
