using Microsoft.Windows.ApplicationModel.Resources;

namespace OmenFlow_App.Helpers;

public static class ResourceHelper
{
    private static readonly ResourceLoader _resourceLoader = new ResourceLoader();

    public static string GetString(string key)
    {
        return _resourceLoader.GetString(key);
    }
}
