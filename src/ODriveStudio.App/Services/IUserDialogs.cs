using System.Windows;
using Microsoft.Win32;

namespace ODriveStudio.App.Services;

/// <summary>Interactions modales avec l'utilisateur, isolées des vues-modèles pour les tests.</summary>
public interface IUserDialogs
{
    bool Confirm(string title, string message);

    /// <summary>Chemin choisi pour un enregistrement, ou <c>null</c> si l'utilisateur annule.</summary>
    string? PickSaveFile(string title, string defaultFileName, string filter);

    /// <summary>Chemin du fichier à ouvrir, ou <c>null</c> si l'utilisateur annule.</summary>
    string? PickOpenFile(string title, string filter);
}

public sealed class WpfUserDialogs : IUserDialogs
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(Application.Current?.MainWindow!, message, title, MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;

    public string? PickSaveFile(string title, string defaultFileName, string filter)
    {
        var dialog = new SaveFileDialog { Title = title, FileName = defaultFileName, Filter = filter };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }

    public string? PickOpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    }
}
