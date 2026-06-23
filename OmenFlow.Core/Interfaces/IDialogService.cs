using System.Threading.Tasks;

namespace OmenFlow.Core.Interfaces;

public interface IDialogService
{
    /// <summary>
    /// Prompts the user for a simple string input (e.g. preset name) via a dialog.
    /// </summary>
    Task<string?> AskStringAsync(string title, string prompt);
}
