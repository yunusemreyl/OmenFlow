using Microsoft.UI.Xaml.Controls;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

using Microsoft.Win32;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml;
using System;
using OmenFlow_App.Helpers;

namespace OmenFlow_App.Pages;

public sealed partial class HomePage : Page
{
    private bool _isSyncing = false;

    public HomePage()
    {
        InitializeComponent();
        LoadSystemDeviceData();
        App.IpcClient.TelemetryReceived += IpcClient_TelemetryReceived;
    }

    private void IpcClient_TelemetryReceived(object? sender, Helpers.TelemetryData e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            CpuTempText.Text = $"{e.CpuTemp:F0}°C";
            GpuTempText.Text = $"{e.GpuTemp:F0}°C";
            CpuUsageText.Text = $"%{e.CpuLoad:F0}";
            GpuUsageText.Text = $"%{e.GpuLoad:F0}";
            CpuFanText.Text = $"{e.CpuTemp:F0}°C - {TelemetryDisplayHelper.FormatFanRpm(e.CpuFanRpm, e.CpuFanState)}";
            GpuFanText.Text = $"{e.GpuTemp:F0}°C - {TelemetryDisplayHelper.FormatFanRpm(e.GpuFanRpm, e.GpuFanState)}";

            _isSyncing = true;
            if (FanModeComboBox != null)
            {
                if (e.ActiveFanMode == 2) FanModeComboBox.SelectedIndex = 1; // Max Fan
                else if (e.ActiveFanMode == 3 || e.ActiveFanMode == 1) FanModeComboBox.SelectedIndex = 2; // Manual / OmenFlow
                else FanModeComboBox.SelectedIndex = 0; // Auto
            }

            if (PowerModeComboBox != null)
            {
                if ((int)e.ActiveProfile == 50) PowerModeComboBox.SelectedIndex = 2; // Quiet
                else if ((int)e.ActiveProfile == 31) PowerModeComboBox.SelectedIndex = 0; // Performance
                else PowerModeComboBox.SelectedIndex = 1; // Balanced
            }
            _isSyncing = false;
        });
    }

    private void LoadSystemDeviceData()
    {
        try
        {
            string systemModel = Helpers.ResourceHelper.GetString("HomePage_UnknownSystem");
            
            using (RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS"))
            {
                if (key != null)
                {
                    object? prodName = key.GetValue("SystemProductName");
                    if (prodName != null)
                    {
                        systemModel = prodName.ToString() ?? systemModel;
                    }
                }
            }

            BoardNumberText.Text = string.Format(Helpers.ResourceHelper.GetString("HomePage_SystemModel"), systemModel);

            if (systemModel.IndexOf("Victus", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                DeviceTitleText.Text = "HP Victus";
                DeviceImage.Source = new BitmapImage(new Uri("ms-appx:///icons/hpvictus.png"));
            }
            else if (systemModel.IndexOf("Omen", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                DeviceTitleText.Text = "HP OMEN";
                DeviceImage.Source = new BitmapImage(new Uri("ms-appx:///icons/hpomen.png"));
            }
            else
            {
                // Fallback to Omen
                DeviceTitleText.Text = "HP Laptop";
                DeviceImage.Source = new BitmapImage(new Uri("ms-appx:///icons/hpomen.png"));
            }
        }
        catch (Exception)
        {
            DeviceTitleText.Text = "HP Laptop";
            BoardNumberText.Text = Helpers.ResourceHelper.GetString("HomePage_SystemModelError");
        }
    }

    private async void PowerModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncing) return;

        if (PowerModeComboBox?.SelectedItem is ComboBoxItem item)
        {
            int profile = 30; // Balanced/Default
            if (item.Content.ToString() == "Quiet") profile = 50;
            if (item.Content.ToString() == "Performance") profile = 31;

            if (App.IpcClient != null)
            {
                await App.IpcClient.SendCommandAsync("SetThermalProfile", profile);
            }
        }
    }

    private async void FanModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isSyncing) return;

        if (FanModeComboBox?.SelectedItem is ComboBoxItem item)
        {
            string mode = item.Content.ToString() ?? "";
            
            if (mode == "Manual")
            {
                var mainWindow = (Application.Current as App)?.GetMainWindow() as MainWindow;
                mainWindow?.NavigateToPerformance("SelectCustomFan");
            }
            else if (App.IpcClient != null)
            {
                if (mode == "Max Fan")
                    await App.IpcClient.SendCommandAsync("SetMaxFan", true);
                else
                    await App.IpcClient.SendCommandAsync("SetAuto");
            }
        }
    }

    private void PowerCoolingCard_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        var mainWindow = (Application.Current as App)?.GetMainWindow() as MainWindow;
        mainWindow?.NavigateToPerformance(null!);
    }

    private void LightingCard_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        var mainWindow = (Application.Current as App)?.GetMainWindow() as MainWindow;
        mainWindow?.NavigateToLighting();
    }
}
