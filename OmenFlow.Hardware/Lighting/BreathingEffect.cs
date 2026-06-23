using System;

namespace OmenFlow.Hardware.Lighting;

public class BreathingEffect : IEffect
{
    public string Name => "Breathing";

    private byte[] _baseColor;
    private double _elapsedSeconds = 0;
    private double _speed; // cycles per second

    private string _currentFrame = "";
    private bool _isDirty = true;

    public bool IsDirty => _isDirty;

    public BreathingEffect(byte r, byte g, byte b, double speed = 0.5)
    {
        _baseColor = new byte[] { r, g, b };
        _speed = speed;
    }

    public void Start()
    {
        _elapsedSeconds = 0;
        _isDirty = true;
    }

    public void Update(TimeSpan deltaTime)
    {
        _elapsedSeconds += deltaTime.TotalSeconds;

        // Calculate intensity based on a sine wave mapping [-1, 1] to [0, 1]
        double intensity = (Math.Sin(_elapsedSeconds * Math.PI * 2 * _speed) + 1.0) / 2.0;
        
        byte r = (byte)(_baseColor[0] * intensity);
        byte g = (byte)(_baseColor[1] * intensity);
        byte b = (byte)(_baseColor[2] * intensity);

        byte[] frameBytes = new byte[12];
        for (int i = 0; i < 4; i++)
        {
            frameBytes[i * 3 + 0] = r;
            frameBytes[i * 3 + 1] = g;
            frameBytes[i * 3 + 2] = b;
        }

        string newFrame = Convert.ToBase64String(frameBytes);
        if (_currentFrame != newFrame)
        {
            _currentFrame = newFrame;
            _isDirty = true;
        }
    }

    public string GetCurrentFrame()
    {
        _isDirty = false;
        return _currentFrame;
    }
}
