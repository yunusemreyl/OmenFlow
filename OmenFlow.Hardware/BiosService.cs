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
    private CimInstance? _biosData;
    private CimInstance? _biosMethods;

    public BiosService()
    {
        _requestChannel = Channel.CreateUnbounded<BiosRequest>();
        _cts = new CancellationTokenSource();

        _dispatchLoopTask = Task.Factory.StartNew(
            DispatchLoop,
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
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
        CimSession? cimSession = null;
        try
        {
            try
            {
                cimSession = CimSession.Create(null);

                _biosData = new CimInstance(cimSession.GetClass(@"root\WMI", "hpqBDataIn"));
                _biosData.CimInstanceProperties["Sign"].Value = new byte[] { 0x53, 0x45, 0x43, 0x55 };

                _biosMethods = new CimInstance("hpqBIntM", @"root\WMI");
                _biosMethods.CimInstanceProperties.Add(CimProperty.Create("InstanceName", "ACPI\\PNP0C14\\0_0", CimFlags.Key));
                _biosMethods = cimSession.GetInstance(@"root\WMI", _biosMethods);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] BiosService initialization failed: {ex.Message}");
                // Fail all future requests
                await foreach (var request in _requestChannel.Reader.ReadAllAsync(_cts.Token))
                {
                    request.Tcs.TrySetException(new InvalidOperationException("BIOS Service failed to initialize.", ex));
                }
                return;
            }

            await foreach (var request in _requestChannel.Reader.ReadAllAsync(_cts.Token))
            {
                if (request.CancellationToken.IsCancellationRequested)
                {
                    request.Tcs.TrySetCanceled(request.CancellationToken);
                    continue;
                }

                try
                {
                    using var input = new CimInstance(_biosData);
                    input.CimInstanceProperties["Command"].Value = request.CommandType;
                    input.CimInstanceProperties["CommandType"].Value = (uint)request.Command;
                    input.CimInstanceProperties["hpqBData"].Value = request.InData;
                    input.CimInstanceProperties["Size"].Value = (uint)request.InData.Length;

                    var methodParameters = new CimMethodParametersCollection
                    {
                        CimMethodParameter.Create("InData", input, CimType.Instance, CimFlags.In)
                    };

                    string methodName;
                    if (request.OutSize > 1024) methodName = "hpqBIOSInt4096";
                    else if (request.OutSize > 128) methodName = "hpqBIOSInt1024";
                    else if (request.OutSize > 4) methodName = "hpqBIOSInt128";
                    else methodName = "hpqBIOSInt4";

                    var result = cimSession.InvokeMethod(@"root\WMI", _biosMethods, methodName, methodParameters);
                    
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
        finally
        {
            cimSession?.Dispose();
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _requestChannel.Writer.TryComplete();
        _cts.Dispose();
        _biosData?.Dispose();
        _biosMethods?.Dispose();
    }

    private record BiosRequest(
        uint CommandType,
        byte Command, 
        byte[] InData, 
        int OutSize, 
        TaskCompletionSource<(int, byte[])> Tcs, 
        CancellationToken CancellationToken);
}
