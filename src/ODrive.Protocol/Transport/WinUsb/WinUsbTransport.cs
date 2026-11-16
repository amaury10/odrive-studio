using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace ODrive.Protocol.Transport.WinUsb;

/// <summary>
/// Transport USB de l'interface native par WinUSB. Sur USB bulk il n'y a pas de tramage : un transfert = un
/// paquet, de 63 octets au plus (<c>ref/ODrive/Firmware/communication/interface_usb.cpp:65-74,149</c>).
/// Un fil dédié garde en permanence une lecture en attente sur l'endpoint IN et range les paquets reçus dans
/// une file : aucun transfert n'est annulé sur expiration du délai, donc aucune réponse n'est perdue.
/// </summary>
public sealed class WinUsbTransport : ITransport
{
    /// <summary>Endpoint bulk OUT de l'interface native (<c>.../CDC/Inc/usbd_cdc.h:44-48</c>).</summary>
    public const byte ExpectedOutEndpoint = 0x03;

    /// <summary>Endpoint bulk IN de l'interface native.</summary>
    public const byte ExpectedInEndpoint = 0x83;

    private const uint WriteTimeoutMs = 1000;
    private const int ReadBufferSize = 64;

    private readonly SafeFileHandle _file;
    private readonly IntPtr _winUsb;
    private readonly byte _outPipe;
    private readonly byte _inPipe;
    private readonly Channel<byte[]> _incoming = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleWriter = true });
    private readonly Thread _reader;
    private readonly Lock _writeGate = new();
    private int _connected = 1;
    private int _disposed;

    private WinUsbTransport(string devicePath, SafeFileHandle file, IntPtr winUsb, byte outPipe, byte inPipe)
    {
        Description = devicePath;
        _file = file;
        _winUsb = winUsb;
        _outPipe = outPipe;
        _inPipe = inPipe;
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "ODrive WinUSB RX" };
        _reader.Start();
    }

    public event EventHandler? Disconnected;

    public string Description { get; }
    public bool IsConnected => Volatile.Read(ref _connected) == 1;

    /// <summary>Ouvre l'interface native d'une carte énumérée par <see cref="WinUsbEnumerator.Enumerate"/>.</summary>
    public static WinUsbTransport Open(WinUsbDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);

        SafeFileHandle file = NativeMethods.CreateFile(device.DevicePath,
            NativeMethods.GenericRead | NativeMethods.GenericWrite,
            NativeMethods.FileShareRead | NativeMethods.FileShareWrite,
            IntPtr.Zero, NativeMethods.OpenExisting, NativeMethods.FileFlagOverlapped, IntPtr.Zero);
        if (file.IsInvalid)
        {
            int error = Marshal.GetLastPInvokeError();
            file.Dispose();
            throw new IOException(
                "Impossible d'ouvrir l'interface native de la carte (déjà ouverte par odrivetool ou une autre "
                + "instance ?) : " + new Win32Exception(error).Message);
        }

        IntPtr winUsb = IntPtr.Zero;
        try
        {
            if (!NativeMethods.WinUsb_Initialize(file, out winUsb))
            {
                throw new IOException("WinUsb_Initialize a échoué : "
                    + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
            }

            (byte outPipe, byte inPipe) = FindBulkPipes(winUsb);

            // Une écriture de 63 octets au plus aboutit en moins d'une milliseconde ; le délai ne sert qu'à ne
            // pas rester bloqué si la carte cesse d'accepter des données.
            NativeMethods.WinUsb_SetPipePolicy(winUsb, outPipe, NativeMethods.PipeTransferTimeout,
                sizeof(uint), WriteTimeoutMs);
            // Jette une éventuelle réponse restée dans l'endpoint IN depuis une session précédente.
            NativeMethods.WinUsb_FlushPipe(winUsb, inPipe);

            return new WinUsbTransport(device.DevicePath, file, winUsb, outPipe, inPipe);
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

    public unsafe ValueTask SendAsync(ReadOnlyMemory<byte> packet, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (packet.Length > PacketCodec.MaxPacketSize)
        {
            throw new ArgumentException($"Paquet de {packet.Length} octets : le maximum est {PacketCodec.MaxPacketSize}.",
                nameof(packet));
        }

        lock (_writeGate)
        {
            if (!IsConnected)
            {
                throw new ODriveConnectionLostException();
            }

            fixed (byte* data = packet.Span)
            {
                if (!NativeMethods.WinUsb_WritePipe(_winUsb, _outPipe, data, (uint)packet.Length,
                        out uint written, IntPtr.Zero) || written != packet.Length)
                {
                    int error = Marshal.GetLastPInvokeError();
                    MarkDisconnected();
                    throw new ODriveConnectionLostException(
                        "Écriture USB impossible : " + new Win32Exception(error).Message);
                }
            }
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, TimeSpan timeout, CancellationToken ct)
    {
        byte[] packet;
        if (!_incoming.Reader.TryRead(out packet!))
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutSource.CancelAfter(timeout);
            try
            {
                packet = await _incoming.Reader.ReadAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                return 0;
            }
            catch (ChannelClosedException)
            {
                throw new ODriveConnectionLostException();
            }
        }

        packet.CopyTo(buffer);
        return packet.Length;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return ValueTask.CompletedTask;
        }

        MarkDisconnected();
        // Débloque la lecture en attente, puis libère les handles une fois le fil de lecture sorti.
        NativeMethods.WinUsb_AbortPipe(_winUsb, _inPipe);
        if (Thread.CurrentThread != _reader)
        {
            _reader.Join(TimeSpan.FromSeconds(2));
        }
        lock (_writeGate)
        {
            NativeMethods.WinUsb_Free(_winUsb);
            _file.Dispose();
        }
        return ValueTask.CompletedTask;
    }

    private static (byte OutPipe, byte InPipe) FindBulkPipes(IntPtr winUsb)
    {
        if (!NativeMethods.WinUsb_QueryInterfaceSettings(winUsb, 0, out NativeMethods.UsbInterfaceDescriptor descriptor))
        {
            throw new IOException("WinUsb_QueryInterfaceSettings a échoué : "
                + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
        }

        byte? outPipe = null;
        byte? inPipe = null;
        for (byte index = 0; index < descriptor.NumEndpoints; index++)
        {
            if (NativeMethods.WinUsb_QueryPipe(winUsb, 0, index, out NativeMethods.WinUsbPipeInformation pipe)
                && pipe.PipeType == NativeMethods.UsbdPipeTypeBulk)
            {
                if ((pipe.PipeId & 0x80) != 0)
                {
                    inPipe ??= pipe.PipeId;
                }
                else
                {
                    outPipe ??= pipe.PipeId;
                }
            }
        }

        if (outPipe is null || inPipe is null)
        {
            throw new IOException(
                $"L'interface USB ouverte n'a pas les deux endpoints bulk attendus (0x{ExpectedOutEndpoint:X2} OUT, "
                + $"0x{ExpectedInEndpoint:X2} IN) : ce n'est pas l'interface native d'un ODrive.");
        }
        return (outPipe.Value, inPipe.Value);
    }

    private unsafe void ReadLoop()
    {
        byte* buffer = stackalloc byte[ReadBufferSize];
        while (IsConnected)
        {
            if (NativeMethods.WinUsb_ReadPipe(_winUsb, _inPipe, buffer, ReadBufferSize, out uint read, IntPtr.Zero))
            {
                if (read > 0)
                {
                    _incoming.Writer.TryWrite(new ReadOnlySpan<byte>(buffer, (int)read).ToArray());
                }
                continue;
            }

            int error = Marshal.GetLastPInvokeError();
            if (error == NativeMethods.ErrorSemTimeout)
            {
                continue;
            }

            // Carte débranchée ou redémarrée (ERROR_GEN_FAILURE, ERROR_DEVICE_NOT_CONNECTED, ERROR_BAD_COMMAND…),
            // ou lecture annulée par DisposeAsync (ERROR_OPERATION_ABORTED).
            break;
        }
        MarkDisconnected();
    }

    private void MarkDisconnected()
    {
        if (Interlocked.Exchange(ref _connected, 0) == 1)
        {
            _incoming.Writer.TryComplete();
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
    }
}
