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
using OmenFlow_App.Helpers;

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
            // Sıcaklık metinleri + dinamik renk (yeşil→sarı→kırmızı)
            if (CpuTempText != null)
            {
                CpuTempText.Text = $"{e.CpuTemp:F0}°C";
                CpuTempText.Foreground = GetTempBrush(e.CpuTemp);
            }
            if (GpuTempText != null)
            {
                GpuTempText.Text = $"{e.GpuTemp:F0}°C";
                GpuTempText.Foreground = GetTempBrush(e.GpuTemp);
            }

            if (CpuPowerText != null) CpuPowerText.Text = $"{e.CpuPower:F1} W";
            if (GpuPowerText != null) GpuPowerText.Text = $"{e.GpuPower:F1} W";
            if (CpuUsageText != null) CpuUsageText.Text = $"%{e.CpuLoad:F0}";
            if (GpuUsageText != null) GpuUsageText.Text = $"%{e.GpuLoad:F0}";
            if (CpuFanText  != null) CpuFanText.Text = Helpers.TelemetryDisplayHelper.FormatFanRpm(e.CpuFanRpm, e.CpuFanState);
            if (GpuFanText  != null) GpuFanText.Text = Helpers.TelemetryDisplayHelper.FormatFanRpm(e.GpuFanRpm, e.GpuFanState);

            // Performans profili senkronizasyonu
            if (BtnPerfQuiet   != null) BtnPerfQuiet.IsChecked   = ((int)e.ActiveProfile == 50);
            if (BtnPerfDefault != null) BtnPerfDefault.IsChecked = ((int)e.ActiveProfile == 30);
            if (BtnPerfPerf    != null) BtnPerfPerf.IsChecked    = ((int)e.ActiveProfile == 31);

            // Fan modu senkronizasyonu
            if (BtnFanAuto    != null) BtnFanAuto.IsChecked    = (e.ActiveFanMode == 0);
            if (BtnFanOmenFlow!= null) BtnFanOmenFlow.IsChecked= (e.ActiveFanMode == 1);
            if (BtnFanMax     != null) BtnFanMax.IsChecked     = (e.ActiveFanMode == 2);
            if (BtnFanManual  != null) BtnFanManual.IsChecked  = (e.ActiveFanMode == 3);

        });
    }

    /// <summary>
    /// Sıcaklığa göre renk döndürür:
    ///   0–69°C → Yeşil (normal)
    ///   70–84°C → Sarı/turuncu (yüksek)
    ///   85°C+   → Kırmızı (kritik)
    /// </summary>
    private static Microsoft.UI.Xaml.Media.SolidColorBrush GetTempBrush(float tempC)
    {
        if (tempC >= 85f)
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 220, 38, 38));   // Kırmızı
        if (tempC >= 70f)
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 217, 119, 6));   // Turuncu
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 16, 185, 129));       // Yeşil
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
        var btn = sender as RadioButton;
        if (btn == null) return;

        int profile = 30; // Default
        string modeName = "Varsayılan (Default)";
        if (btn == BtnPerfQuiet) { profile = 50; modeName = "Sessiz (Quiet)"; }
        if (btn == BtnPerfPerf) { profile = 31; modeName = "Performans"; }

        if (App.IpcClient != null)
        {
            bool sent = await App.IpcClient.SendCommandAsync("SetThermalProfile", profile);
            if (!sent)
            {
                // Rollback
                if (profile == 50) BtnPerfQuiet.IsChecked = true;
                else if (profile == 31) BtnPerfPerf.IsChecked = true;
                else BtnPerfDefault.IsChecked = true;

                await ShowCommandFailedDialogAsync("Profil uygulanamadı", "Worker servisine erişilemedi ya da komut reddedildi.");
                return;
            }

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
        var btn = sender as RadioButton;
        if (btn == null) return;

        bool prevAuto = BtnFanAuto.IsChecked == true;
        bool prevOmenFlow = BtnFanOmenFlow.IsChecked == true;
        bool prevMax = BtnFanMax.IsChecked == true;
        bool prevManual = BtnFanManual.IsChecked == true;

        if (btn == BtnFanManual)
        {
            OpenCustomFanWindow();
        }
        else if (btn == BtnFanOmenFlow)
        {
            if (App.IpcClient != null)
            {
                bool sent = await App.IpcClient.SendCommandAsync("SetFlow");
                if (!sent)
                {
                    RestoreFanModeRollback(prevAuto, prevOmenFlow, prevMax, prevManual);
                    await ShowCommandFailedDialogAsync("Fan modu uygulanamadı", "Worker servisine erişilemedi ya da komut reddedildi.");
                }
            }
        }
        else if (btn == BtnFanMax)
        {
            if (App.IpcClient != null)
            {
                bool sent = await App.IpcClient.SendCommandAsync("SetMaxFan", true);
                if (!sent)
                {
                    RestoreFanModeRollback(prevAuto, prevOmenFlow, prevMax, prevManual);
                    await ShowCommandFailedDialogAsync("Fan modu uygulanamadı", "Worker servisine erişilemedi ya da komut reddedildi.");
                }
            }
        }
        else if (btn == BtnFanAuto)
        {
            if (App.IpcClient != null)
            {
                bool sent = await App.IpcClient.SendCommandAsync("SetAuto");
                if (!sent)
                {
                    RestoreFanModeRollback(prevAuto, prevOmenFlow, prevMax, prevManual);
                    await ShowCommandFailedDialogAsync("Fan modu uygulanamadı", "Worker servisine erişilemedi ya da komut reddedildi.");
                }
            }
        }
    }

    private void RestoreFanModeRollback(bool auto, bool flow, bool max, bool manual)
    {
        BtnFanAuto.IsChecked = auto;
        BtnFanOmenFlow.IsChecked = flow;
        BtnFanMax.IsChecked = max;
        BtnFanManual.IsChecked = manual;
    }

    private void SetFanMode(RadioButton? btn)
    {
        BtnFanAuto.IsChecked = (btn == BtnFanAuto);
        BtnFanOmenFlow.IsChecked = (btn == BtnFanOmenFlow);
        BtnFanMax.IsChecked = (btn == BtnFanMax);
        BtnFanManual.IsChecked = (btn == BtnFanManual);
    }

    private async void BtnShowFanLogs_Click(object sender, RoutedEventArgs e)
    {
        if (App.IpcClient == null) return;

        BtnShowFanLogs.IsEnabled = false;
        BtnShowFanLogs.Content = "Yükleniyor...";

        try
        {
            string? response = await App.IpcClient.SendCommandWithResultAsync("GetFanDiagnostics");
            string reportText = "Günlük bilgisi alınamadı.";

            if (response != null && response.Contains("Report"))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(response);
                if (doc.RootElement.TryGetProperty("Report", out var rProp))
                {
                    reportText = rProp.GetString() ?? "Log geçmişi boş.";
                }
            }

            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 400,
                Content = new TextBox
                {
                    Text = reportText,
                    IsReadOnly = true,
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.NoWrap,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize = 12
                }
            };

            var dialog = new ContentDialog
            {
                Title = "Fan Tanılama Geçmişi (Log)",
                Content = scrollViewer,
                CloseButtonText = "Kapat",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title = "Hata",
                Content = $"Günlükler yüklenirken hata oluştu:\n{ex.Message}",
                CloseButtonText = "Kapat",
                XamlRoot = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
        finally
        {
            BtnShowFanLogs.IsEnabled = true;
            BtnShowFanLogs.Content = "Günlükleri Göster";
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
