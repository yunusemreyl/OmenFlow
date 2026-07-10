using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;
using OmenFlow_App.SubWindows;
using OmenFlow_App.Helpers;

namespace OmenFlow_App.Pages;

public sealed partial class PerformancePage : Page
{
    private CustomFanWindow? _customFanWindow;

    // Telemetri ile gÃ¼ncelleme sÄ±rasÄ±nda Click olaylarÄ±nÄ± tetiklememek iÃ§in guard
    private bool _updatingFromTelemetry = false;

    // Profil ve fan modu geÃ§iÅŸlerinde Ã§ift tÄ±klama / ardÄ±ÅŸÄ±k tÄ±klama Ã§Ã¶kmesini Ã¶nlemek iÃ§in guard'lar
    private bool _profileChangeInProgress = false;
    private bool _fanModeChangeInProgress = false;

    // Son bilinen performans profili â€” gereksiz tray bildirimi gÃ¶ndermemek iÃ§in
    private int _lastNotifiedProfile = -1;

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
            try
            {
                // â”€â”€ SÄ±caklÄ±k metinleri + dinamik renk â”€â”€
                if (CpuTempText != null)
                {
                    CpuTempText.Text = $"{e.CpuTemp:F0}Â°C";
                    CpuTempText.Foreground = GetTempBrush(e.CpuTemp);
                }
                if (GpuTempText != null)
                {
                    GpuTempText.Text = $"{e.GpuTemp:F0}Â°C";
                    GpuTempText.Foreground = GetTempBrush(e.GpuTemp);
                }

                // â”€â”€ GÃ¼Ã§ ve YÃ¼k â”€â”€
                if (CpuPowerText != null) CpuPowerText.Text = e.CpuPower > 0 ? $"{e.CpuPower:F1} W" : "-- W";
                if (GpuPowerText != null) GpuPowerText.Text = e.GpuPower > 0 ? $"{e.GpuPower:F1} W" : "-- W";
                if (CpuUsageText != null) CpuUsageText.Text = $"%{e.CpuLoad:F0}";
                if (GpuUsageText != null) GpuUsageText.Text = $"%{e.GpuLoad:F0}";

                // â”€â”€ GÃ¼Ã§ Detay Kutusu â”€â”€
                float totalPower = (e.CpuPower > 0 ? e.CpuPower : 0) + (e.GpuPower > 0 ? e.GpuPower : 0);
                if (CpuPowerTextDetailed != null) CpuPowerTextDetailed.Text = e.CpuPower > 0 ? $"{e.CpuPower:F1} W" : "-- W";
                if (GpuPowerTextDetailed != null) GpuPowerTextDetailed.Text = e.GpuPower > 0 ? $"{e.GpuPower:F1} W" : "-- W";
                if (TotalPowerTextDetailed != null) TotalPowerTextDetailed.Text = totalPower > 0 ? $"{totalPower:F1} W" : "-- W";

                // â”€â”€ Fan RPM â”€â”€
                if (CpuFanText != null)
                    CpuFanText.Text = Helpers.TelemetryDisplayHelper.FormatFanRpm(e.CpuFanRpm, e.CpuFanState);
                if (GpuFanText != null)
                    GpuFanText.Text = Helpers.TelemetryDisplayHelper.FormatFanRpm(e.GpuFanRpm, e.GpuFanState);

                // â”€â”€ Performans profili senkronizasyonu (guard ile â€” Click tetiklenmesin) â”€â”€
                _updatingFromTelemetry = true;
                try
                {
                    // ThermalProfile enum deÄŸerleri: Quiet=50, Default=30, Performance=31
                    bool isQuiet   = ((int)e.ActiveProfile == 50);
                    bool isDefault = ((int)e.ActiveProfile == 30);
                    bool isPerf    = ((int)e.ActiveProfile == 31);

                    if (isQuiet && BtnPerfQuiet?.IsChecked != true) { if (BtnPerfQuiet != null) BtnPerfQuiet.IsChecked = true; }
                    else if (isDefault && BtnPerfDefault?.IsChecked != true) { if (BtnPerfDefault != null) BtnPerfDefault.IsChecked = true; }
                    else if (isPerf && BtnPerfPerf?.IsChecked != true) { if (BtnPerfPerf != null) BtnPerfPerf.IsChecked = true; }
                }
                finally
                {
                    _updatingFromTelemetry = false;
                }

                // â”€â”€ Fan modu senkronizasyonu â”€â”€
                _updatingFromTelemetry = true;
                try
                {
                    if (e.ActiveFanMode == 0 && BtnFanAuto?.IsChecked != true) { if (BtnFanAuto != null) BtnFanAuto.IsChecked = true; }
                    else if (e.ActiveFanMode == 1 && BtnFanOmenFlow?.IsChecked != true) { if (BtnFanOmenFlow != null) BtnFanOmenFlow.IsChecked = true; }
                    else if (e.ActiveFanMode == 2 && BtnFanMax?.IsChecked != true) { if (BtnFanMax != null) BtnFanMax.IsChecked = true; }
                    else if (e.ActiveFanMode == 3 && BtnFanManual?.IsChecked != true) { if (BtnFanManual != null) BtnFanManual.IsChecked = true; }
                }
                finally
                {
                    _updatingFromTelemetry = false;
                }

                // â”€â”€ Aktif fan modu etiket metni â”€â”€
                if (FanModeDescText != null)
                {
                    FanModeDescText.Text = e.ActiveFanMode switch
                    {
                        0 => "BIOS otomatik â€” fanlar termal eÄŸriye gÃ¶re ayarlanÄ±r.",
                        1 => "OmenFlow akÄ±llÄ± eÄŸrisi â€” sÄ±caklÄ±ÄŸa gÃ¶re optimize edilmiÅŸ.",
                        2 => "Maksimum hÄ±z â€” fanlar tam gÃ¼Ã§te Ã§alÄ±ÅŸÄ±yor.",
                        3 => "Ã–zel eÄŸri â€” kullanÄ±cÄ± tanÄ±mlÄ± fan profili aktif.",
                        _ => "Fan modu bilinmiyor."
                    };
                }
            }
            catch (Exception ex)
            {
                OmenFlow.Core.Services.Logger.LogInfo($"[TelemetryReceived] UI Hata: {ex.Message}");
            }
        });
    }

    private static Microsoft.UI.Xaml.Media.SolidColorBrush GetTempBrush(float tempC)
    {
        if (tempC >= 85f)
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 220, 38, 38));   // KÄ±rmÄ±zÄ±
        if (tempC >= 70f)
            return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 217, 119, 6));   // Turuncu
        return new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.ColorHelper.FromArgb(255, 16, 185, 129));       // YeÅŸil
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string param && param == "SelectCustomFan")
        {
            SetFanModeRadio(BtnFanManual);
            OpenCustomFanWindow();
        }
    }

    // ========== OmenFlow AkÄ±llÄ± Fan Preseti ==========
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
        // Telemetri gÃ¼ncellemesinden gelen IsChecked deÄŸiÅŸikliÄŸi veya iÅŸlem devam ediyorsa â€” tÄ±klamayÄ± yoksay
        if (_updatingFromTelemetry || _profileChangeInProgress) return;

        var btn = sender as RadioButton;
        if (btn == null) return;

        // Zaten seÃ§ili olan buton tekrar tÄ±klandÄ±ysa iÅŸlem yapma
        if (btn.IsChecked != true) return;

        _profileChangeInProgress = true;
        SetProfileButtonsEnabled(false);
        try
        {
            int profile = 30; // Default
            string modeName = "VarsayÄ±lan";
            string modeIcon = "âš™ï¸";

            if (btn == BtnPerfQuiet)   { profile = 50; modeName = "Sessiz";      modeIcon = "ğŸŒ¿"; }
            if (btn == BtnPerfPerf)    { profile = 31; modeName = "Performans";  modeIcon = "ğŸš€"; }

            if (App.IpcClient != null)
            {
                bool sent = await App.IpcClient.SendCommandAsync("SetThermalProfile", profile);
                if (!sent)
                {
                    await ShowCommandFailedDialogAsync("Profil uygulanamadÄ±", "Worker servisine eriÅŸilemedi ya da komut reddedildi.");
                    return;
                }

                // Sadece profil gerÃ§ekten deÄŸiÅŸtiyse bildirim gÃ¶nder
                if (_lastNotifiedProfile != profile)
                {
                    _lastNotifiedProfile = profile;
                    ShowTrayNotification("Performans Modu", $"{modeIcon} {modeName} profili aktif edildi.");
                }
            }
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[PerfMode_Click] Exception: {ex.Message}");
        }
        finally
        {
            _profileChangeInProgress = false;
            SetProfileButtonsEnabled(true);
        }
    }

    // ========== Windows Tray (Toast) Bildirimi ==========
    private static void ShowTrayNotification(string title, string message)
    {
        try
        {
            string xml = $@"
<toast>
  <visual>
    <binding template='ToastGeneric'>
      <text>{title}</text>
      <text>{message}</text>
    </binding>
  </visual>
</toast>";
            var doc = new XmlDocument();
            doc.LoadXml(xml);
            var notifier = ToastNotificationManager.CreateToastNotifier("OmenFlow");
            var notification = new ToastNotification(doc)
            {
                ExpirationTime = DateTimeOffset.Now.AddSeconds(4)
            };
            notifier.Show(notification);
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[Toast] Bildirim gÃ¶nderilemedi: {ex.Message}");
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

    // ========== Fan Modu ==========
    private async void FanMode_Click(object sender, RoutedEventArgs e)
    {
        if (_updatingFromTelemetry || _fanModeChangeInProgress) return;

        var btn = sender as RadioButton;
        if (btn == null) return;
        if (btn.IsChecked != true) return;

        _fanModeChangeInProgress = true;
        SetFanButtonsEnabled(false);
        try
        {
            bool prevAuto      = BtnFanAuto.IsChecked == true;
            bool prevOmenFlow  = BtnFanOmenFlow.IsChecked == true;
            bool prevMax       = BtnFanMax.IsChecked == true;
            bool prevManual    = BtnFanManual.IsChecked == true;

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
                        await ShowCommandFailedDialogAsync("Fan modu uygulanamadÄ±", "Worker servisine eriÅŸilemedi ya da komut reddedildi.");
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
                        await ShowCommandFailedDialogAsync("Fan modu uygulanamadÄ±", "Worker servisine eriÅŸilemedi ya da komut reddedildi.");
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
                        await ShowCommandFailedDialogAsync("Fan modu uygulanamadÄ±", "Worker servisine eriÅŸilemedi ya da komut reddedildi.");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[FanMode_Click] Exception: {ex.Message}");
        }
        finally
        {
            _fanModeChangeInProgress = false;
            SetFanButtonsEnabled(true);
        }
    }

    private void RestoreFanModeRollback(bool auto, bool flow, bool max, bool manual)
    {
        _updatingFromTelemetry = true;
        try
        {
            if (auto && BtnFanAuto?.IsChecked != true) BtnFanAuto.IsChecked = true;
            else if (flow && BtnFanOmenFlow?.IsChecked != true) BtnFanOmenFlow.IsChecked = true;
            else if (max && BtnFanMax?.IsChecked != true) BtnFanMax.IsChecked = true;
            else if (manual && BtnFanManual?.IsChecked != true) BtnFanManual.IsChecked = true;
        }
        finally
        {
            _updatingFromTelemetry = false;
        }
    }

    private void SetFanModeRadio(RadioButton? btn)
    {
        _updatingFromTelemetry = true;
        try
        {
            if (btn != null && btn.IsChecked != true) btn.IsChecked = true;
        }
        finally
        {
            _updatingFromTelemetry = false;
        }
    }

    private async void BtnShowFanLogs_Click(object sender, RoutedEventArgs e)
    {
        if (App.IpcClient == null) return;

        BtnShowFanLogs.IsEnabled = false;
        BtnShowFanLogs.Content   = "YÃ¼kleniyor...";

        try
        {
            string? response  = await App.IpcClient.SendCommandWithResultAsync("GetFanDiagnostics");
            string reportText = "GÃ¼nlÃ¼k bilgisi alÄ±namadÄ±.";

            if (response != null && response.Contains("Report"))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(response);
                if (doc.RootElement.TryGetProperty("Report", out var rProp))
                    reportText = rProp.GetString() ?? "Log geÃ§miÅŸi boÅŸ.";
            }

            var scrollViewer = new ScrollViewer
            {
                VerticalScrollBarVisibility   = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                MaxHeight = 400,
                Content = new TextBox
                {
                    Text = reportText,
                    IsReadOnly      = true,
                    AcceptsReturn   = true,
                    TextWrapping    = TextWrapping.NoWrap,
                    FontFamily      = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
                    FontSize        = 12
                }
            };

            var dialog = new ContentDialog
            {
                Title           = "Fan TanÄ±lama GeÃ§miÅŸi (Log)",
                Content         = scrollViewer,
                CloseButtonText = "Kapat",
                XamlRoot        = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
        catch (Exception ex)
        {
            var dialog = new ContentDialog
            {
                Title           = "Hata",
                Content         = $"GÃ¼nlÃ¼kler yÃ¼klenirken hata oluÅŸtu:\n{ex.Message}",
                CloseButtonText = "Kapat",
                XamlRoot        = this.XamlRoot
            };
            await dialog.ShowAsync();
        }
        finally
        {
            BtnShowFanLogs.IsEnabled = true;
            BtnShowFanLogs.Content   = "Fan GÃ¼nlÃ¼kleri";
        }
    }
    private void SetProfileButtonsEnabled(bool enabled)
    {
        if (BtnPerfQuiet != null) BtnPerfQuiet.IsEnabled = enabled;
        if (BtnPerfDefault != null) BtnPerfDefault.IsEnabled = enabled;
        if (BtnPerfPerf != null) BtnPerfPerf.IsEnabled = enabled;
    }

    private void SetFanButtonsEnabled(bool enabled)
    {
        if (BtnFanAuto != null) BtnFanAuto.IsEnabled = enabled;
        if (BtnFanOmenFlow != null) BtnFanOmenFlow.IsEnabled = enabled;
        if (BtnFanMax != null) BtnFanMax.IsEnabled = enabled;
        if (BtnFanManual != null) BtnFanManual.IsEnabled = enabled;
    }
}

/// <summary>
/// JSON serialization iÃ§in DTO. OmenFlow.Core.Models.FanCurvePoint record struct olduÄŸu iÃ§in
/// System.Text.Json ile sorunsuz serialize edilebilmesi adÄ±na dÃ¼z bir sÄ±nÄ±f kullanÄ±yoruz.
/// </summary>
public class FanCurvePointDto
{
    public int TemperatureCelsius { get; set; }
    public int FanSpeedPercent    { get; set; }
}

