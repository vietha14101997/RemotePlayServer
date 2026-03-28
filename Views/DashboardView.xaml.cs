#nullable enable
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using RemotePlayServer.ViewModels;

namespace RemotePlayServer.Views;

public partial class DashboardView : UserControl
{
    private Storyboard? _pulseStoryboard;

    public DashboardView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is DashboardViewModel vm)
        {
            vm.State.PropertyChanged += OnStateChanged;
            UpdatePulse(vm.State.IsRunning);
        }
    }

    private void OnStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "IsRunning" && DataContext is DashboardViewModel vm)
            Dispatcher.Invoke(() => UpdatePulse(vm.State.IsRunning));
    }

    private void UpdatePulse(bool isRunning)
    {
        if (_pulseStoryboard == null)
        {
            _pulseStoryboard = (Storyboard)FindResource("PulseAnimation");
        }

        if (isRunning)
            _pulseStoryboard.Begin(RunDot, true);
        else
            _pulseStoryboard.Stop(RunDot);
    }
}
