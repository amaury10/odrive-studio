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

    /// <summary>Place un texte dans le presse-papiers.</summary>
    void CopyText(string text);
}

public sealed class WpfUserDialogs : IUserDialogs
{
    public bool Confirm(string title, string message) => OnUi(() =>
        MessageBox.Show(Application.Current?.MainWindow!, message, title, MessageBoxButton.YesNo,
            MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes);

    public string? PickSaveFile(string title, string defaultFileName, string filter) => OnUi(() =>
    {
        var dialog = new SaveFileDialog { Title = title, FileName = defaultFileName, Filter = filter };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    });

    public string? PickOpenFile(string title, string filter) => OnUi(() =>
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dialog.ShowDialog(Application.Current?.MainWindow) == true ? dialog.FileName : null;
    });

    public void CopyText(string text) => OnUi(() =>
    {
        try
        {
            Clipboard.SetDataObject(text, copy: true);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Presse-papiers tenu par une autre application : la copie est perdue, pas l'outil.
        }
        return true;
    });

    // Les bibliothèques posent leurs questions depuis le thread où leur lecture USB s'est terminée
    // (ConfigureAwait(false), par exemple SafetyGuard.AuthorizeMotionAsync) : sur une vraie carte ce n'est pas
    // le thread de l'interface, et une fenêtre ne s'ouvre que depuis lui.
    private static T OnUi<T>(Func<T> show) =>
        Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess() ? dispatcher.Invoke(show) : show();
}
