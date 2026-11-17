using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using ODrive.Protocol.Dfu;

namespace ODrive.Protocol.Transport.WinUsb;

/// <summary>
/// Bootloader ROM du STM32 (« STM32 BOOTLOADER », VID 0483, PID DF11) par WinUSB : transferts de contrôle
/// de classe DFU sur l'interface 0. Aucun endpoint autre que le contrôle.
/// </summary>
public sealed class WinUsbDfuTransport : IDfuTransport
{
    private const byte RequestTypeOut = 0x21; // classe, interface, vers la carte (DfuDevice.py:9)
    private const byte RequestTypeIn = 0xA1;  // classe, interface, depuis la carte (DfuDevice.py:10)
    private const byte DescriptorDevice = 1;
    private const byte DescriptorString = 3;
    private const ushort LanguageEnglish = 0x0409;

    private readonly SafeFileHandle _file;
    private readonly IntPtr _winUsb;
    private readonly byte _interfaceNumber;
    private bool _disposed;

    private WinUsbDfuTransport(SafeFileHandle file, IntPtr winUsb, byte interfaceNumber, string serialNumber,
        IReadOnlyList<string> alternateNames)
    {
        _file = file;
        _winUsb = winUsb;
        _interfaceNumber = interfaceNumber;
        SerialNumber = serialNumber;
        AlternateNames = alternateNames;
    }

    public string SerialNumber { get; }

    public IReadOnlyList<string> AlternateNames { get; }

    /// <summary>Ouvre un bootloader énuméré par <see cref="WinUsbEnumerator.EnumerateDfu"/>.</summary>
    /// <exception cref="IOException">Périphérique déjà ouvert ailleurs, disparu, ou pilote inadapté.</exception>
    public static WinUsbDfuTransport Open(WinUsbDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        SafeFileHandle file = NativeMethods.CreateFile(device.DevicePath,
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite, IntPtr.Zero,
            NativeMethods.OpenExisting, NativeMethods.FileFlagOverlapped, IntPtr.Zero);
        if (file.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            file.Dispose();
            throw new IOException("Ouverture du bootloader impossible (déjà ouvert par un autre outil ?) : "
                + new Win32Exception(error).Message);
        }

        IntPtr winUsb = IntPtr.Zero;
        try
        {
            if (!NativeMethods.WinUsb_Initialize(file, out winUsb))
            {
                throw LastError("WinUsb_Initialize");
            }

            var names = new List<string>();
            byte interfaceNumber = 0;
            for (byte alternate = 0;
                 NativeMethods.WinUsb_QueryInterfaceSettings(winUsb, alternate, out NativeMethods.UsbInterfaceDescriptor descriptor);
                 alternate++)
            {
                interfaceNumber = descriptor.InterfaceNumber;
                names.Add(descriptor.Interface == 0 ? "" : ReadString(winUsb, descriptor.Interface));
            }

            Span<byte> deviceDescriptor = stackalloc byte[18];
            string serial = GetDescriptor(winUsb, DescriptorDevice, 0, 0, deviceDescriptor) >= 17 && deviceDescriptor[16] != 0
                ? ReadString(winUsb, deviceDescriptor[16])
                : "";
            return new WinUsbDfuTransport(file, winUsb, interfaceNumber, serial, names);
        }
        catch
        {
            if (winUsb != IntPtr.Zero)
            {
                NativeMethods.WinUsb_Free(winUsb);
            }
            file.Dispose();
            throw;
        }
    }

    public void SetAlternate(int alternate)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!NativeMethods.WinUsb_SetCurrentAlternateSetting(_winUsb, (byte)alternate))
        {
            throw LastError("WinUsb_SetCurrentAlternateSetting");
        }
    }

    public unsafe void ControlOut(byte request, ushort value, ReadOnlySpan<byte> data)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var setup = new NativeMethods.WinUsbSetupPacket
        {
            RequestType = RequestTypeOut,
            Request = request,
            Value = value,
            Index = _interfaceNumber,
            Length = (ushort)data.Length,
        };
        fixed (byte* buffer = data)
        {
            if (!NativeMethods.WinUsb_ControlTransfer(_winUsb, setup, buffer, (uint)data.Length, out _, IntPtr.Zero))
            {
                throw LastError($"requête DFU {request}");
            }
        }
    }

    public unsafe int ControlIn(byte request, ushort value, Span<byte> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var setup = new NativeMethods.WinUsbSetupPacket
        {
            RequestType = RequestTypeIn,
            Request = request,
            Value = value,
            Index = _interfaceNumber,
            Length = (ushort)buffer.Length,
        };
        fixed (byte* data = buffer)
        {
            if (!NativeMethods.WinUsb_ControlTransfer(_winUsb, setup, data, (uint)buffer.Length, out uint read, IntPtr.Zero))
            {
                throw LastError($"requête DFU {request}");
            }
            return (int)read;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        NativeMethods.WinUsb_Free(_winUsb);
        _file.Dispose();
    }

    private static IOException LastError(string what) =>
        new($"{what} a échoué : {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");

    private static unsafe int GetDescriptor(IntPtr winUsb, byte type, byte index, ushort language, Span<byte> buffer)
    {
        fixed (byte* data = buffer)
        {
            return NativeMethods.WinUsb_GetDescriptor(winUsb, type, index, language, data, (uint)buffer.Length, out uint read)
                ? (int)read
                : 0;
        }
    }

    private static string ReadString(IntPtr winUsb, byte index)
    {
        Span<byte> buffer = stackalloc byte[255];
        int read = GetDescriptor(winUsb, DescriptorString, index, LanguageEnglish, buffer);
        // bLength, bDescriptorType, puis le texte en UTF-16LE.
        return read > 2 ? Encoding.Unicode.GetString(buffer[2..Math.Min(read, buffer[0])]) : "";
    }
}
