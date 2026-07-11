using System;
using System.Linq;
using System.Management;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using OmenFlow_App.Helpers;

namespace OmenFlow_App.Pages;

public sealed partial class GraphicsSwitcherPage : Page
{
    public GraphicsSwitcherPage()
    {
        this.InitializeComponent();
        App.IpcClient.TelemetryReceived += IpcClient_TelemetryReceived;
        this.Unloaded += GraphicsSwitcherPage_Unloaded;
        this.Loaded += GraphicsSwitcherPage_Loaded;
    }

    private void GraphicsSwitcherPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            string? discreteGpu = null;
            using (var searcher = new ManagementObjectSearcher("SELECT Name FROM Win32_VideoController"))
            {
                foreach (ManagementObject obj in searcher.Get())
                {
                    string name = obj["Name"]?.ToString() ?? "";
                    if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) || 
                        name.Contains("AMD Radeon RX", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("RTX", StringComparison.OrdinalIgnoreCase))
                    {
                        discreteGpu = name;
                        break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(discreteGpu))
            {
                TxtDiscreteDesc.Text = $"Ekran görüntünüz {discreteGpu} üzerinden verilir.";
            }
        }
        catch
        {
            // Ignore WMI errors
        }
    }

    private void GraphicsSwitcherPage_Unloaded(object sender, RoutedEventArgs e)
    {
        App.IpcClient.TelemetryReceived -= IpcClient_TelemetryReceived;
    }

    private static int? _pendingGpuMode = null;
    private bool _isSwitching = false;

    private void IpcClient_TelemetryReceived(object? sender, TelemetryData e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (LoadingRing != null && LoadingRing.IsActive)
            {
                LoadingRing.IsActive = false;
                LoadingRing.Visibility = Visibility.Collapsed;
                if (GpuButtonsGrid != null) GpuButtonsGrid.Visibility = Visibility.Visible;
            }

            if (_isSwitching) return; // Kullanıcı seçim yaparken arayüzü telemetri ile ezme
            int currentMode = _pendingGpuMode.HasValue ? _pendingGpuMode.Value : e.GpuMode;
            // GpuMode 0 = Hybrid, 1 = Dedicated/Discrete, 2 = Optimus
            if (BtnMuxHybrid != null) BtnMuxHybrid.IsChecked = (currentMode == 0 || currentMode == 2);
            if (BtnMuxDiscrete != null) BtnMuxDiscrete.IsChecked = (currentMode == 1);
        });
    }

    private async void MuxMode_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as RadioButton;
        if (btn == null) return;

        int newMode = (btn == BtnMuxDiscrete) ? 1 : 0;
        int oldMode = (newMode == 1) ? 0 : 1;

        if (_isSwitching)
        {
            // Eğer halihazırda işlem yapılıyorsa, görsel olarak tıklamayı geri al
            if (newMode == 1) BtnMuxHybrid.IsChecked = true;
            else BtnMuxDiscrete.IsChecked = true;
            return;
        }

        _isSwitching = true;
        try
        {
            _pendingGpuMode = newMode;

            if (App.IpcClient != null)
            {
                bool sent = await App.IpcClient.SendCommandAsync("SetGpuMode", newMode);
                if (!sent)
                {
                    _pendingGpuMode = null;
                    // Rollback
                    if (newMode == 1) BtnMuxHybrid.IsChecked = true;
                    else BtnMuxDiscrete.IsChecked = true;
                    
                    await ShowCommandFailedDialogAsync("MUX değiştirilemedi", "Worker servisine erişilemedi ya da komut reddedildi.");
                    return;
                }

                await ShowMuxToastNotificationAsync(oldMode, newMode);
            }
        }
        finally
        {
            _isSwitching = false;
        }
    }

    private async void BtnResetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (BtnMuxHybrid.IsChecked == true) return; // Already hybrid
        if (_isSwitching) return;

        _isSwitching = true;
        try
        {
            _pendingGpuMode = 0;

            if (App.IpcClient != null)
            {
                bool sent = await App.IpcClient.SendCommandAsync("SetGpuMode", 0);
                if (sent)
                {
                    BtnMuxHybrid.IsChecked = true;
                    BtnMuxDiscrete.IsChecked = false;
                    await ShowMuxToastNotificationAsync(1, 0);
                }
                else
                {
                    _pendingGpuMode = null;
                    await ShowCommandFailedDialogAsync("Sıfırlama başarısız", "Varsayılana geçiş yapılamadı.");
                }
            }
        }
        finally
        {
            _isSwitching = false;
        }
    }

    private async Task ShowCommandFailedDialogAsync(string title, string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = message,
                CloseButtonText = "Tamam",
                XamlRoot = this.XamlRoot,
                RequestedTheme = ElementTheme.Default
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"Dialog error: {ex.Message}");
        }
    }

    private async Task ShowMuxToastNotificationAsync(int oldMode, int newMode)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Yeniden Başlatma Gerekli",
                Content = "GPU MUX Modu değişikliğinin etkinleşmesi için bilgisayarınızı yeniden başlatmanız gerekiyor.",
                PrimaryButtonText = "Yeniden Başlat",
                CloseButtonText = "Daha Sonra",
                XamlRoot = this.XamlRoot,
                RequestedTheme = ElementTheme.Default
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("shutdown", "/r /t 0") { CreateNoWindow = true, UseShellExecute = false });
            }
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"Dialog error: {ex.Message}");
        }
    }
}

