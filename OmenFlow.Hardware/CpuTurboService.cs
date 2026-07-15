using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using OmenFlow.Core.Interfaces;
using OmenFlow.Core.Services;

namespace OmenFlow.Hardware;

public class CpuTurboService : ICpuTurboService
{
    private static readonly Guid GUID_PROCESSOR_SETTINGS_SUBGROUP = new("54533251-82be-4824-96c1-47b60b740d00");
    private static readonly Guid GUID_PROCESSOR_PERF_BOOST_MODE = new("be337238-0d82-4146-a960-4f3749d470c7");

    [DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
    private static extern uint PowerReadACValueIndex(
        IntPtr RootPowerKey,
        [MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid,
        [MarshalAs(UnmanagedType.LPStruct)] Guid SubGroupOfPowerSettingsGuid,
        [MarshalAs(UnmanagedType.LPStruct)] Guid PowerSettingGuid,
        out uint AcValueIndex);

    [DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
    private static extern uint PowerWriteACValueIndex(
        IntPtr RootPowerKey,
        [MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid,
        [MarshalAs(UnmanagedType.LPStruct)] Guid SubGroupOfPowerSettingsGuid,
        [MarshalAs(UnmanagedType.LPStruct)] Guid PowerSettingGuid,
        uint AcValueIndex);

    [DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
    private static extern uint PowerWriteDCValueIndex(
        IntPtr RootPowerKey,
        [MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid,
        [MarshalAs(UnmanagedType.LPStruct)] Guid SubGroupOfPowerSettingsGuid,
        [MarshalAs(UnmanagedType.LPStruct)] Guid PowerSettingGuid,
        uint DcValueIndex);

    [DllImport("PowrProf.dll")]
    private static extern uint PowerGetActiveScheme(
        IntPtr UserRootPowerKey,
        out IntPtr ActivePolicyGuid);

    [DllImport("PowrProf.dll", CharSet = CharSet.Unicode)]
    private static extern uint PowerSetActiveScheme(
        IntPtr UserRootPowerKey,
        [MarshalAs(UnmanagedType.LPStruct)] Guid SchemeGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    public Task<bool> IsTurboEnabledAsync(CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                Guid activeScheme = GetActiveSchemeGuid();
                uint result = PowerReadACValueIndex(
                    IntPtr.Zero,
                    activeScheme,
                    GUID_PROCESSOR_SETTINGS_SUBGROUP,
                    GUID_PROCESSOR_PERF_BOOST_MODE,
                    out uint acValueIndex);

                if (result == 0)
                {
                    // 0 = Disabled, > 0 = Enabled (usually 2 for Aggressive)
                    return acValueIndex > 0;
                }
                
                Logger.LogInfo($"[CpuTurboService] Failed to read AC value index. Error: {result}");
            }
            catch (Exception ex)
            {
                Logger.LogInfo($"[CpuTurboService] Exception in IsTurboEnabledAsync: {ex.Message}");
            }
            
            return true; // Default to true if unable to read
        }, cancellationToken);
    }

    public Task<bool> SetTurboEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            try
            {
                Guid activeScheme = GetActiveSchemeGuid();
                uint value = enabled ? 2u : 0u; // 2 = Aggressive, 0 = Disabled

                uint resultAc = PowerWriteACValueIndex(
                    IntPtr.Zero,
                    activeScheme,
                    GUID_PROCESSOR_SETTINGS_SUBGROUP,
                    GUID_PROCESSOR_PERF_BOOST_MODE,
                    value);

                uint resultDc = PowerWriteDCValueIndex(
                    IntPtr.Zero,
                    activeScheme,
                    GUID_PROCESSOR_SETTINGS_SUBGROUP,
                    GUID_PROCESSOR_PERF_BOOST_MODE,
                    value);

                if (resultAc == 0 && resultDc == 0)
                {
                    uint setActiveResult = PowerSetActiveScheme(IntPtr.Zero, activeScheme);
                    if (setActiveResult == 0)
                    {
                        Logger.LogInfo($"[CpuTurboService] CPU Turbo successfully set to: {(enabled ? "Enabled" : "Disabled")}");
                        return true;
                    }
                    Logger.LogInfo($"[CpuTurboService] Failed to set active scheme. Error: {setActiveResult}");
                }
                else
                {
                    Logger.LogInfo($"[CpuTurboService] Failed to write power value. AC: {resultAc}, DC: {resultDc}");
                }
            }
            catch (Exception ex)
            {
                Logger.LogInfo($"[CpuTurboService] Exception in SetTurboEnabledAsync: {ex.Message}");
            }

            return false;
        }, cancellationToken);
    }

    private Guid GetActiveSchemeGuid()
    {
        IntPtr pActiveSchemeGuid = IntPtr.Zero;
        uint result = PowerGetActiveScheme(IntPtr.Zero, out pActiveSchemeGuid);

        if (result == 0 && pActiveSchemeGuid != IntPtr.Zero)
        {
            try
            {
                return Marshal.PtrToStructure<Guid>(pActiveSchemeGuid);
            }
            finally
            {
                LocalFree(pActiveSchemeGuid);
            }
        }
        
        // Fallback to Balanced scheme if unable to read active
        return new Guid("381b4222-f694-41f0-9685-ff5bb260df2e"); 
    }
}
