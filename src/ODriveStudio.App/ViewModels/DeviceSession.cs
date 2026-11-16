using ODrive.Model;
using ODrive.Protocol;
using ODriveStudio.App.Services;

namespace ODriveStudio.App.ViewModels;

/// <summary>Ce dont les vues d'une carte ont besoin : la carte, le garde-fou, les dialogues et la barre d'état.</summary>
public sealed class DeviceSession(ODriveDevice device, SafetyGuard guard, IUserDialogs dialogs, Action<string> reportStatus)
{
    public ODriveDevice Device { get; } = device;
    public ODrive3 Odrv { get; } = new(device);
    public SafetyGuard Guard { get; } = guard;
    public IUserDialogs Dialogs { get; } = dialogs;

    public void Status(string message) => reportStatus(message);

    /// <summary>
    /// Exécute une action utilisateur sur la carte ; une perte de liaison ou une réponse inattendue va dans la
    /// barre d'état au lieu de faire tomber l'application.
    /// </summary>
    public async Task<bool> RunAsync(string what, Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (ODriveConnectionLostException)
        {
            Status($"{what} : liaison avec la carte perdue.");
        }
        catch (ODriveProtocolException e)
        {
            Status($"{what} : {e.Message}");
        }
        return false;
    }

    /// <summary>Écrit une propriété, en passant par le garde-fou si l'écriture peut mettre un moteur en mouvement.</summary>
    /// <returns>Faux si le garde-fou a refusé (la raison est dans la barre d'état).</returns>
    public async Task<bool> WriteGuardedAsync(PropertyNode property, object value)
    {
        if (SafetyGuard.IsMotionRequest(property.Path, value))
        {
            SafetyDecision decision = await Guard.AuthorizeMotionAsync(Device,
                question => Dialogs.Confirm("Mise en mouvement", question));
            if (!decision.IsAllowed)
            {
                Status(decision.Reason ?? "Action refusée.");
                return false;
            }
        }

        await Device.WriteAsync(property, value);
        return true;
    }
}
