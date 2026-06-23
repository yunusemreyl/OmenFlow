using System;

namespace OmenFlow.Hardware.Lighting;

/// <summary>
/// Base interface for all RGB lighting effects.
/// </summary>
public interface IEffect
{
    /// <summary>
    /// Gets the name of the effect.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Initialize the effect.
    /// </summary>
    void Start();

    /// <summary>
    /// Update the effect state.
    /// </summary>
    /// <param name="deltaTime">Time elapsed since last frame.</param>
    void Update(TimeSpan deltaTime);

    /// <summary>
    /// Get the current RGB frame (zone colors).
    /// </summary>
    /// <returns>Base64 encoded string of 12 bytes representing 4 zones (R,G,B per zone).</returns>
    string GetCurrentFrame();

    /// <summary>
    /// Checks whether the frame has changed since last update.
    /// </summary>
    bool IsDirty { get; }
}
