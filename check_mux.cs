using System;
using System.Linq;
using Microsoft.Management.Infrastructure;
using Microsoft.Management.Infrastructure.Options;
namespace GpuCheck {
    class Program {
        static void Main() {
            try {
                using var cimSession = CimSession.Create(null);
                using var biosDataClass = cimSession.GetClass(@"root\WMI", "hpqBDataIn");
                using var input = new CimInstance(biosDataClass);
                input.CimInstanceProperties["Sign"].Value = new byte[] { 0x53, 0x45, 0x43, 0x55 };
                input.CimInstanceProperties["Command"].Value = (uint)0x00002;
                input.CimInstanceProperties["CommandType"].Value = (uint)0x52;
                input.CimInstanceProperties["hpqBData"].Value = new byte[] { 0x00, 0x04, 0x00, 0x00 };
                input.CimInstanceProperties["Size"].Value = (uint)4;

                using var biosMethods = cimSession.EnumerateInstances(@"root\WMI", "hpqBIntM").FirstOrDefault();
                if (biosMethods == null) {
                    System.IO.File.WriteAllText("mux_result.txt", "hpqBIntM not found");
                    return;
                }

                using var methodParameters = new CimMethodParametersCollection {
                    CimMethodParameter.Create("InData", input, CimType.Instance, CimFlags.In)
                };

                using var result = cimSession.InvokeMethod(@"root\WMI", biosMethods, "hpqBIOSInt4", methodParameters);
                using var outDataObj = (CimInstance)result.OutParameters["OutData"].Value;
                int returnCode = Convert.ToInt32(outDataObj.CimInstanceProperties["rwReturnCode"].Value);
                byte[] data = (byte[])outDataObj.CimInstanceProperties["Data"].Value;
                System.IO.File.WriteAllText("mux_result.txt", $"ret={returnCode}, data[0]={data[0]}");
            } catch (Exception ex) {
                System.IO.File.WriteAllText("mux_result.txt", ex.Message);
            }
        }
    }
}
