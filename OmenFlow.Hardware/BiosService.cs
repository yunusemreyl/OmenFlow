using System;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Management.Infrastructure;
using OmenFlow.Core.Interfaces;

namespace OmenFlow.Hardware;

public class BiosService : IBiosService, IDisposable
{
    private readonly Channel<BiosRequest> _requestChannel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _dispatchLoopTask;
    private readonly Timer _heartbeatTimer;
    private bool _isMuxChangePending = false;

    public BiosService()
    {
        _requestChannel = Channel.CreateUnbounded<BiosRequest>();
        _cts = new CancellationTokenSource();

        _dispatchLoopTask = Task.Factory.StartNew(
            DispatchLoop,
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // 2023+ Omen/Victus cihazlarÄ±n WMI komutlarÄ±nÄ± kilitlemesini engellemek iÃ§in 60 saniyelik Heartbeat
        _heartbeatTimer = new Timer(
            _ => _ = SendCommandAsync(0x20008, 0x10, new byte[4], 4, CancellationToken.None),
            null,
            60_000,
            60_000);
    }

    public Task<(int ReturnCode, byte[] OutData)> SendCommandAsync(uint commandType, byte command, byte[] inData, int outSize, CancellationToken cancellationToken = default)
    {
        var tcs = new TaskCompletionSource<(int, byte[])>(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new BiosRequest(commandType, command, inData, outSize, tcs, cancellationToken);
        
        if (!_requestChannel.Writer.TryWrite(request))
        {
            tcs.SetException(new InvalidOperationException("Failed to enqueue BIOS request."));
        }

        return tcs.Task;
    }

    private async Task DispatchLoop()
    {
        try
        {
            await foreach (var request in _requestChannel.Reader.ReadAllAsync(_cts.Token))
            {
                if (request.CancellationToken.IsCancellationRequested)
                {
                    request.Tcs.TrySetCanceled(request.CancellationToken);
                    continue;
                }

                if (_isMuxChangePending)
                {
                    // Prevent any further WMI calls from overwriting the ACPI buffer where the MUX switch command is stored
                    request.Tcs.TrySetResult((-1, Array.Empty<byte>()));
                    continue;
                }

                try
                {
                    using var cimSession = CimSession.Create(null);
                    using var biosDataClass = cimSession.GetClass(@"root\WMI", "hpqBDataIn");
                    using var input = new CimInstance(biosDataClass);
                    input.CimInstanceProperties["Sign"].Value = new byte[] { 0x53, 0x45, 0x43, 0x55 };
                    input.CimInstanceProperties["Command"].Value = request.CommandType;
                    input.CimInstanceProperties["CommandType"].Value = (uint)request.Command;
                    
                    if (request.InData == null || request.InData.Length == 0)
                    {
                        input.CimInstanceProperties["Size"].Value = (uint)0;
                    }
                    else
                    {
                        input.CimInstanceProperties["hpqBData"].Value = request.InData;
                        input.CimInstanceProperties["Size"].Value = (uint)request.InData.Length;
                    }

                    using var biosMethods = cimSession.EnumerateInstances(@"root\WMI", "hpqBIntM").FirstOrDefault();
                    if (biosMethods == null)
                    {
                        throw new InvalidOperationException("hpqBIntM WMI instance not found.");
                    }

                    using var methodParameters = new CimMethodParametersCollection
                    {
                        CimMethodParameter.Create("InData", input, CimType.Instance, CimFlags.In)
                    };

                    int maxLen = Math.Max(request.OutSize, request.InData.Length);
                    string methodName;
                    if (maxLen > 1024) methodName = "hpqBIOSInt4096";
                    else if (maxLen > 128) methodName = "hpqBIOSInt1024";
                    else if (maxLen > 4) methodName = "hpqBIOSInt128";
                    else methodName = "hpqBIOSInt4";

                    using var result = cimSession.InvokeMethod(@"root\WMI", biosMethods, methodName, methodParameters);
                    
                    using var outDataObj = (CimInstance)result.OutParameters["OutData"].Value;
                    int returnCode = Convert.ToInt32(outDataObj.CimInstanceProperties["rwReturnCode"].Value);
                    
                    byte[] outData = Array.Empty<byte>();
                    if (request.OutSize > 0)
                    {
                        if (outDataObj.CimInstanceProperties["Data"].Value is byte[] bytes)
                        {
                            outData = bytes;
                        }
                    }

                    if (request.Command == 0x52 && request.InData.Length == 4 && request.InData[1] == 0x00 && returnCode == 0)
                    {
                        OmenFlow.Core.Services.Logger.LogInfo("[BiosService] MUX mode change command (0x52) successful. Locking WMI buffer until reboot.");
                        _isMuxChangePending = true;
                    }

                    request.Tcs.TrySetResult((returnCode, outData));
                }
                catch (Exception ex)
                {
                    request.Tcs.TrySetException(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
    }

    public void Dispose()
    {
        _heartbeatTimer?.Dispose();
        _cts.Cancel();
        _requestChannel.Writer.TryComplete();
        _cts.Dispose();
    }

    public async Task<(int cpuTemp, int gpuTemp)?> GetBothTemperaturesAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // CPU Temp: Command 0x23, Payload 0x01
            var cpuTempTask = SendCommandAsync(0x20008, 0x23, new byte[] { 0x01, 0x00, 0x00, 0x00 }, 4, cancellationToken);
            
            // GPU Temp: Command 0x23, Payload 0x02
            var gpuTempTask = SendCommandAsync(0x20008, 0x23, new byte[] { 0x02, 0x00, 0x00, 0x00 }, 4, cancellationToken);
            
            await Task.WhenAll(cpuTempTask, gpuTempTask);
            
            var cpuRes = cpuTempTask.Result;
            var gpuRes = gpuTempTask.Result;
            
            int cpuTemp = 0, gpuTemp = 0;
            if (cpuRes.ReturnCode == 0 && cpuRes.OutData.Length > 0) cpuTemp = cpuRes.OutData[0];
            if (gpuRes.ReturnCode == 0 && gpuRes.OutData.Length > 0) gpuTemp = gpuRes.OutData[0];
            
            if (cpuTemp > 0 || gpuTemp > 0)
            {
                return (cpuTemp, gpuTemp);
            }
        }
        catch (Exception ex)
        {
            OmenFlow.Core.Services.Logger.LogInfo($"[BiosService] GetBothTemperaturesAsync error: {ex.Message}");
        }
        
        return null;
    }

    private record BiosRequest(
        uint CommandType,
        byte Command, 
        byte[] InData, 
        int OutSize, 
        TaskCompletionSource<(int, byte[])> Tcs, 
        CancellationToken CancellationToken);
}

