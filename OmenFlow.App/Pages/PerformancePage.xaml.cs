using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Microsoft.UI.Xaml.Navigation;

namespace OmenFlow_App.Pages;

public sealed partial class PerformancePage : Page
{
    private List<Ellipse> _fanCurvePoints = new List<Ellipse>();
    private Ellipse? _draggingPoint = null;

    public PerformancePage()
    {
        this.InitializeComponent();
        InitializeFanCurve();
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

            // Sync GPU Mode
            if (BtnMuxHybrid != null) BtnMuxHybrid.IsChecked = (e.GpuMode == 0 || e.GpuMode == 2);
            if (BtnMuxDiscrete != null) BtnMuxDiscrete.IsChecked = (e.GpuMode == 1);

            // Sync GPU Power
            if (BtnGpuBase != null) BtnGpuBase.IsChecked = (e.GpuPowerLevel == 0);
            if (BtnGpuExtra != null) BtnGpuExtra.IsChecked = (e.GpuPowerLevel == 1);
            if (BtnGpuMax != null) BtnGpuMax.IsChecked = (e.GpuPowerLevel == 2);
        });
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string param && param == "SelectCustomFan")
        {
            SetFanMode(BtnFanManual);
            CustomFanCurveContainer.Visibility = Visibility.Visible;
        }
    }

    // ========== OmenFlow Akıllı Fan Preseti ==========
    // 60°C altı: Fan sessiz (%0 - BIOS yönetir)
    // 60°C: Hafif soğutma başlar (%25)
    // 70°C: Orta düzey soğutma (%40)
    // 80°C: Efektif soğutma (%65)
    // 85°C: Agresif soğutma (%80)
    // 90°C: Yüksek soğutma (%90)
    // 95°C+: Acil durum - tam güç (%100)
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

    // ========== Fan Eğrisi Grafiği ==========

    private void InitializeFanCurve()
    {
        // 5 sürüklenebilir nokta
        double[] defaultTemps = { 30, 50, 70, 85, 100 };
        double[] defaultSpeeds = { 20, 40, 60, 80, 100 };

        for (int i = 0; i < 5; i++)
        {
            var ellipse = new Ellipse
            {
                Width = 16,
                Height = 16,
                Fill = new SolidColorBrush(Colors.White),
                Stroke = new SolidColorBrush(ColorHelper.FromArgb(255, 16, 185, 129)),
                StrokeThickness = 3
            };

            double x = (defaultTemps[i] / 100.0) * 800;
            double y = 200 - ((defaultSpeeds[i] / 100.0) * 200);

            Canvas.SetLeft(ellipse, x - 8);
            Canvas.SetTop(ellipse, y - 8);

            ellipse.PointerPressed += Ellipse_PointerPressed;

            _fanCurvePoints.Add(ellipse);
            FanCurveCanvas.Children.Add(ellipse);
        }
        UpdateFanCurveLine();
    }

    private void Ellipse_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _draggingPoint = sender as Ellipse;
        FanCurveCanvas.CapturePointer(e.Pointer);
    }

    private void FanCurveCanvas_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingPoint != null)
        {
            var pos = e.GetCurrentPoint(FanCurveCanvas).Position;
            
            double y = Math.Clamp(pos.Y, 0, 200);

            int index = _fanCurvePoints.IndexOf(_draggingPoint);
            double minX = index > 0 ? Canvas.GetLeft(_fanCurvePoints[index - 1]) + 8 : 0;
            double maxX = index < _fanCurvePoints.Count - 1 ? Canvas.GetLeft(_fanCurvePoints[index + 1]) + 8 : 800;
            
            double x = Math.Clamp(pos.X, minX + 1, maxX - 1);

            Canvas.SetLeft(_draggingPoint, x - 8);
            Canvas.SetTop(_draggingPoint, y - 8);

            UpdateFanCurveLine();
        }
    }

    private void FanCurveCanvas_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_draggingPoint != null)
        {
            FanCurveCanvas.ReleasePointerCapture(e.Pointer);
            _draggingPoint = null;
        }
    }

    private void UpdateFanCurveLine()
    {
        var points = new PointCollection();
        foreach (var p in _fanCurvePoints)
        {
            points.Add(new Point(Canvas.GetLeft(p) + 8, Canvas.GetTop(p) + 8));
        }
        FanCurveLine.Points = points;
    }

    /// <summary>
    /// Canvas üzerindeki sürüklenebilir noktalardan FanCurvePoint listesi oluşturur.
    /// Canvas: X ekseni 0-800 → Sıcaklık 0-100°C, Y ekseni 200-0 → Fan Hızı 0-100%
    /// </summary>
    private List<FanCurvePointDto> GetCustomCurveFromCanvas()
    {
        var result = new List<FanCurvePointDto>();
        foreach (var ellipse in _fanCurvePoints)
        {
            double x = Canvas.GetLeft(ellipse) + 8; // center
            double y = Canvas.GetTop(ellipse) + 8;

            int tempC = (int)Math.Round((x / 800.0) * 100.0);
            int speedPercent = (int)Math.Round((1.0 - (y / 200.0)) * 100.0);

            tempC = Math.Clamp(tempC, 0, 100);
            speedPercent = Math.Clamp(speedPercent, 0, 100);

            result.Add(new FanCurvePointDto { TemperatureCelsius = tempC, FanSpeedPercent = speedPercent });
        }
        return result;
    }

    // ========== "Uygula" Butonu ==========
    private async void ApplyCurveButton_Click(object sender, RoutedEventArgs e)
    {
        var points = GetCustomCurveFromCanvas();
        await ApplyFanCurveAsync(points);

        // Kısa bir onay göster
        if (ApplyCurveButton != null)
        {
            ApplyCurveButton.Content = "✓ Uygulandı!";
            await Task.Delay(1500);
            ApplyCurveButton.Content = "Eğriyi Uygula";
        }
    }

    // ========== Performans Profili ==========
    private async void PerfMode_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as ToggleButton;
        BtnPerfQuiet.IsChecked = (btn == BtnPerfQuiet);
        BtnPerfDefault.IsChecked = (btn == BtnPerfDefault);
        BtnPerfPerf.IsChecked = (btn == BtnPerfPerf);

        int profile = 0x30; // Default
        if (btn == BtnPerfQuiet) profile = 0x50; // Quiet
        if (btn == BtnPerfPerf) profile = 0x31; // Performance

        if (App.IpcClient != null)
        {
            await App.IpcClient.SendCommandAsync("SetThermalProfile", profile);
        }
    }

    // ========== Fan Modu ==========
    private async void FanMode_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as ToggleButton;
        SetFanMode(btn);
        
        if (btn == BtnFanManual)
        {
            CustomFanCurveContainer.Visibility = Visibility.Visible;
            // Eğri henüz uygulanmaz, kullanıcı "Uygula" butonuna basmalı
        }
        else if (btn == BtnFanOmenFlow)
        {
            CustomFanCurveContainer.Visibility = Visibility.Collapsed;
            // OmenFlow akıllı fan eğrisini hemen uygula
            await ApplyFanCurveAsync(OmenFlowPresetPoints);
        }
        else if (btn == BtnFanMax)
        {
            CustomFanCurveContainer.Visibility = Visibility.Collapsed;
            if (App.IpcClient != null)
                await App.IpcClient.SendCommandAsync("SetMaxFan");
        }
        else // Auto
        {
            CustomFanCurveContainer.Visibility = Visibility.Collapsed;
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
        BtnMuxHybrid.IsChecked = (btn == BtnMuxHybrid);
        BtnMuxDiscrete.IsChecked = (btn == BtnMuxDiscrete);

        if (App.IpcClient != null)
        {
            int mode = (btn == BtnMuxDiscrete) ? 1 : 2; // Discrete=1, Hybrid=2
            await App.IpcClient.SendCommandAsync("SetGpuMode", mode);
        }
    }

    private async void GpuPower_Click(object sender, RoutedEventArgs e)
    {
        var btn = sender as ToggleButton;
        BtnGpuBase.IsChecked = (btn == BtnGpuBase);
        BtnGpuExtra.IsChecked = (btn == BtnGpuExtra);
        BtnGpuMax.IsChecked = (btn == BtnGpuMax);

        if (App.IpcClient != null)
        {
            int power = 0; // BasePower
            if (btn == BtnGpuExtra) power = 1;
            if (btn == BtnGpuMax) power = 2;
            await App.IpcClient.SendCommandAsync("SetGpuPower", power);
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

