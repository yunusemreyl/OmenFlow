using System.Collections.Generic;
using OmenFlow.Core.Models;

namespace OmenFlow.Core.Interfaces;

public interface IPresetService
{
    /// <summary>
    /// Returns all presets (built-in + user-defined), ordered: built-in first.
    /// </summary>
    IReadOnlyList<FanPreset> GetAll();
    
    /// <summary>
    /// Saves or updates a user-defined preset (cannot save over built-in).
    /// </summary>
    void Save(FanPreset preset);
    
    /// <summary>
    /// Deletes a user-defined preset by Id. Throws if IsBuiltIn.
    /// </summary>
    void Delete(string id);
}
