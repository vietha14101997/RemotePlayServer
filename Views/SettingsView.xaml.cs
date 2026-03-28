#nullable enable
using System.Windows.Controls;
using RemotePlayServer.ViewModels;

namespace RemotePlayServer.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    private void AutoStartToggled(object sender, System.EventArgs e)
    {
        if (DataContext is SettingsViewModel vm)
            vm.ToggleAutoStartCommand.Execute(null);
    }
}
