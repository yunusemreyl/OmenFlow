using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OmenFlow_App.Pages;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace OmenFlow_App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("icons/omenflowicon.png");
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
                case "home":
                    NavFrame.Navigate(typeof(HomePage));
                    break;
                case "performance":
                    NavFrame.Navigate(typeof(PerformancePage));
                    break;
                case "lighting":
                    NavFrame.Navigate(typeof(LightingPage));
                    break;

                case "advanced":
                    NavFrame.Navigate(typeof(AdditionalSettingsPage));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown navigation item tag: {item.Tag}");
            }
        }
    }

    public void NavigateToPerformance(string parameter)
    {
        // Find the Performance item and select it
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

    public void NavigateToAdvancedSettings()
    {
        foreach (var item in NavView.MenuItems)
        {
            if (item is NavigationViewItem navItem && navItem.Tag?.ToString() == "advanced")
            {
                NavView.SelectedItem = navItem;
                NavFrame.Navigate(typeof(AdditionalSettingsPage));
                break;
            }
        }
    }

    public void NavigateToPage(Type pageType)
    {
        NavView.SelectedItem = null; // Unselect sidebar items if any
        NavFrame.Navigate(pageType);
    }
}
