using System;
using Microsoft.Management.Infrastructure;

class Program {
    static void Main() {
        try {
            using var cimSession = Microsoft.Management.Infrastructure.CimSession.Create(null);
            var gpus = cimSession.QueryInstances(@"root\cimv2", "WQL", "SELECT Name, Availability FROM Win32_VideoController");
            
            bool hasOfflineOrMissingIGpu = true;
            foreach (var gpu in gpus)
            {
                string name = gpu.CimInstanceProperties["Name"]?.Value?.ToString() ?? "";
                Console.WriteLine("Found GPU: " + name);
                if (name.Contains("Intel") || (name.Contains("AMD") && name.Contains("Radeon")))
                {
                    ushort availability = Convert.ToUInt16(gpu.CimInstanceProperties["Availability"]?.Value ?? 3);
                    Console.WriteLine("IGpu Availability: " + availability);
                    if (availability != 8) 
                    {
                        hasOfflineOrMissingIGpu = false;
                    }
                }
            }
            Console.WriteLine("Is Discrete: " + hasOfflineOrMissingIGpu);
        } catch (Exception ex) {
            Console.WriteLine("Error: " + ex.Message);
        }
    }
}
