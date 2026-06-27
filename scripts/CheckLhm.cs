using System;
using System.Reflection;
using LibreHardwareMonitor.Hardware;

class Program {
    static void Main() {
        var t = typeof(Computer);
        foreach (var p in t.GetProperties()) {
            Console.WriteLine(p.Name);
        }
    }
}
