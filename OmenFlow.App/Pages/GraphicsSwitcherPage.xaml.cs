using System;
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
    }

    private void GraphicsSwitcherPage_Unloaded(object sender, RoutedEventArgs e)
    {
        App.IpcClient.TelemetryReceived -= IpcClient_TelemetryReceived;
    }

    private void IpcClient_TelemetryReceived(object? sender, TelemetryData e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            // GpuMode 0 = Hybrid, 1 = Dedicated/Discrete, 2 = Optimus
            if (BtnMuxHybrid != null) BtnMuxHybrid.IsChecked = (e.GpuMode == 0 || e.GpuMode == 2);
            if (BtnMuxDiscrete != null) BtnMuxDiscrete.IsChecked = (e.GpuMode == 1);
        });
    }

    private async void MuxMode_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as RadioButton;
        if (btn == null) return;

        int newMode = (btn == BtnMuxDiscrete) ? 1 : 0;
        int oldMode = (newMode == 1) ? 0 : 1;

        if (App.IpcClient != null)
        {
            bool sent = await App.IpcClient.SendCommandAsync("SetGpuMode", newMode);
            if (!sent)
            {
                // Rollback
                if (newMode == 1)
                {
                    BtnMuxHybrid.IsChecked = true;
                }
                else
                {
                    BtnMuxDiscrete.IsChecked = true;
                }
                await ShowCommandFailedDialogAsync("MUX değiştirilemedi", "Worker servisine erişilemedi ya da komut reddedildi.");
                return;
            }

            ShowMuxToastNotification(oldMode, newMode);
        }
    }

    private async void BtnResetDefault_Click(object sender, RoutedEventArgs e)
    {
        // Reset default GPU mode to Hybrid (0)
        if (BtnMuxHybrid.IsChecked == true) return; // Already hybrid

        if (App.IpcClient != null)
        {
            bool sent = await App.IpcClient.SendCommandAsync("SetGpuMode", 0);
            if (sent)
            {
                BtnMuxHybrid.IsChecked = true;
                BtnMuxDiscrete.IsChecked = false;
                ShowMuxToastNotification(1, 0);
            }
            else
            {
                await ShowCommandFailedDialogAsync("Sıfırlama başarısız", "Varsayılan moda geçiş yapılamadı.");
            }
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
            System.Diagnostics.Debug.WriteLine($"Dialog error: {ex.Message}");
        }
    }

    private async void ShowMuxToastNotification(int oldMode, int newMode)
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
            System.Diagnostics.Debug.WriteLine($"Dialog error: {ex.Message}");
        }
    }
}
