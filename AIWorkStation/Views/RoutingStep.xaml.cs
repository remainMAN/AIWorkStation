using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using AIWorkStation.ViewModels;

namespace AIWorkStation.Views;

public partial class RoutingStep : UserControl
{
    private MainViewModel? _viewModel;
    private bool _syncingPassword;

    public RoutingStep()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_viewModel is not null) _viewModel.PropertyChanged -= ViewModelPropertyChanged;
        _viewModel = e.NewValue as MainViewModel;
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged += ViewModelPropertyChanged;
            SetPasswordBox(_viewModel.ProxyPassword);
        }
    }

    private void ViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.ProxyPassword) && _viewModel is not null)
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
            if (Dispatcher.CheckAccess()) SetPasswordBox(_viewModel.ProxyPassword);
            else _ = Dispatcher.BeginInvoke(() =>
            {
                if (!Dispatcher.HasShutdownStarted && _viewModel is not null)
                    SetPasswordBox(_viewModel.ProxyPassword);
            });
        }
    }

    private void SetPasswordBox(string value)
    {
        if (ProxyPasswordBox.Password == value) return;
        _syncingPassword = true;
        try { ProxyPasswordBox.Password = value; }
        finally { _syncingPassword = false; }
    }

    private void ProxyPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_syncingPassword && DataContext is MainViewModel viewModel && sender is PasswordBox box)
            viewModel.ProxyPassword = box.Password;
    }

    private void ColumnsGridSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var compact = e.NewSize.Width < 1080;
        Grid.SetRow(ApplicationCard, 0);
        Grid.SetColumn(ApplicationCard, 0);
        Grid.SetColumnSpan(ApplicationCard, compact ? 3 : 1);
        Grid.SetRow(ProxyCard, compact ? 1 : 0);
        Grid.SetColumn(ProxyCard, compact ? 0 : 2);
        Grid.SetColumnSpan(ProxyCard, compact ? 3 : 1);
        ProxyCard.Margin = compact ? new Thickness(0, 18, 0, 0) : new Thickness(0);
    }
}
