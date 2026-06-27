using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI.Notifications;
using Windows.Data.Xml.Dom;
using OmenFlow_App.SubWindows;

namespace OmenFlow_App.Pages;

public sealed partial class PerformancePage : Page
{
    private CustomFanWindow? _customFanWindow;

    public PerformancePage()
    {
        this.InitializeComponent();
        App.IpcClient.TelemetryReceived += IpcClient_TelemetryReceived;
        this.Unloaded += PerformancePage_Unloaded;
    }

    private void PerformancePage_Unloaded(object sender, RoutedEventArgs e)
    {
        App.IpcClient.TelemetryReceived -= IpcClient_TelemetryReceived;
    }

    private void IpcClient_TelemetryReceived(object? sender, Helpers.TelemetryData e)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            if (CpuTempText != null) CpuTempText.Text = $"{e.CpuTemp:F0}°C";
            if (GpuTempText != null) GpuTempText.Text = $"{e.GpuTemp:F0}°C";
            if (CpuPowerText != null) CpuPowerText.Text = $"{e.CpuPower:F1} W";
            if (GpuPowerText != null) GpuPowerText.Text = $"{e.GpuPower:F1} W";
            if (CpuUsageText != null) CpuUsageText.Text = $"%{e.CpuLoad:F0}";
            if (GpuUsageText != null) GpuUsageText.Text = $"%{e.GpuLoad:F0}";
            if (CpuFanText != null) CpuFanText.Text = $"{e.CpuFanRpm} RPM";
            if (GpuFanText != null) GpuFanText.Text = $"{e.GpuFanRpm} RPM";

            // Sync Performance Profile
            if (BtnPerfQuiet != null) BtnPerfQuiet.IsChecked = (e.ActiveProfile == 0x50);
            if (BtnPerfDefault != null) BtnPerfDefault.IsChecked = (e.ActiveProfile == 0x30);
            if (BtnPerfPerf != null) BtnPerfPerf.IsChecked = (e.ActiveProfile == 0x31);

            // Sync Fan Mode
            if (BtnFanAuto != null) BtnFanAuto.IsChecked = (e.ActiveFanMode == 0);
            if (BtnFanOmenFlow != null) BtnFanOmenFlow.IsChecked = (e.ActiveFanMode == 1);
            if (BtnFanMax != null) BtnFanMax.IsChecked = (e.ActiveFanMode == 2);
            if (BtnFanManual != null) BtnFanManual.IsChecked = (e.ActiveFanMode == 3);

            // Sync GPU Mode
            if (BtnMuxHybrid != null) BtnMuxHybrid.IsChecked = (e.GpuMode == 0 || e.GpuMode == 2);
            if (BtnMuxDiscrete != null) BtnMuxDiscrete.IsChecked = (e.GpuMode == 1);
        });
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string param && param == "SelectCustomFan")
        {
            SetFanMode(BtnFanManual);
            OpenCustomFanWindow();
        }
    }

    // ========== OmenFlow Akıllı Fan Preseti ==========
    private static readonly List<FanCurvePointDto> OmenFlowPresetPoints = new()
    {
        new FanCurvePointDto { TemperatureCelsius = 50, FanSpeedPercent = 0 },
        new FanCurvePointDto { TemperatureCelsius = 60, FanSpeedPercent = 25 },
        new FanCurvePointDto { TemperatureCelsius = 70, FanSpeedPercent = 40 },
        new FanCurvePointDto { TemperatureCelsius = 80, FanSpeedPercent = 65 },
        new FanCurvePointDto { TemperatureCelsius = 85, FanSpeedPercent = 80 },
        new FanCurvePointDto { TemperatureCelsius = 90, FanSpeedPercent = 90 },
        new FanCurvePointDto { TemperatureCelsius = 95, FanSpeedPercent = 100 },
    };

    private async Task ApplyFanCurveAsync(List<FanCurvePointDto> points)
    {
        if (App.IpcClient == null) return;

        var curvePayload = new
        {
            Target = 2, // Both (CPU + GPU)
            Points = points
        };

        await App.IpcClient.SendCommandAsync("ApplyCurve", curvePayload);
    }

    private void OpenCustomFanWindow()
    {
        if (_customFanWindow == null)
        {
            _customFanWindow = new CustomFanWindow();
            _customFanWindow.Closed += (s, args) => { _customFanWindow = null; };
        }
        _customFanWindow.Activate();
    }

    // ========== Performans Profili ==========
    private async void PerfMode_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as ToggleButton;
        BtnPerfQuiet.IsChecked = (btn == BtnPerfQuiet);
        BtnPerfDefault.IsChecked = (btn == BtnPerfDefault);
        BtnPerfPerf.IsChecked = (btn == BtnPerfPerf);

        int profile = 0x30; // Default
        string modeName = "Varsayılan (Default)";
        if (btn == BtnPerfQuiet) { profile = 0x50; modeName = "Sessiz (Quiet)"; }
        if (btn == BtnPerfPerf) { profile = 0x31; modeName = "Performans"; }

        if (App.IpcClient != null)
        {
            await App.IpcClient.SendCommandAsync("SetThermalProfile", profile);
            ShowPerformanceToastNotification(modeName);
        }
    }

    private async void ShowPerformanceToastNotification(string modeName)
    {
        try
        {
            var dialog = new ContentDialog
            {
                Title = "Performans Modu Değişti",
                Content = $"Aktif Profil: {modeName}",
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

    // ========== Fan Modu ==========
    private async void FanMode_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as ToggleButton;
        SetFanMode(btn);
        
        if (btn == BtnFanManual)
        {
            OpenCustomFanWindow();
        }
        else if (btn == BtnFanOmenFlow)
        {
            await ApplyFanCurveAsync(OmenFlowPresetPoints);
        }
        else if (btn == BtnFanMax)
        {
            if (App.IpcClient != null)
                await App.IpcClient.SendCommandAsync("SetMaxFan");
        }
        else // Auto
        {
            if (App.IpcClient != null)
                await App.IpcClient.SendCommandAsync("SetAuto");
        }
    }

    private void SetFanMode(ToggleButton? btn)
    {
        BtnFanAuto.IsChecked = (btn == BtnFanAuto);
        BtnFanOmenFlow.IsChecked = (btn == BtnFanOmenFlow);
        BtnFanMax.IsChecked = (btn == BtnFanMax);
        BtnFanManual.IsChecked = (btn == BtnFanManual);
    }

    // ========== GPU Kontrolleri ==========
    private async void MuxMode_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as ToggleButton;
        int oldMode = (btn == BtnMuxDiscrete) ? 0 : 1; // Discrete tıklandıysa eski mod Hybrid(0), Hybrid tıklandıysa eski mod Discrete(1)
        int newMode = (btn == BtnMuxDiscrete) ? 1 : 0; // Discrete=1, Hybrid=0

        BtnMuxHybrid.IsChecked = (btn == BtnMuxHybrid);
        BtnMuxDiscrete.IsChecked = (btn == BtnMuxDiscrete);

        if (App.IpcClient != null)
        {
            await App.IpcClient.SendCommandAsync("SetGpuMode", newMode);
            ShowMuxToastNotification(oldMode, newMode);
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

/// <summary>
/// JSON serialization için DTO. OmenFlow.Core.Models.FanCurvePoint record struct olduğu için
/// System.Text.Json ile sorunsuz serialize edilebilmesi adına düz bir sınıf kullanıyoruz.
/// </summary>
public class FanCurvePointDto
{
    public int TemperatureCelsius { get; set; }
    public int FanSpeedPercent { get; set; }
}
