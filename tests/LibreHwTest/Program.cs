using System;
using LibreHwAccess;

class LibreHwTest
{
    static void Main(string[] args)
    {
        Console.WriteLine("LibreHardwareMonitor Access Test");
        Console.WriteLine("=================================\n");

        using var reader = new LibreHwReader();

        Console.WriteLine("1. Checking availability...");
        bool available = reader.IsAvailable();
        Console.WriteLine($"   Available: {available}");

        if (!available)
        {
            Console.WriteLine("   LibreHardwareMonitor could not be initialized (driver/permission issue?).");
            return;
        }

        Console.WriteLine("\n2. Reading all sensors (pass 1)...");
        var sensors1 = reader.ReadAllSensors();
        Console.WriteLine($"   Found {sensors1.Count} sensor readings\n");

        foreach (var s in sensors1)
        {
            string dn = s.DeviceName.ToLowerInvariant();
            if (dn.Contains("aqua") || dn.Contains("d5") || dn.Contains("quadro") ||
                dn.Contains("highflow") || dn.Contains("flow"))
            {
                Console.WriteLine($"   - {s.DeviceName} | {s.LabelOrig} = {s.Value} {s.Unit} ({s.ReadingType})");
            }
        }

        Console.WriteLine("\n3. Waiting 3s, reading again (pass 2) to verify values actually change...");
        System.Threading.Thread.Sleep(3000);
        var sensors2 = reader.ReadAllSensors();

        int changed = 0;
        for (int i = 0; i < Math.Min(sensors1.Count, sensors2.Count); i++)
        {
            if (sensors1[i].DeviceName == sensors2[i].DeviceName &&
                sensors1[i].LabelOrig == sensors2[i].LabelOrig &&
                sensors1[i].Value != sensors2[i].Value)
            {
                changed++;
            }
        }
        Console.WriteLine($"   {changed} sensor value(s) changed between pass 1 and pass 2 (proves live polling).");

        Console.WriteLine("\n4. All detected hardware:");
        var allByDevice = new System.Collections.Generic.Dictionary<string, int>();
        foreach (var s in sensors2)
        {
            allByDevice.TryGetValue(s.DeviceName, out int count);
            allByDevice[s.DeviceName] = count + 1;
        }
        foreach (var kv in allByDevice)
        {
            Console.WriteLine($"   - {kv.Key}: {kv.Value} sensor(s)");
        }
    }
}
