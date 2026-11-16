using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using ODrive.Model;
using ODrive.Protocol;
using ODrive.Protocol.Simulation;
using ODrive.Protocol.Transport.WinUsb;
using ODriveStudio.App.Services;
using ODriveStudio.App.ViewModels;

namespace ODriveStudio.App;

/// <summary>
/// Point d'entrée WPF. Option de ligne de commande <c>--sim</c> : ajoute une carte simulée (développement
/// sans matériel).
/// </summary>
#pragma warning disable CA1001 // _manager est libéré dans OnExit : Application n'est pas IDisposable.
public partial class App : Application
#pragma warning restore CA1001
{
    private DeviceManager? _manager;
    private MainViewModel? _main;
    private DispatcherTimer? _hintTimer;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var journal = new WriteJournal();
        var sources = new List<ITransportSource> { new WinUsbTransportSource() };
        if (e.Args.Contains("--sim"))
        {
            sources.Add(new SimulatedTransportSource(SimulatedODrive.CreateReference()));
        }

        _manager = new DeviceManager(sources, new ODriveDeviceOptions { Journal = journal });
        _main = new MainViewModel(_manager, journal, new SafetyGuard(), new WpfUserDialogs(), DriverHint);

        // Une erreur imprévue dans une commande ne doit pas fermer l'outil pendant qu'un moteur tourne :
        // l'arrêt d'urgence doit rester disponible.
        DispatcherUnhandledException += (_, args) =>
        {
            _main.StatusMessage = "Erreur inattendue : " + args.Exception.Message;
            args.Handled = true;
        };

        MainWindow = new MainWindow { DataContext = _main };
        MainWindow.Show();

        _manager.Start(TimeSpan.FromSeconds(1));
        _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _hintTimer.Tick += (_, _) => _main.UpdateConnectionHint();
        _hintTimer.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hintTimer?.Stop();
        _manager?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        base.OnExit(e);
    }

    // SPEC §1 : carte visible par son VID/PID mais pas par son GUID d'interface → un autre pilote que WinUSB
    // a été forcé sur l'interface native.
    private static string? DriverHint()
    {
        try
        {
            int present = WinUsbEnumerator.FindNativeInterfaceInstances().Count;
            int usable = WinUsbEnumerator.Enumerate().Count;
            return present > usable
                ? "Une carte ODrive est branchée, mais son interface native n'est pas pilotée par WinUSB : un autre "
                  + "pilote a été forcé (libusb-win32, libusbK…).\nAvec Zadig : Options > List All Devices, choisir "
                  + "« ODrive 3.6 Native Interface (Interface 2) », sélectionner WinUSB puis Replace Driver."
                : null;
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}
