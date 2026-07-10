using System;
using System.Reflection;
using LibreHardwareMonitor.Hardware;

class Program {
    static void Main() {
        var t = typeof(Computer);
        foreach (var p in t.GetProperties()) {
            OmenFlow.Core.Services.Logger.LogInfo(p.Name);
        }
    }
}

