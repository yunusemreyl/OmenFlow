using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;
using OmenFlow.Core.Interfaces;
using OmenFlow.Core.Models;

namespace OmenFlow.Hardware;

public class EcService : IEcService, IDisposable
{
    private readonly BoardConfiguration _boardConfig;
    private IntPtr _handle = IntPtr.Zero;
    private IntPtr _pawnIOLib = IntPtr.Zero;
    private bool _moduleLoaded;
    private bool _disposed;
    private byte[]? _lpcAcpiEcModule;

    // ACPI EC standard ports
    private const ushort EC_DATA_PORT = 0x62;
    private const ushort EC_CMD_PORT = 0x66;

    // EC commands
    private const byte EC_CMD_READ = 0x80;
    private const byte EC_CMD_WRITE = 0x81;

    // EC status bits
    private const byte EC_STATUS_OBF = 0x01;  // Output Buffer Full
    private const byte EC_STATUS_IBF = 0x02;  // Input Buffer Full

    private const int EC_TIMEOUT_MS = 100;
    private const int EC_POLL_DELAY_US = 10;

    // Function delegates
    private delegate int PawnioOpen(out IntPtr handle);
    private delegate int PawnioLoad(IntPtr handle, byte[] blob, IntPtr size);
    private delegate int PawnioExecute(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] string name, ulong[] input, IntPtr inSize, ulong[] output, IntPtr outSize, out IntPtr returnSize);
    private delegate int PawnioClose(IntPtr handle);

    private PawnioOpen? _pawnioOpen;
    private PawnioLoad? _pawnioLoad;
    private PawnioExecute? _pawnioExecute;
    private PawnioClose? _pawnioClose;

    private static readonly Mutex EcMutex = new(false, @"Global\Access_EC");

    // Safe allowed list of EC registers for fans/power
    private static readonly HashSet<ushort> AllowedWriteAddresses = new()
    {
        0x06, 0x11, 0x12, 0x3A, 0x3B, // Victus 8BBE specific fan control registers
        0x2C, 0x2D, 0x2E, 0x2F, 0x34, 0x35, 0x44, 0x45, 0x46,
        0x4A, 0x4B, 0x4C, 0x4D, 0x62, 0x63, 0x95, 0xB0, 0xB1,
        0xCE, 0xCF, 0xEC, 0xF4, 0x96
    };

    public EcService(BoardConfiguration boardConfig)
    {
        _boardConfig = boardConfig;
        InitializePawnIO();
    }

    private void InitializePawnIO()
    {
        try
        {
            string? libPath = FindPawnIOLibPath();
            if (libPath == null)
            {
                Console.WriteLine("[EC] PawnIOLib.dll not found.");
                return;
            }

            _pawnIOLib = NativeMethods.LoadLibrary(libPath);
            if (_pawnIOLib == IntPtr.Zero)
            {
                Console.WriteLine($"[EC] Failed to load PawnIOLib.dll from {libPath}. Error: {Marshal.GetLastWin32Error()}");
                return;
            }

            if (!ResolveFunctions())
            {
                Console.WriteLine("[EC] Failed to resolve functions from PawnIOLib.dll.");
                return;
            }

            int hr = _pawnioOpen!(out _handle);
            if (hr < 0 || _handle == IntPtr.Zero)
            {
                Console.WriteLine($"[EC] pawnio_open failed with HR {hr:X8}");
                return;
            }

            if (!LoadEcModule())
            {
                Console.WriteLine("[EC] Failed to load LpcACPIEC module.");
                _pawnioClose!(_handle);
                _handle = IntPtr.Zero;
                return;
            }

            _moduleLoaded = true;
            Console.WriteLine("[EC] PawnIO EC access initialized.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[EC] Init error: {ex.Message}");
        }
    }

    private string? FindPawnIOLibPath()
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string bundledLibPath = Path.Combine(appDir, "drivers", "PawnIOLib.dll");
        if (File.Exists(bundledLibPath)) return bundledLibPath;

        string defaultPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO");
        string libPath = Path.Combine(defaultPath, "PawnIOLib.dll");
        if (File.Exists(libPath)) return libPath;
        return null;
    }

    private bool ResolveFunctions()
    {
        IntPtr openPtr = NativeMethods.GetProcAddress(_pawnIOLib, "pawnio_open");
        IntPtr loadPtr = NativeMethods.GetProcAddress(_pawnIOLib, "pawnio_load");
        IntPtr executePtr = NativeMethods.GetProcAddress(_pawnIOLib, "pawnio_execute");
        IntPtr closePtr = NativeMethods.GetProcAddress(_pawnIOLib, "pawnio_close");

        if (openPtr == IntPtr.Zero || loadPtr == IntPtr.Zero || executePtr == IntPtr.Zero || closePtr == IntPtr.Zero)
            return false;

        _pawnioOpen = Marshal.GetDelegateForFunctionPointer<PawnioOpen>(openPtr);
        _pawnioLoad = Marshal.GetDelegateForFunctionPointer<PawnioLoad>(loadPtr);
        _pawnioExecute = Marshal.GetDelegateForFunctionPointer<PawnioExecute>(executePtr);
        _pawnioClose = Marshal.GetDelegateForFunctionPointer<PawnioClose>(closePtr);
        return true;
    }

    private bool LoadEcModule()
    {
        string[] moduleNames = { "LpcACPIEC.bin", "LpcACPIEC.amx" };
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        string pawnIOPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PawnIO");
        
        // 1. Try bundled app directory first
        foreach (var name in moduleNames)
        {
            string modulePath = Path.Combine(appDir, "drivers", name);
            if (File.Exists(modulePath))
            {
                _lpcAcpiEcModule = File.ReadAllBytes(modulePath);
                int hr = _pawnioLoad!(_handle, _lpcAcpiEcModule, (IntPtr)_lpcAcpiEcModule.Length);
                if (hr < 0) Console.WriteLine($"[EC] pawnio_load failed with HR {hr:X8} for {modulePath}");
                return hr >= 0;
            }
        }

        // 2. Try Program Files
        foreach (var name in moduleNames)
        {
            string modulePath = Path.Combine(pawnIOPath, "modules", name);
            if (File.Exists(modulePath))
            {
                _lpcAcpiEcModule = File.ReadAllBytes(modulePath);
                int hr = _pawnioLoad!(_handle, _lpcAcpiEcModule, (IntPtr)_lpcAcpiEcModule.Length);
                if (hr < 0) Console.WriteLine($"[EC] pawnio_load failed with HR {hr:X8} for {modulePath}");
                return hr >= 0;
            }
        }
        
        Console.WriteLine($"[EC] LpcACPIEC module not found in bundled drivers or {Path.Combine(pawnIOPath, "modules")}");
        return false;
    }

    public Task<byte> ReadByteAsync(byte register, CancellationToken cancellationToken = default)
    {
        if (!_moduleLoaded) return Task.FromResult<byte>(0);

        bool gotMutex = false;
        try
        {
            gotMutex = EcMutex.WaitOne(500);
            if (!gotMutex) throw new TimeoutException("EC mutex contention");

            if (!WaitForInputBufferEmpty()) throw new TimeoutException("EC IBF full");
            WritePort(EC_CMD_PORT, EC_CMD_READ);
            if (!WaitForInputBufferEmpty()) throw new TimeoutException("EC IBF full after cmd");
            WritePort(EC_DATA_PORT, register);
            if (!WaitForOutputBufferFull()) throw new TimeoutException("EC OBF empty");
            
            return Task.FromResult(ReadPort(EC_DATA_PORT));
        }
        finally
        {
            if (gotMutex) EcMutex.ReleaseMutex();
        }
    }

    public Task WriteByteAsync(byte register, byte value, CancellationToken cancellationToken = default)
    {
        if (!_moduleLoaded) return Task.CompletedTask;
        if (!AllowedWriteAddresses.Contains(register))
        {
            Console.WriteLine($"[EC] Blocked write to 0x{register:X2}");
            return Task.CompletedTask;
        }

        bool gotMutex = false;
        try
        {
            gotMutex = EcMutex.WaitOne(500);
            if (!gotMutex) throw new TimeoutException("EC mutex contention");

            if (!WaitForInputBufferEmpty()) throw new TimeoutException("EC IBF full");
            WritePort(EC_CMD_PORT, EC_CMD_WRITE);
            if (!WaitForInputBufferEmpty()) throw new TimeoutException("EC IBF full after cmd");
            WritePort(EC_DATA_PORT, register);
            if (!WaitForInputBufferEmpty()) throw new TimeoutException("EC IBF full after addr");
            WritePort(EC_DATA_PORT, value);
            
            Thread.Sleep(1);
        }
        finally
        {
            if (gotMutex) EcMutex.ReleaseMutex();
        }
        return Task.CompletedTask;
    }

    private bool WaitForInputBufferEmpty()
    {
        int start = Environment.TickCount;
        while (Environment.TickCount - start < EC_TIMEOUT_MS)
        {
            if ((ReadPort(EC_CMD_PORT) & EC_STATUS_IBF) == 0) return true;
            Thread.SpinWait(EC_POLL_DELAY_US);
        }
        return false;
    }

    private bool WaitForOutputBufferFull()
    {
        int start = Environment.TickCount;
        while (Environment.TickCount - start < EC_TIMEOUT_MS)
        {
            if ((ReadPort(EC_CMD_PORT) & EC_STATUS_OBF) != 0) return true;
            Thread.SpinWait(EC_POLL_DELAY_US);
        }
        return false;
    }

    private byte ReadPort(ushort port)
    {
        ulong[] input = { port };
        ulong[] output = new ulong[1];
        int hr = _pawnioExecute!(_handle, "ioctl_pio_read", input, (IntPtr)1, output, (IntPtr)1, out _);
        if (hr < 0) throw new InvalidOperationException("PawnIO read failed");
        return (byte)(output[0] & 0xFF);
    }

    private void WritePort(ushort port, byte value)
    {
        ulong[] input = { port, value };
        ulong[] output = Array.Empty<ulong>();
        int hr = _pawnioExecute!(_handle, "ioctl_pio_write", input, (IntPtr)2, output, IntPtr.Zero, out _);
        if (hr < 0) throw new InvalidOperationException("PawnIO write failed");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero && _pawnioClose != null) _pawnioClose(_handle);
        if (_pawnIOLib != IntPtr.Zero) NativeMethods.FreeLibrary(_pawnIOLib);
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern IntPtr LoadLibrary(string lpFileName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool FreeLibrary(IntPtr hModule);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
        public static extern IntPtr GetProcAddress(IntPtr hModule, string procName);
    }
}
