using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace OmenFlow_App;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    
    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public static Helpers.IpcClient IpcClient { get; } = new Helpers.IpcClient();

    public App()
    {
        // Apply saved language before components initialize
        if (Helpers.LocalSettings.Values.TryGetValue("AppLanguage", out object? langObj))
        {
            string lang = langObj?.ToString() ?? "";
            if (!string.IsNullOrEmpty(lang))
            {
                Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = lang;
            }
        }

        this.InitializeComponent();

        // Global Exception Handlers
        this.UnhandledException += (s, e) =>
        {
            OmenFlow.Core.Services.Logger.LogError("[App] Unhandled XAML Exception", e.Exception);
            e.Handled = true; // Attempt to prevent instant crash
        };

        System.AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.ExceptionObject is System.Exception ex)
            {
                OmenFlow.Core.Services.Logger.LogError("[App] AppDomain Unhandled Exception", ex);
            }
        };

        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            OmenFlow.Core.Services.Logger.LogError("[App] Unobserved Task Exception", e.Exception);
            e.SetObserved();
        };

        IpcClient.Connect();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        _window = new MainWindow();
        _window.Activate();
    }

    public Window GetMainWindow()
    {
        return _window;
    }

    public void ReloadLanguage(string lang, string? initialPage = null)
    {
        Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = lang;
        try
        {
            Windows.ApplicationModel.Resources.Core.ResourceContext.GetForViewIndependentUse().Reset();
        }
        catch (System.Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogError("[App] ResourceContext Reset Failed", ex);
        }
        
        var oldWindow = _window;
        _window = new MainWindow(initialPage);
        _window.Activate();
        oldWindow?.Close();
    }
}
