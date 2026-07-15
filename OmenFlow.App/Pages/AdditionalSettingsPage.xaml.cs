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

        // Auto Profiles
        bool autoProfilesEnabled = false;
        if (localSettings.TryGetValue("AutoProfileGamesEnabled", out object apVal))
            autoProfilesEnabled = (bool)apVal;
        ToggleAutoProfileGames.IsOn = autoProfilesEnabled;
        PanelAutoProfileGames.Visibility = autoProfilesEnabled ? Visibility.Visible : Visibility.Collapsed;

        // Omen Key Intercept
        bool omenKeyEnabled = false;
        if (localSettings.TryGetValue("OmenKeyInterceptEnabled", out object okVal))
            omenKeyEnabled = (bool)okVal;
        ToggleOmenKey.IsOn = omenKeyEnabled;

        _isInitializing = false;

        _ = LoadAutoProfileGamesAsync();
    }

    private async System.Threading.Tasks.Task LoadAutoProfileGamesAsync()
    {
        if (App.IpcClient == null) return;
        var games = await App.IpcClient.GetAutoProfileGamesAsync();
        if (games != null)
        {
            ListGames.ItemsSource = games;
        }
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

    private async void ToggleAutoProfileGames_Toggled(object sender, RoutedEventArgs e)
    {
        bool isOn = ToggleAutoProfileGames.IsOn;
        PanelAutoProfileGames.Visibility = isOn ? Visibility.Visible : Visibility.Collapsed;
        
        if (_isInitializing) return;
        
        Helpers.LocalSettings.Values["AutoProfileGamesEnabled"] = isOn;
        if (App.IpcClient != null)
        {
            await App.IpcClient.SendCommandAsync("SetAutoProfileConfig", new { IsEnabled = isOn });
        }
    }

    private async void BtnAddGame_Click(object sender, RoutedEventArgs e)
    {
        string game = TxtNewGame.Text.Trim();
        if (string.IsNullOrEmpty(game) || App.IpcClient == null) return;
        
        TxtNewGame.Text = "";
        var sent = await App.IpcClient.SendCommandAsync("AutoProfileAddGame", game);
        if (sent)
        {
            await System.Threading.Tasks.Task.Delay(100);
            await LoadAutoProfileGamesAsync();
        }
    }

    private async void BtnRemoveGame_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as Button;
        if (btn == null || btn.Tag == null || App.IpcClient == null) return;
        
        string game = btn.Tag.ToString() ?? "";
        if (string.IsNullOrEmpty(game)) return;

        var sent = await App.IpcClient.SendCommandAsync("AutoProfileRemoveGame", game);
        if (sent)
        {
            await System.Threading.Tasks.Task.Delay(100);
            await LoadAutoProfileGamesAsync();
        }
    }

    private async void ToggleOmenKey_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        
        Helpers.LocalSettings.Values["OmenKeyInterceptEnabled"] = ToggleOmenKey.IsOn;
        if (App.IpcClient != null)
        {
            await App.IpcClient.SendCommandAsync("SetOmenKeyIntercept", ToggleOmenKey.IsOn);
        }
    }
}
