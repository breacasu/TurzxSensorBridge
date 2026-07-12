using System;
using HwInfoAccess;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

class HwInfoTest
{
    static void Main(string[] args)
    {
        Console.WriteLine("HWiNFO Access Test");
        Console.WriteLine("==================\n");

        // Diagnostic: print raw header PollTime (Unix timestamp) so we can
        // tell whether HWiNFO is actively polling, or the shared memory is
        // frozen/stale.
        try
        {
            using var mmf = MemoryMappedFile.OpenExisting(@"Global\HWiNFO_SENS_SM2", MemoryMappedFileRights.Read);
            using var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            var header = new HwInfoSharedMemHeader();
            int headerSize = Marshal.SizeOf<HwInfoSharedMemHeader>();
            byte[] headerBytes = new byte[headerSize];
            accessor.ReadArray(0, headerBytes, 0, headerSize);
            GCHandle handle = GCHandle.Alloc(headerBytes, GCHandleType.Pinned);
            try { header = Marshal.PtrToStructure<HwInfoSharedMemHeader>(handle.AddrOfPinnedObject()); }
            finally { handle.Free(); }

            var pollTimeUtc = DateTimeOffset.FromUnixTimeSeconds(header.PollTime).UtcDateTime;
            Console.WriteLine($"Header.PollTime (raw)  = {header.PollTime}");
            Console.WriteLine($"Header.PollTime (UTC)  = {pollTimeUtc:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Now (UTC)              = {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}");
            Console.WriteLine($"Age of last poll       = {(DateTime.UtcNow - pollTimeUtc).TotalSeconds:F1}s");
            Console.WriteLine($"Header.Version         = {header.Version}");
            Console.WriteLine($"Header.Revision        = {header.Revision}");
            Console.WriteLine($"NumSensorElements      = {header.NumSensorElements}");
            Console.WriteLine($"NumReadingElements     = {header.NumReadingElements}");
            Console.WriteLine();
        }
        catch (Exception ex)
        {
            Console.WriteLine("Header diag error: " + ex.Message);
        }

        var reader = new HwInfoReader();
        
        Console.WriteLine("1. Checking HWiNFO availability...");
        bool available = reader.IsHwInfoAvailable();
        Console.WriteLine($"   HWiNFO available: {available}");
        
        if (available)
        {
            Console.WriteLine("\n2. Reading all sensors...");
            var sensors = reader.ReadAllSensors();
            Console.WriteLine($"   Found {sensors.Count} sensors\n");
            
            foreach (var sensor in sensors)
            {
                Console.WriteLine($"   - {sensor.DeviceName}");
                Console.WriteLine($"     {sensor.LabelUser}: {sensor.Value} {sensor.Unit}");
                Console.WriteLine($"     (Orig: {sensor.LabelOrig})");
                Console.WriteLine();
            }
        }
        else
        {
            Console.WriteLine("\n   HWiNFO not available - is HWiNFO running with Shared Memory enabled?");
            Console.WriteLine("   Expected pipe name: \\\\.__\\pipe\\TurzxSensorBridge");
        }
    }
}
