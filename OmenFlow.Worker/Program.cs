using System;
using System.Threading;
using System.Threading.Tasks;

using OmenFlow.Core.Models;
using OmenFlow.Hardware;
using OmenFlow.Hardware.Lighting;

namespace OmenFlow.Worker;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("OmenFlow Worker Process starting...");
        
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            Console.WriteLine("Shutdown requested via Ctrl+C...");
            cts.Cancel();
        };

        try
        {
            using var sensorReader = new SensorReader();
            
            // Initialize Hardware Services
            var capabilityService = new CapabilityDetectionService();
            var boardConfig = capabilityService.DetectBoard();

            using var biosService      = new BiosService();
            using var ecService        = new EcService(boardConfig);
            using var fanControlService  = new FanControlService(biosService, boardConfig, ecService);
            using var fanCurveService    = new FanCurveHostedService(fanControlService);
            var gpuControlService        = new GpuControlService(biosService);
            var lightingService          = new KeyboardLightingService(biosService);
            using var rgbEffectEngine    = new RgbEffectEngine(lightingService);
            using var perfModeService    = new PerformanceModeService(biosService, ecService, boardConfig, gpuControlService);
            var powerService             = new PowerService(biosService);
            var presetService            = new PresetService();

            var fanCurveTask = fanCurveService.StartAsync(cts.Token);

            var ipcServer = new IpcServer(
                sensorReader,
                fanControlService,
                fanCurveService,
                gpuControlService,
                lightingService,
                rgbEffectEngine,
                perfModeService,
                powerService,
                presetService);

            Console.WriteLine("Hardware Services initialized. Starting IpcServer...");
            await ipcServer.RunAsync(cts.Token);
            
            await fanCurveTask;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[FATAL ERROR] Worker crashed: {ex}");
        }

        Console.WriteLine("Worker Process exited cleanly.");
    }
}
