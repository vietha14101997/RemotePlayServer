#nullable enable
using System.Windows.Controls;
using RemotePlayServer.ViewModels;

namespace RemotePlayServer.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
    }

    private void PasswordBox_PasswordChanged(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm)
            vm.Password = PasswordBox.Password;
    }
}
