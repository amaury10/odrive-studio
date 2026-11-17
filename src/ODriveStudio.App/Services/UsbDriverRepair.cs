using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using ODrive.Protocol.Transport.WinUsb;

namespace ODriveStudio.App.Services;

public enum UsbRepairOutcome
{
    /// <summary>Le GUID d'interface a été enregistré.</summary>
    Repaired,

    /// <summary>L'utilisateur a refusé l'élévation de droits (UAC).</summary>
    Declined,

    Failed,
}

/// <summary>
/// Correction du GUID d'interface WinUSB manquant (défaut du firmware, voir <see cref="UsbDriverDiagnosis"/>).
/// Isolée des vues-modèles pour les tests.
/// </summary>
public interface IUsbDriverRepair
{
    /// <summary>Vrai si une carte branchée n'a aucun pilote, ou WinUSB sans GUID d'interface.</summary>
    bool IsNeeded { get; }

    /// <summary>Applique la correction avec les droits administrateur, pour cette seule opération.</summary>
    Task<UsbRepairOutcome> RunElevatedAsync();
}

/// <summary>
/// L'application reste sans privilèges : seule l'écriture du registre est faite par une seconde instance,
/// lancée avec <c>--fix-usb</c> par l'invite UAC, qui se termine aussitôt (voir <c>App.OnStartup</c>).
/// </summary>
public sealed class ElevatedUsbDriverRepair : IUsbDriverRepair
{
    /// <summary>Option de ligne de commande de l'instance élevée.</summary>
    public const string Argument = "--fix-usb";

    private const int ErrorCancelled = 1223; // ERROR_CANCELLED : invite UAC refusée

    public bool IsNeeded
    {
        get
        {
            try
            {
                return WinUsbEnumerator.FindNativeInterfaces().Concat(WinUsbEnumerator.FindDfuDevices())
                    .Any(WinUsbEnumerator.NeedsRepair);
            }
            catch (Win32Exception)
            {
                return false;
            }
        }
    }

    public async Task<UsbRepairOutcome> RunElevatedAsync()
    {
        if (Environment.ProcessPath is not { } executable)
        {
            return UsbRepairOutcome.Failed;
        }

        try
        {
            using Process? process = Process.Start(new ProcessStartInfo(executable, Argument)
            {
                UseShellExecute = true, // exigé par le verbe « runas »
                Verb = "runas",
            });
            if (process is null)
            {
                return UsbRepairOutcome.Failed;
            }
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? UsbRepairOutcome.Repaired : UsbRepairOutcome.Failed;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            return UsbRepairOutcome.Declined;
        }
        catch (Win32Exception)
        {
            return UsbRepairOutcome.Failed;
        }
    }

    /// <summary>Corps de l'instance élevée. Code de sortie : 0 si au moins une interface a été corrigée.</summary>
    public static int RunAsElevatedInstance()
    {
        try
        {
            return WinUsbEnumerator.Repair().Count > 0 ? 0 : 2;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or Win32Exception)
        {
            return 1;
        }
    }
}
