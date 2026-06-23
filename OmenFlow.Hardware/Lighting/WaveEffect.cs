using System;

namespace OmenFlow.Hardware.Lighting;

public class WaveEffect : IEffect
{
    public string Name => "Wave";

    private double _elapsedSeconds = 0;
    private double _speed; // Cycles per second (e.g. 0.1 for 10s cycle)
    private double _brightness;

    private string _currentFrame = "";
    private bool _isDirty = true;

    public bool IsDirty => _isDirty;

    public WaveEffect(double speed = 0.5, double brightness = 1.0)
    {
        _speed = speed;
        _brightness = brightness;
    }

    public void Start()
    {
        _elapsedSeconds = 0;
        _isDirty = true;
    }

    public void Update(TimeSpan deltaTime)
    {
        _elapsedSeconds += deltaTime.TotalSeconds;

        byte[] frameBytes = new byte[12];
        for (int i = 0; i < 4; i++)
        {
            double hue = (_elapsedSeconds * _speed + (i * 0.1)) % 1.0;
            if (hue < 0) hue += 1.0;

            var rgb = HsvToRgb(hue, 1.0, _brightness);
            
            frameBytes[i * 3 + 0] = rgb.r;
            frameBytes[i * 3 + 1] = rgb.g;
            frameBytes[i * 3 + 2] = rgb.b;
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

    private (byte r, byte g, byte b) HsvToRgb(double h, double s, double v)
    {
        int hi = Convert.ToInt32(Math.Floor(h * 6)) % 6;
        double f = h * 6 - Math.Floor(h * 6);

        v = v * 255;
        byte vByte = (byte)Convert.ToInt32(v);
        byte p = (byte)Convert.ToInt32(v * (1 - s));
        byte q = (byte)Convert.ToInt32(v * (1 - f * s));
        byte t = (byte)Convert.ToInt32(v * (1 - (1 - f) * s));

        return hi switch
        {
            0 => (vByte, t, p),
            1 => (q, vByte, p),
            2 => (p, vByte, t),
            3 => (p, q, vByte),
            4 => (t, p, vByte),
            _ => (vByte, p, q)
        };
    }
}
