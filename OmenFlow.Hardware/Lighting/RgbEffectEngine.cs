using System;
using System.Threading;
using System.Threading.Tasks;

namespace OmenFlow.Hardware.Lighting;

public class RgbEffectEngine : IDisposable
{
    private readonly KeyboardLightingService _lightingService;
    private IEffect? _currentEffect;
    
    private CancellationTokenSource? _cts;
    private Task? _renderTask;

    private readonly int _targetFps = 15; // Kept relatively low to prevent WMI SMI stuttering on laptops.
    private readonly object _lock = new object();

    public RgbEffectEngine(KeyboardLightingService lightingService)
    {
        _lightingService = lightingService;
    }

    public void SetEffect(IEffect effect)
    {
        lock (_lock)
        {
            _currentEffect = effect;
            _currentEffect.Start();
        }

        EnsureRenderTaskStarted();
    }

    public void Stop()
    {
        _cts?.Cancel();
        _renderTask = null;
    }

    private void EnsureRenderTaskStarted()
    {
        if (_renderTask != null && !_renderTask.IsCompleted) return;

        _cts = new CancellationTokenSource();
        _renderTask = Task.Run(() => RenderLoop(_cts.Token));
    }

    private async Task RenderLoop(CancellationToken token)
    {
        DateTime lastTime = DateTime.UtcNow;
        TimeSpan frameTime = TimeSpan.FromMilliseconds(1000.0 / _targetFps);

        while (!token.IsCancellationRequested)
        {
            DateTime now = DateTime.UtcNow;
            TimeSpan delta = now - lastTime;
            lastTime = now;

            string? frameToRender = null;

            lock (_lock)
            {
                if (_currentEffect != null)
                {
                    _currentEffect.Update(delta);
                    if (_currentEffect.IsDirty)
                    {
                        frameToRender = _currentEffect.GetCurrentFrame();
                    }
                }
            }

            if (frameToRender != null)
            {
                // Send frame to hardware
                await _lightingService.SetLightingAsync(true, frameToRender, token);
            }

            // Sleep until next frame
            TimeSpan elapsed = DateTime.UtcNow - now;
            TimeSpan sleepTime = frameTime - elapsed;
            
            if (sleepTime > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(sleepTime, token);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }
    }

    public void Dispose()
    {
        Stop();
    }
}
