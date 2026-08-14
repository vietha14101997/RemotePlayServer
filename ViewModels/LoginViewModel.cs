#nullable enable
using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using RemotePlayServer.Core;
using RemotePlayServer.Core.Models;
using RemotePlayServer.Infrastructure.Network;

namespace RemotePlayServer.ViewModels;

public partial class LoginViewModel : ObservableObject
{
    [ObservableProperty] private string _relayUrl = "";
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private bool _rememberMe = true;
    [ObservableProperty] private bool _isRegistering;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _errorMessage = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private string _loggedInEmail = "";

    /// <summary>Fired when login/register succeeds. Passes the RelayClient instance.</summary>
    public event Action<RelayClient>? OnLoginSuccess;

    /// <summary>Fired when user clicks logout.</summary>
    public event Action? OnLogoutRequested;

    public void LoadFromConfig(InternetConfig config)
    {
        RelayUrl = config.RelayUrl ?? "";
        Email = config.RelayEmail ?? "";
        Password = config.RelayPassword ?? "";
    }

    public void SetLoggedIn(string email)
    {
        IsLoggedIn = true;
        LoggedInEmail = email;
        ErrorMessage = "";
        StatusMessage = "";
    }

    [RelayCommand]
    private void ToggleMode()
    {
        IsRegistering = !IsRegistering;
        ErrorMessage = "";
    }

    [RelayCommand(CanExecute = nameof(CanSubmit))]
    private async Task SubmitAsync()
    {
        IsBusy = true;
        ErrorMessage = "";

        try
        {
            if (string.IsNullOrWhiteSpace(RelayUrl))
            {
                ErrorMessage = "Relay URL is required";
                return;
            }

            if (IsRegistering)
            {
                if (string.IsNullOrWhiteSpace(Username))
                {
                    ErrorMessage = "Username is required";
                    return;
                }
            }

            var client = new RelayClient();
            try
            {
                if (IsRegistering)
                {

                    StatusMessage = "Creating account...";
                    var (success, error) = await client.RegisterAsync(RelayUrl.Trim(), Email.Trim(), Username.Trim(), Password);
                    if (!success)
                    {
                        ErrorMessage = error ?? "Registration failed";
                        client.Dispose();
                        return;
                    }
                }
                else
                {
                    StatusMessage = "Signing in...";
                    var success = await client.LoginAsync(RelayUrl.Trim(), Email.Trim(), Password);
                    if (!success)
                    {
                        ErrorMessage = client.LastAuthError ?? "Invalid email or password";
                        client.Dispose();
                        return;
                    }
                }

                StatusMessage = "";
                IsLoggedIn = true;
                LoggedInEmail = Email.Trim();
                OnLoginSuccess?.Invoke(client);
            }
            catch
            {
                client.Dispose();
                throw;
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Connection error: {ex.Message}";
            Logger.Error($"[Login] Error: {ex.Message}");
        }
        finally
        {
            IsBusy = false;
            StatusMessage = "";
        }
    }

    private bool CanSubmit() => !IsBusy;

    partial void OnIsBusyChanged(bool value) => SubmitCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    private void Logout()
    {
        IsLoggedIn = false;
        LoggedInEmail = "";
        Password = "";
        OnLogoutRequested?.Invoke();
    }
}
