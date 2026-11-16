using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ODrive.Protocol.Transport.WinUsb;

/// <summary>P/Invoke SetupAPI, kernel32 et winusb.dll. Aucune dépendance à libusb.</summary>
internal static partial class NativeMethods
{
    public const uint DigcfPresent = 0x02;
    public const uint DigcfAllClasses = 0x04;
    public const uint DigcfDeviceInterface = 0x10;

    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint FileShareRead = 0x01;
    public const uint FileShareWrite = 0x02;
    public const uint OpenExisting = 3;
    public const uint FileFlagOverlapped = 0x40000000; // exigé par WinUsb_Initialize

    public const int ErrorNoMoreItems = 259;
    public const int ErrorInsufficientBuffer = 122;
    public const int ErrorSemTimeout = 121;
    public const int ErrorOperationAborted = 995;

    public const uint PipeTransferTimeout = 0x03;

    public const int UsbdPipeTypeBulk = 2;

    public static readonly IntPtr InvalidHandleValue = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SpDevinfoData
    {
        public uint CbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct UsbInterfaceDescriptor
    {
        public byte Length;
        public byte DescriptorType;
        public byte InterfaceNumber;
        public byte AlternateSetting;
        public byte NumEndpoints;
        public byte InterfaceClass;
        public byte InterfaceSubClass;
        public byte InterfaceProtocol;
        public byte Interface;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WinUsbPipeInformation
    {
        public int PipeType;
        public byte PipeId;
        public ushort MaximumPacketSize;
        public byte Interval;
    }

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr SetupDiGetClassDevs(in Guid classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial IntPtr SetupDiGetClassDevsAll(IntPtr classGuid, string? enumerator, IntPtr hwndParent, uint flags);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        in Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
        ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, uint memberIndex, ref SpDevinfoData deviceInfoData);

    [LibraryImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInstanceIdW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData,
        char* deviceInstanceId, uint deviceInstanceIdSize, out uint requiredSize);

    [LibraryImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_Initialize(SafeFileHandle deviceHandle, out IntPtr interfaceHandle);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_Free(IntPtr interfaceHandle);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_QueryInterfaceSettings(IntPtr interfaceHandle, byte alternateSettingNumber,
        out UsbInterfaceDescriptor descriptor);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_QueryPipe(IntPtr interfaceHandle, byte alternateInterfaceNumber,
        byte pipeIndex, out WinUsbPipeInformation pipeInformation);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_SetPipePolicy(IntPtr interfaceHandle, byte pipeId, uint policyType,
        uint valueLength, in uint value);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool WinUsb_ReadPipe(IntPtr interfaceHandle, byte pipeId, byte* buffer,
        uint bufferLength, out uint lengthTransferred, IntPtr overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static unsafe partial bool WinUsb_WritePipe(IntPtr interfaceHandle, byte pipeId, byte* buffer,
        uint bufferLength, out uint lengthTransferred, IntPtr overlapped);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_AbortPipe(IntPtr interfaceHandle, byte pipeId);

    [LibraryImport("winusb.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WinUsb_FlushPipe(IntPtr interfaceHandle, byte pipeId);
}
