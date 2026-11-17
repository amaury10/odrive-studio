namespace ODrive.Protocol.Transport.WinUsb;

/// <summary>État, vu par Windows, de l'interface native d'une carte ODrive présente sur le bus USB.</summary>
/// <param name="InstanceId">Identifiant d'instance (<c>USB\VID_1209&amp;PID_0D32&amp;MI_02\…</c>).</param>
/// <param name="Service">Pilote chargé (<c>WINUSB</c>, <c>libusb0</c>…), <c>null</c> s'il n'y en a pas.</param>
/// <param name="InterfaceGuids">GUID d'interface enregistrés pour cette instance (souvent aucun, voir
/// <see cref="UsbDriverDiagnosis"/>).</param>
public sealed record NativeInterfaceStatus(string InstanceId, string? Service, IReadOnlyList<Guid> InterfaceGuids)
{
    /// <summary>Vrai si le pilote de l'interface est WinUSB.</summary>
    public bool IsWinUsb => string.Equals(Service, "WINUSB", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Explique pourquoi une carte branchée n'est pas utilisable.
/// <para>
/// Défaut du firmware 0.5.6 : le compat ID <c>WINUSB</c> est bien fourni pour l'interface 2
/// (<c>ref/ODrive/Firmware/ThirdParty/STM32_USB_Device_Library/Class/CDC/Src/usbd_cdc.c:823-845</c>), donc
/// Windows charge WinUSB tout seul ; mais la requête « extended properties », qui porte le
/// <c>DeviceInterfaceGUID</c>, n'est acceptée que pour l'interface 0 et refusée pour l'interface 2
/// (<c>usbd_cdc.c:940-951</c>). Windows n'enregistre alors aucun GUID d'interface, et sans GUID il n'existe
/// aucun chemin à ouvrir. Relevé sur une v3.6 : <c>Service = WINUSB</c>, <c>DeviceInterfaceGUIDs</c> absent.
/// </para>
/// </summary>
public static class UsbDriverDiagnosis
{
    private const string NativeInterfacePathMarker = "vid_1209&pid_0d32&mi_02";

    /// <summary>Message pour l'utilisateur, ou <c>null</c> si toutes les cartes présentes sont utilisables.</summary>
    /// <param name="present">Interfaces natives présentes sur le bus.</param>
    /// <param name="usableCount">Nombre de cartes que l'énumération WinUSB sait ouvrir.</param>
    public static string? Explain(IReadOnlyList<NativeInterfaceStatus> present, int usableCount)
    {
        ArgumentNullException.ThrowIfNull(present);
        if (present.Count <= usableCount)
        {
            return null;
        }

        if (present.Any(status => !status.IsWinUsb))
        {
            return "Une carte ODrive est branchée, mais son interface native n'est pas pilotée par WinUSB "
                + "(pilote absent, ou autre pilote : libusb-win32, libusbK…).\nAvec Zadig : Options > List All "
                + "Devices, choisir « ODrive 3.6 Native Interface (Interface 2) », sélectionner WinUSB puis "
                + "Replace Driver.";
        }

        if (present.Any(status => status.InterfaceGuids.Count == 0))
        {
            return "Une carte ODrive est branchée et son pilote WinUSB est chargé, mais Windows n'a enregistré "
                + "aucun GUID d'interface pour elle (défaut du firmware 0.5.6) : elle ne peut pas être ouverte.\n"
                + "Correction, une fois par carte et par port USB : bouton « Corriger » d'ODrive Studio, ou "
                + "« odrive-cli fix-usb » dans un terminal administrateur.";
        }

        return "Une carte ODrive est branchée et son GUID d'interface est enregistré, mais Windows ne le publie "
            + "pas encore : débrancher puis rebrancher le câble USB de la carte.";
    }

    /// <summary>
    /// Lit la valeur de registre <c>DeviceInterfaceGUIDs</c> (REG_MULTI_SZ) ou <c>DeviceInterfaceGUID</c>
    /// (REG_SZ). Toute entrée illisible est ignorée.
    /// </summary>
    public static IReadOnlyList<Guid> ParseGuids(object? registryValue)
    {
        IEnumerable<string> texts = registryValue switch
        {
            string single => [single],
            string[] several => several,
            _ => [],
        };

        var guids = new List<Guid>();
        foreach (string text in texts)
        {
            if (Guid.TryParse(text, out Guid guid) && !guids.Contains(guid))
            {
                guids.Add(guid);
            }
        }
        return guids;
    }

    /// <summary>
    /// Vrai si le chemin d'interface désigne l'interface native d'une carte ODrive. Un GUID installé par un
    /// tiers (Zadig) peut être partagé avec d'autres périphériques : c'est le chemin qui fait foi.
    /// </summary>
    public static bool IsNativeInterfacePath(string devicePath)
    {
        ArgumentNullException.ThrowIfNull(devicePath);
        return devicePath.Contains(NativeInterfacePathMarker, StringComparison.OrdinalIgnoreCase);
    }
}
