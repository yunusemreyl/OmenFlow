using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmenFlow_App.Pages;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace OmenFlow_App;

public sealed partial class MainWindow : Window
{
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    public MainWindow(string? initialPage = null)
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        AppWindow.SetIcon("icons/omenflowicon.png");

        IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double dpiScale = GetDpiForWindow(hWnd) / 96.0;

        int width = (int)(880 * dpiScale);
        int height = (int)(620 * dpiScale);
        AppWindow.Resize(new Windows.Graphics.SizeInt32(width, height));

        var displayArea = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        // Center the window on the screen
        int x = workArea.X + (workArea.Width - width) / 2;
        int y = workArea.Y + (workArea.Height - height) / 2;
        AppWindow.Move(new Windows.Graphics.PointInt32(x, y));

        if (initialPage == "settings")
        {
            NavView.Loaded += (s, e) =>
            {
                NavView.SelectedItem = NavView.SettingsItem;
                NavFrame.Navigate(typeof(SettingsPage));
            };
        }
        else if (initialPage == "graphics_switcher")
        {
            NavView.Loaded += (s, e) =>
            {
                foreach (var item in NavView.MenuItems)
                {
                    if (item is NavigationViewItem navItem && navItem.Tag?.ToString() == "graphics_switcher")
                    {
                        NavView.SelectedItem = navItem;
                        NavFrame.Navigate(typeof(GraphicsSwitcherPage));
                        break;
                    }
                }
            };
        }
        else if (initialPage == "lighting")
        {
            NavView.Loaded += (s, e) => NavigateToLighting();
        }
        else
        {
            NavFrame.Navigate(typeof(PerformancePage));
        }

        if (App.IpcClient != null)
        {
            App.IpcClient.Connected += (s, e) => DispatcherQueue.TryEnqueue(() => BackendConnectionInfoBar.IsOpen = false);
            App.IpcClient.Disconnected += (s, e) => DispatcherQueue.TryEnqueue(() => BackendConnectionInfoBar.IsOpen = true);
        }

        this.SizeChanged += MainWindow_SizeChanged;
        
        // Apply saved theme
        if (Helpers.LocalSettings.Values.TryGetValue("AppTheme", out object? themeObj))
        {
            string theme = themeObj?.ToString() ?? "";
            if (this.Content is FrameworkElement rootElement)
            {
                if (theme == "Light") rootElement.RequestedTheme = ElementTheme.Light;
                else if (theme == "Dark") rootElement.RequestedTheme = ElementTheme.Dark;
                else rootElement.RequestedTheme = ElementTheme.Default;
            }
        }
    }

    private void MainWindow_SizeChanged(object sender, WindowSizeChangedEventArgs args)
    {
        IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        double dpiScale = GetDpiForWindow(hWnd) / 96.0;
        int minWidth = (int)(800 * dpiScale);
        int minHeight = (int)(550 * dpiScale);

        if (AppWindow.Size.Width < minWidth || AppWindow.Size.Height < minHeight)
        {
            int newWidth = Math.Max(AppWindow.Size.Width, minWidth);
            int newHeight = Math.Max(AppWindow.Size.Height, minHeight);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(newWidth, newHeight));
        }
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        NavFrame.GoBack();
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
        }
        else if (args.SelectedItem is NavigationViewItem item)
        {
            switch (item.Tag)
            {
                case "performance":
                    NavFrame.Navigate(typeof(PerformancePage));
                    break;
                case "graphics_switcher":
                    NavFrame.Navigate(typeof(GraphicsSwitcherPage));
                    break;
                case "lighting":
                    NavFrame.Navigate(typeof(LightingPage));
                    break;
            }
        }
    }

    public void NavigateToPerformance(string parameter)
    {
        foreach (var item in NavView.MenuItems)
        {
            if (item is NavigationViewItem navItem && navItem.Tag?.ToString() == "performance")
            {
                NavView.SelectedItem = navItem;
                NavFrame.Navigate(typeof(PerformancePage), parameter);
                break;
            }
        }
    }

    public void NavigateToLighting()
    {
        foreach (var item in NavView.MenuItems)
        {
            if (item is NavigationViewItem navItem && navItem.Tag?.ToString() == "lighting")
            {
                NavView.SelectedItem = navItem;
                NavFrame.Navigate(typeof(LightingPage));
                break;
            }
        }
    }
}
