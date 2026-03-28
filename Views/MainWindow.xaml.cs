#nullable enable
using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media.Animation;
using MahApps.Metro.Controls;
using RemotePlayServer.ViewModels;

namespace RemotePlayServer.Views;

public partial class MainWindow : MetroWindow
{
    public MainWindow()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is MainViewModel vm)
            vm.PropertyChanged += OnViewModelPropertyChanged;
        if (e.OldValue is MainViewModel oldVm)
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentView))
        {
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut }
            };
            ContentArea.BeginAnimation(OpacityProperty, fadeIn);
        }
    }

    private void MetroWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            Hide();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        TrayIcon.Dispose();
        base.OnClosing(e);
    }
}
