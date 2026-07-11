using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using OmenFlow_App.Pages;

namespace OmenFlow_App.SubWindows;

public sealed partial class CustomFanWindow : Window
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private List<Ellipse> _fanCurvePoints = new List<Ellipse>();
    private Ellipse? _draggingPoint = null;

    public CustomFanWindow()
    {
        this.InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        AppWindow.SetIcon("icons/omenflowicon.png");

        IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double dpiScale = GetDpiForWindow(hWnd) / 96.0;

        int customWidth = (int)(410 * dpiScale);
        int customHeight = (int)(440 * dpiScale);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(customWidth, customHeight));

        if (Application.Current is App app && app.GetMainWindow() is Window mainWindow)
        {
            var mainPos = mainWindow.AppWindow.Position;
            int x = mainPos.X - customWidth - (int)(8 * dpiScale);
            int y = mainPos.Y + mainWindow.AppWindow.Size.Height - customHeight;
            AppWindow.Move(new Windows.Graphics.PointInt32(x, y));
        }

        InitializeFanCurve();
        this.SizeChanged += CustomFanWindow_SizeChanged;
    }

    private void CustomFanWindow_SizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double dpiScale = GetDpiForWindow(hWnd) / 96.0;
        int minWidth = (int)(410 * dpiScale);
        int minHeight = (int)(420 * dpiScale);

        if (AppWindow.Size.Width < minWidth || AppWindow.Size.Height < minHeight)
        {
            int newWidth = Math.Max(AppWindow.Size.Width, minWidth);
            int newHeight = Math.Max(AppWindow.Size.Height, minHeight);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(newWidth, newHeight));
        }
    }

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

            double x = (defaultTemps[i] / 100.0) * 300;
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
            double minX = index > 0 ? Canvas.GetLeft(_fanCurvePoints[index - 1]) + 10 : 0;
            double maxX = index < _fanCurvePoints.Count - 1 ? Canvas.GetLeft(_fanCurvePoints[index + 1]) - 10 : 300;
            
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

    private List<FanCurvePointDto> GetCustomCurveFromCanvas()
    {
        var result = new List<FanCurvePointDto>();
        for (int i = 0; i < _fanCurvePoints.Count; i++)
        {
            var ellipse = _fanCurvePoints[i];
            double x = Canvas.GetLeft(ellipse) + 8; // merkeze göre
            double y = Canvas.GetTop(ellipse) + 8;

            int tempC = (int)Math.Round((x / 300.0) * 100.0);
            int speedPercent = (int)Math.Round(((200.0 - y) / 200.0) * 100.0);

            tempC = Math.Clamp(tempC, 0, 100);
            speedPercent = Math.Clamp(speedPercent, 0, 100);

            result.Add(new FanCurvePointDto { TemperatureCelsius = tempC, FanSpeedPercent = speedPercent });
        }
        return result;
    }

    private async void ApplyCurveButton_Click(object sender, RoutedEventArgs e)
    {
        var points = GetCustomCurveFromCanvas();
        bool sent = false;
        if (App.IpcClient != null)
        {
            var curvePayload = new
            {
                Target = 2, // Both (CPU + GPU)
                Points = points
            };
            sent = await App.IpcClient.SendCommandAsync("ApplyCurve", curvePayload);
        }

        if (sent && ApplyCurveButton != null)
        {
            ApplyCurveButton.Content = "✓ Uygulandı!";
            await Task.Delay(500);
            this.Close();
        }
        else if (!sent && ApplyCurveButton != null)
        {
            ApplyCurveButton.Content = "Uygulanamadı";
        }
    }
}
