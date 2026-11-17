using System.ComponentModel;
using System.Windows;
using System.Windows.Threading;
using ODrive.Model;
using ODrive.Protocol;
using ODrive.Protocol.Dfu;
using ODrive.Protocol.Simulation;
using ODrive.Protocol.Transport.WinUsb;
using ODriveStudio.App.Services;
using ODriveStudio.App.ViewModels;

namespace ODriveStudio.App;

/// <summary>
/// Point d'entrée WPF. Options de ligne de commande : <c>--sim</c> ajoute une carte simulée (développement
/// sans matériel) ; <c>--fix-usb</c> est réservée à l'instance élevée qui corrige le pilote USB.
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

        // Instance élevée lancée par le bouton « Corriger » : écrit le registre et se termine, sans fenêtre.
        if (e.Args.Contains(ElevatedUsbDriverRepair.Argument))
        {
            Shutdown(ElevatedUsbDriverRepair.RunAsElevatedInstance());
            return;
        }

        var journal = new WriteJournal();
        var sources = new List<ITransportSource> { new WinUsbTransportSource() };
        IDfuDeviceSource dfuSource = new WinUsbDfuSource();
        if (e.Args.Contains("--sim"))
        {
            dfuSource = new CompositeDfuSource([dfuSource, AddSimulatedBoard(sources)]);
        }

        _manager = new DeviceManager(sources, new ODriveDeviceOptions { Journal = journal });
        _main = new MainViewModel(_manager, journal, new SafetyGuard(), new WpfUserDialogs(), DriverHint,
            new ElevatedUsbDriverRepair(), dfuSource);

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
        _hintTimer.Tick += (_, _) =>
        {
            _main.UpdateConnectionHint();
            _main.Firmware.Refresh();
        };
        _hintTimer.Start();
    }

    // Carte simulée complète : en mode normal, puis dans son bootloader après enter_dfu_mode(), puis de
    // nouveau en mode normal quand le bootloader démarre l'application.
    private static SimulatedDfuSource AddSimulatedBoard(List<ITransportSource> sources)
    {
        SimulatedODrive board = SimulatedODrive.CreateReference();
        var normal = new SimulatedTransportSource(board);
        sources.Add(normal);

        var bootloader = new SimulatedDfuDevice();
        // Faux firmware : une table de vecteurs plausible suivie d'un motif.
        Span<byte> flash = bootloader.Flash.AsSpan(0, 200 * 1024);
        for (int i = 0; i < flash.Length; i++)
        {
            flash[i] = (byte)(i * 31);
        }
        BitConverter.TryWriteBytes(flash, 0x2002_0000u);
        BitConverter.TryWriteBytes(flash[4..], 0x0800_0101u);

        var dfu = new SimulatedDfuSource(bootloader);
        board.OnCall("enter_dfu_mode", _ =>
        {
            normal.IsPresent = false;
            bootloader.Reenter();
            dfu.IsPresent = true;
            board.Reboot();
        });
        bootloader.Left += (_, _) =>
        {
            dfu.IsPresent = false;
            normal.IsPresent = true;
        };
        return dfu;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hintTimer?.Stop();
        _manager?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        base.OnExit(e);
    }

    // SPEC §1 : carte visible sur le bus mais impossible à ouvrir → expliquer pourquoi (GUID d'interface
    // absent, ou autre pilote que WinUSB).
    private static string? DriverHint()
    {
        try
        {
            return UsbDriverDiagnosis.Explain(WinUsbEnumerator.FindNativeInterfaces(),
                WinUsbEnumerator.Enumerate().Count);
        }
        catch (Win32Exception)
        {
            return null;
        }
    }
}
