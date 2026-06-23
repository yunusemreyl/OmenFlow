using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OmenFlow_App.Pages;

public sealed partial class AdditionalSettingsPage : Page
{
    private bool _isInitializing = true;

    public AdditionalSettingsPage()
    {
        this.InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
    {
        ToggleWinKeyLock.IsOn = Helpers.WindowsKeyLockHelper.IsLocked;
        ToggleTouchpadLock.IsOn = Helpers.TouchpadHelper.IsLocked;
        
        var localSettings = Helpers.LocalSettings.Values;
        
        if (localSettings.TryGetValue("BatteryCare", out object bVal))
            ToggleBatteryCare.IsOn = (bool)bVal;
        else
            ToggleBatteryCare.IsOn = false;

        if (localSettings.TryGetValue("UsbCharging", out object uVal))
            ToggleUsbCharging.IsOn = (bool)uVal;
        else
            ToggleUsbCharging.IsOn = false;

        _isInitializing = false;
    }

    private async void ToggleBatteryCare_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        Helpers.LocalSettings.Values["BatteryCare"] = ToggleBatteryCare.IsOn;
        await App.IpcClient.SendCommandAsync("SetBatteryCare", ToggleBatteryCare.IsOn);
    }

    private async void ToggleUsbCharging_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        Helpers.LocalSettings.Values["UsbCharging"] = ToggleUsbCharging.IsOn;
        await App.IpcClient.SendCommandAsync("SetUsbCharging", ToggleUsbCharging.IsOn);
    }

    private void ToggleWinKeyLock_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        Helpers.WindowsKeyLockHelper.IsLocked = ToggleWinKeyLock.IsOn;
    }

    private void ToggleTouchpadLock_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        Helpers.TouchpadHelper.IsLocked = ToggleTouchpadLock.IsOn;
    }
}
