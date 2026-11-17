using System.ComponentModel;
using ODrive.Protocol.Transport.WinUsb;

namespace ODrive.Protocol.Dfu;

/// <summary>Ce que l'on sait des cartes en mode DFU sur le bus.</summary>
/// <param name="Present">Au moins un bootloader STM32 est sur le bus.</param>
/// <param name="Usable">Il peut être ouvert.</param>
/// <param name="Hint">Si présent mais inutilisable : explication pour l'utilisateur (pilote).</param>
public sealed record DfuPresence(bool Present, bool Usable, string? Hint);

/// <summary>Accès au bootloader d'une carte en mode DFU (USB réel ou simulé).</summary>
public interface IDfuDeviceSource
{
    DfuPresence Probe();

    /// <summary>Ouvre le bootloader ; à libérer après chaque opération.</summary>
    /// <exception cref="IOException">Aucun bootloader utilisable.</exception>
    DfuSeDevice Open();
}

/// <summary>Bootloader STM32 par WinUSB.</summary>
public sealed class WinUsbDfuSource : IDfuDeviceSource
{
    public DfuPresence Probe()
    {
        try
        {
            IReadOnlyList<NativeInterfaceStatus> present = WinUsbEnumerator.FindDfuDevices();
            int usable = WinUsbEnumerator.EnumerateDfu().Count;
            return new DfuPresence(present.Count > 0, usable > 0, UsbDriverDiagnosis.ExplainDfu(present, usable));
        }
        catch (Win32Exception)
        {
            return new DfuPresence(false, false, null);
        }
    }

    public DfuSeDevice Open()
    {
        IReadOnlyList<WinUsbDeviceInfo> devices = WinUsbEnumerator.EnumerateDfu();
        if (devices.Count == 0)
        {
            throw new IOException("Aucune carte en mode DFU utilisable.");
        }
        WinUsbDfuTransport transport = WinUsbDfuTransport.Open(devices[0]);
        try
        {
            return new DfuSeDevice(transport);
        }
        catch
        {
            transport.Dispose();
            throw;
        }
    }
}

/// <summary>Bootloader simulé, pour les tests et l'application lancée avec <c>--sim</c>.</summary>
public sealed class SimulatedDfuSource(SimulatedDfuDevice device) : IDfuDeviceSource
{
    public SimulatedDfuDevice Device { get; } = device;

    /// <summary>Vrai quand la carte simulée est dans son bootloader.</summary>
    public bool IsPresent { get; set; }

    /// <summary>Non nul pour simuler un bootloader présent mais sans pilote utilisable.</summary>
    public string? Hint { get; set; }

    public DfuPresence Probe() => new(IsPresent, IsPresent && Hint is null, IsPresent ? Hint : null);

    public DfuSeDevice Open() => IsPresent && Hint is null
        ? new DfuSeDevice(Device)
        : throw new IOException("Aucune carte en mode DFU utilisable.");
}

/// <summary>Plusieurs sources (USB réel et bootloader simulé) : la première utilisable sert.</summary>
public sealed class CompositeDfuSource(IReadOnlyList<IDfuDeviceSource> sources) : IDfuDeviceSource
{
    public DfuPresence Probe()
    {
        DfuPresence[] all = [.. sources.Select(source => source.Probe())];
        return all.FirstOrDefault(p => p.Usable) ?? all.FirstOrDefault(p => p.Present) ?? new DfuPresence(false, false, null);
    }

    public DfuSeDevice Open()
    {
        foreach (IDfuDeviceSource source in sources)
        {
            if (source.Probe().Usable)
            {
                return source.Open();
            }
        }
        throw new IOException("Aucune carte en mode DFU utilisable.");
    }
}
