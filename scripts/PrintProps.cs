using System;
using System.IO;
using System.Reflection;
using LibreHardwareMonitor.Hardware;

class Program {
    static void Main() {
        var t = typeof(Computer);
        var pNames = new System.Collections.Generic.List<string>();
        foreach (var p in t.GetProperties()) {
            pNames.Add(p.Name);
        }
        File.WriteAllLines(""props.txt"", pNames);
    }
}
