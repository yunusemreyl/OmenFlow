using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace OmenFlow_App.Pages;

public sealed partial class PowerLimitsPage : Page
{
    public PowerLimitsPage()
    {
        InitializeComponent();

        if (App.IpcClient != null)
        {
            App.IpcClient.TelemetryReceived += IpcClient_TelemetryReceived;
        }
        this.Unloaded += PowerLimitsPage_Unloaded;
    }

    private void PowerLimitsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (App.IpcClient != null)
        {
            App.IpcClient.TelemetryReceived -= IpcClient_TelemetryReceived;
        }
    }

    private void IpcClient_TelemetryReceived(object? sender, Helpers.TelemetryData e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (CpuPowerText != null) CpuPowerText.Text = $"{e.CpuPower:F1} W";
            if (CpuTempText != null) CpuTempText.Text = $"{e.CpuTemp:F0}°C";
            if (GpuPowerText != null) GpuPowerText.Text = $"{e.GpuPower:F1} W";
            if (GpuTempText != null) GpuTempText.Text = $"{e.GpuTemp:F0}°C";

            // Sync dynamic GPU Max TGP limits
            if (SliderGpuTgp != null && e.GpuMaxTgp > 0 && SliderGpuTgp.Maximum != e.GpuMaxTgp)
            {
                SliderGpuTgp.Maximum = e.GpuMaxTgp;
            }
        });
    }

    private void Slider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (TextCpuPl1Value != null && SliderCpuPl1 != null)
            TextCpuPl1Value.Text = $"{(int)SliderCpuPl1.Value} W";
        if (TextCpuPl2Value != null && SliderCpuPl2 != null)
            TextCpuPl2Value.Text = $"{(int)SliderCpuPl2.Value} W";
        if (TextGpuTgpValue != null && SliderGpuTgp != null)
            TextGpuTgpValue.Text = $"{(int)SliderGpuTgp.Value} W";
    }

    private async void BtnApplyPowerLimits_Click(object sender, RoutedEventArgs e)
    {
        if (App.IpcClient == null) return;

        BtnApplyPowerLimits.IsEnabled = false;
        BtnApplyPowerLimits.Content = "Uygulanıyor...";

        int pl1 = (int)SliderCpuPl1.Value;
        int pl2 = (int)SliderCpuPl2.Value;
        int tgp = (int)SliderGpuTgp.Value;

        try
        {
            bool success = await App.IpcClient.SendCommandAsync("SetPowerLimits", new
            {
                CpuPl1W = pl1,
                CpuPl2W = pl2,
                GpuTgpW = tgp
            });

            var dialog = new ContentDialog
            {
                Title = success ? "Limitler Uygulandı" : "Başarısız",
                Content = success 
                    ? $"Güç limitleri başarıyla gönderildi:\nCPU PL1: {pl1}W\nCPU PL2: {pl2}W\nGPU TGP: {tgp}W\n\nNot: Donanımınız WMI limit kontrollerini desteklemiyorsa veya kilitliyse bu limitler yoksayılmış olabilir."
                    : "Güç limitleri worker tarafından reddedildi veya worker ile iletişim kurulamadı.",
                CloseButtonText = "Tamam",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = "Hata",
                Content = $"Güç limitleri uygulanırken hata oluştu:\n{ex.Message}",
                CloseButtonText = "Tamam",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
        finally
        {
            BtnApplyPowerLimits.IsEnabled = true;
            BtnApplyPowerLimits.Content = "Limitleri Uygula";
        }
    }
}
