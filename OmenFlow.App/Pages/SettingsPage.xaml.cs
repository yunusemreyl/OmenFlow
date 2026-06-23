using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OmenFlow_App.Pages;

public sealed partial class SettingsPage : Page
{
    private bool _isInitializing = true;

    public SettingsPage()
    {
        this.InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
    {
        bool isStartupEnabled = Helpers.StartupHelper.IsStartupEnabled;
        var localSettings = Helpers.LocalSettings.Values;
        
        bool minimizeToTray = true; // Default
        if (localSettings.TryGetValue("MinimizeToTray", out object val))
        {
            minimizeToTray = (bool)val;
        }
        
        if (!isStartupEnabled)
        {
            ComboStartupMode.SelectedIndex = 2; // Başlatma
        }
        else
        {
            ComboStartupMode.SelectedIndex = minimizeToTray ? 1 : 0;
        }

        if (localSettings.TryGetValue("AppTheme", out object? themeObj))
        {
            string theme = themeObj?.ToString() ?? "";
            foreach (ComboBoxItem item in ComboTheme.Items)
            {
                if (item.Tag?.ToString() == theme)
                {
                    ComboTheme.SelectedItem = item;
                    break;
                }
            }
        }
        else
        {
            ComboTheme.SelectedIndex = 0; // System
        }

        if (localSettings.TryGetValue("AppLanguage", out object? langObj))
        {
            string lang = langObj?.ToString() ?? "";
            foreach (ComboBoxItem item in ComboLanguage.Items)
            {
                if (item.Tag?.ToString() == lang)
                {
                    ComboLanguage.SelectedItem = item;
                    break;
                }
            }
        }
        else
        {
            ComboLanguage.SelectedIndex = 0; // Türkçe (default)
        }

        _isInitializing = false;
    }

    private void ComboStartupMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (ComboStartupMode.SelectedItem is ComboBoxItem item)
        {
            string mode = item.Tag?.ToString() ?? "";
            if (mode == "Disabled")
            {
                Helpers.StartupHelper.IsStartupEnabled = false;
            }
            else
            {
                Helpers.StartupHelper.IsStartupEnabled = true;
                Helpers.LocalSettings.Values["MinimizeToTray"] = (mode == "Tray");
            }
        }
    }

    private void ComboLanguage_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (ComboLanguage.SelectedItem is ComboBoxItem item)
        {
            string lang = item.Tag?.ToString() ?? "";
            Helpers.LocalSettings.Values["AppLanguage"] = lang;
            // Language applying will be implemented later
        }
    }

    private void ComboTheme_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;
        if (ComboTheme.SelectedItem is ComboBoxItem item)
        {
            string theme = item.Tag?.ToString() ?? "";
            Helpers.LocalSettings.Values["AppTheme"] = theme;
            
            // Apply theme immediately
            if (App.Current is App app && app.GetMainWindow()?.Content is FrameworkElement rootElement)
            {
                if (theme == "Light") rootElement.RequestedTheme = ElementTheme.Light;
                else if (theme == "Dark") rootElement.RequestedTheme = ElementTheme.Dark;
                else rootElement.RequestedTheme = ElementTheme.Default;
            }
        }
    }
}
