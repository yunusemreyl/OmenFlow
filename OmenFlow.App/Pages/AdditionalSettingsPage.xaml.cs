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

        // Power Automation Settings
        bool powerAutoEnabled = false;
        if (localSettings.TryGetValue("PowerAutomationEnabled", out object paVal))
            powerAutoEnabled = (bool)paVal;
        TogglePowerAutomation.IsOn = powerAutoEnabled;
        PanelAutomationSettings.Visibility = powerAutoEnabled ? Visibility.Visible : Visibility.Collapsed;

        string acProfile = "Performance";
        if (localSettings.TryGetValue("PowerAutomationAcProfile", out object acVal))
            acProfile = acVal.ToString() ?? "Performance";
        
        foreach (ComboBoxItem item in ComboAcProfile.Items)
        {
            if (item.Tag?.ToString() == acProfile)
            {
                ComboAcProfile.SelectedItem = item;
                break;
            }
        }

        string batProfile = "Quiet";
        if (localSettings.TryGetValue("PowerAutomationBatProfile", out object batVal))
            batProfile = batVal.ToString() ?? "Quiet";

        foreach (ComboBoxItem item in ComboBatProfile.Items)
        {
            if (item.Tag?.ToString() == batProfile)
            {
                ComboBatProfile.SelectedItem = item;
                break;
            }
        }

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

    private async void TogglePowerAutomation_Toggled(object sender, RoutedEventArgs e)
    {
        bool isOn = TogglePowerAutomation.IsOn;
        PanelAutomationSettings.Visibility = isOn ? Visibility.Visible : Visibility.Collapsed;
        
        if (_isInitializing) return;
        
        Helpers.LocalSettings.Values["PowerAutomationEnabled"] = isOn;
        await SendPowerAutomationToBackendAsync();
    }

    private async void AutomationProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        if (ComboAcProfile.SelectedItem is ComboBoxItem acItem)
        {
            Helpers.LocalSettings.Values["PowerAutomationAcProfile"] = acItem.Tag?.ToString() ?? "Performance";
        }

        if (ComboBatProfile.SelectedItem is ComboBoxItem batItem)
        {
            Helpers.LocalSettings.Values["PowerAutomationBatProfile"] = batItem.Tag?.ToString() ?? "Quiet";
        }

        await SendPowerAutomationToBackendAsync();
    }

    private async Task SendPowerAutomationToBackendAsync()
    {
        if (App.IpcClient == null) return;

        bool isEnabled = TogglePowerAutomation.IsOn;
        string acProfile = (ComboAcProfile.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Performance";
        string batProfile = (ComboBatProfile.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Quiet";

        var payload = new
        {
            IsEnabled = isEnabled,
            OnAcProfile = acProfile,
            OnBatProfile = batProfile
        };

        await App.IpcClient.SendCommandAsync("SetPowerAutomation", payload);
    }
}
