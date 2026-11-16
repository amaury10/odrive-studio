using System.Windows;
using System.Windows.Input;
using ODriveStudio.App.ViewModels;

namespace ODriveStudio.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    // Arrêt d'urgence global (docs/SAFETY.md) : traité en « preview » pour passer avant tout contrôle qui
    // aurait le focus. L'événement n'est pas marqué traité : Échap garde aussi son effet habituel
    // (fermer une liste déroulante, annuler une saisie).
    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !e.IsRepeat && DataContext is MainViewModel main)
        {
            main.EmergencyStopCommand.Execute(null);
        }
    }
}
