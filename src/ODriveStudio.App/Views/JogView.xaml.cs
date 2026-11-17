using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ODriveStudio.App.ViewModels;

namespace ODriveStudio.App.Views;

/// <summary>
/// Les boutons de jog sont « à maintenir » : appui = consigne, relâchement ou perte de la capture de la
/// souris = arrêt. Ce n'est pas exprimable par une commande, d'où ce code.
/// </summary>
public partial class JogView : UserControl
{
    private JogViewModel? _viewModel;
    private bool _syncing;

    public JogView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelChanged;
        }
        _viewModel = DataContext as JogViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += OnViewModelChanged;
            SyncSlider();
        }
    }

    // Le curseur suit la consigne (remise à zéro au relâchement, à l'arrêt, à l'abandon) et la limite.
    private void OnViewModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(JogViewModel.Command) or nameof(JogViewModel.MaxVelocity))
        {
            SyncSlider();
        }
    }

    private void SyncSlider()
    {
        if (_viewModel is null)
        {
            return;
        }
        _syncing = true;
        double limit = Math.Max(0.1, Math.Abs(_viewModel.MaxVelocity));
        Continuous.Minimum = -limit;
        Continuous.Maximum = limit;
        Continuous.TickFrequency = limit / 4;
        Continuous.Value = _viewModel.Command;
        _syncing = false;
    }

    private void OnContinuousChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_syncing && _viewModel is { IsRunning: true } viewModel)
        {
            _ = viewModel.SetCommandAsync((float)e.NewValue);
        }
    }

    private void OnForwardDown(object sender, MouseButtonEventArgs e) => Press(sender, +1);

    private void OnBackwardDown(object sender, MouseButtonEventArgs e) => Press(sender, -1);

    private void Press(object sender, int direction)
    {
        // Capture : le relâchement est reçu même si la souris a quitté le bouton.
        ((UIElement)sender).CaptureMouse();
        _ = _viewModel?.PressAsync(direction);
    }

    private void OnJogUp(object sender, MouseButtonEventArgs e) => ((UIElement)sender).ReleaseMouseCapture();

    private void OnJogLost(object sender, MouseEventArgs e) => _ = _viewModel?.ReleaseAsync();
}
