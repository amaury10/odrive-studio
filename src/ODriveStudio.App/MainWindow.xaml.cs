using System.Windows;
using ODrive.Model;

namespace ODriveStudio.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StatusText.Text =
            $"ODrive Studio — firmware {ODriveInterface.FirmwareTag}\n" +
            $"{ODriveInterface.Properties.Count} propriétés, {ODriveInterface.Functions.Count} fonctions, " +
            $"{ODriveEnums.All.Count} enums connus.\nConnexion : phase 1 de la feuille de route.";
    }
}
