using System;
using System.Linq;
using System.IO;
using Microsoft.Management.Infrastructure;

class Program {
    static void Main() {
        try {
            using var cimSession = CimSession.Create(null);
            using var biosDataClass = cimSession.GetClass(@"root\WMI", "hpqBDataIn");
            using var input = new CimInstance(biosDataClass);
            input.CimInstanceProperties["Sign"].Value = new byte[] { 0x53, 0x45, 0x43, 0x55 };
            input.CimInstanceProperties["Command"].Value = (uint)0x00002;
            input.CimInstanceProperties["CommandType"].Value = (uint)0x52;
            // Write mode 0 (Hybrid)
            input.CimInstanceProperties["hpqBData"].Value = new byte[] { 0x00, 0x00, 0x00, 0x00 };
            input.CimInstanceProperties["Size"].Value = (uint)4;

            using var biosMethods = cimSession.EnumerateInstances(@"root\WMI", "hpqBIntM").FirstOrDefault();
            
            using var methodParameters = new CimMethodParametersCollection {
                CimMethodParameter.Create("InData", input, CimType.Instance, CimFlags.In)
            };

            using var result = cimSession.InvokeMethod(@"root\WMI", biosMethods, "hpqBIOSInt4", methodParameters);
            using var outDataObj = (CimInstance)result.OutParameters["OutData"].Value;
            int returnCode = Convert.ToInt32(outDataObj.CimInstanceProperties["rwReturnCode"].Value);
            File.WriteAllText("C:\Users\yeyil\Documents\GitHub\OmenFlow\wmi_test3.txt", $"write ret={returnCode}");
        } catch (Exception ex) {}
    }
}
