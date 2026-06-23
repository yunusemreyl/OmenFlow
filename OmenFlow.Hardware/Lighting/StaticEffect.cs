using System;

namespace OmenFlow.Hardware.Lighting;

public class StaticEffect : IEffect
{
    public string Name => "Static";

    private string _colorBase64;
    private bool _isDirty = true;

    public bool IsDirty => _isDirty;

    public StaticEffect(string base64Color)
    {
        _colorBase64 = base64Color;
    }

    public void Start()
    {
        _isDirty = true;
    }

    public void Update(TimeSpan deltaTime)
    {
        // Static effect does not change over time.
    }

    public string GetCurrentFrame()
    {
        _isDirty = false;
        return _colorBase64;
    }

    public void SetColor(string base64Color)
    {
        _colorBase64 = base64Color;
        _isDirty = true;
    }
}
