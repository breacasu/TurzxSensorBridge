// ============================================================================
// HwInfoAccess - Library for reading sensor data from HWiNFO Shared Memory
// ============================================================================
// This library provides access to HWiNFO64's Shared Memory interface, which
// exposes all sensor values (CPU, GPU, temperatures, voltages, fans, etc.)
// including Aquacomputer devices.
//
// Shared Memory Details:
//   Mutex Name:              Global\HWiNFO_SM2_MUTEX
//   Memory-Mapped File:      Global\HWiNFO_SENS_SM2
//   Signature:               0x53695748 ("HWiS" in ASCII, little-endian)
//
// Reference: HWiNFO_SHARED_MEMORY.md in docs/
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Threading;

namespace HwInfoAccess
{
    /// <summary>
    /// HWiNFO Shared Memory header structure (HWiNFO_SENSORS_SHARED_MEM2).
    /// Layout: packed (no padding between fields).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct HwInfoSharedMemHeader
    {
        public uint Signature;
        public uint Version;
        public uint Revision;
        public long PollTime;           // __time64_t = 8 byte signed Unix timestamp
        public uint OffsetOfSensorSection;
        public uint SizeOfSensorElement;
        public uint NumSensorElements;
        public uint OffsetOfReadingSection;
        public uint SizeOfReadingElement;
        public uint NumReadingElements;
    }

    /// <summary>
    /// HWiNFO Sensor Element (device instance).
    /// A "Sensor" in HWiNFO terminology is a device instance (e.g. "Aquacomputer D5 Next"),
    /// not an individual measurement value.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct HwInfoSensorElement
    {
        public uint SensorID;
        public uint SensorInst;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string SensorNameOrig;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string SensorNameUser;
    }

    /// <summary>
    /// HWiNFO Reading Type enumeration.
    /// </summary>
    public enum HwInfoSensorReadingType : uint
    {
        None = 0,
        Temperature = 1,
        Voltage = 2,
        Fan = 3,
        Current = 4,
        Power = 5,
        Clock = 6,
        Usage = 7,
        Other = 8,
    }

    /// <summary>
    /// HWiNFO Reading Element (individual measurement value).
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct HwInfoReadingElement
    {
        public HwInfoSensorReadingType ReadingType;
        public uint SensorIndex;
        public uint ReadingID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string LabelOrig;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string LabelUser;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 16)]
        public string Unit;
        public double Value;
        public double ValueMin;
        public double ValueMax;
        public double ValueAvg;
    }

    /// <summary>
    /// Public POCO class for a sensor reading with resolved device name.
    /// This is the main data type returned by HwInfoReader.
    /// </summary>
    public class HwInfoSensorReading
    {
        public string DeviceName { get; set; } = string.Empty;
        public string LabelOrig { get; set; } = string.Empty;
        public string LabelUser { get; set; } = string.Empty;
        public double Value { get; set; }
        public string Unit { get; set; } = string.Empty;
        public HwInfoSensorReadingType ReadingType { get; set; }
        public uint SensorIndex { get; set; }
        public uint ReadingID { get; set; }
    }

    /// <summary>
    /// Reader for HWiNFO Shared Memory.
    /// Opens the Memory-Mapped File, reads the header, sensor elements, and reading elements,
    /// and returns a list of resolved sensor readings.
    /// </summary>
    public class HwInfoReader : IDisposable
    {
        private const string MutexName = @"Global\HWiNFO_SM2_MUTEX";
        private const string MmfName = @"Global\HWiNFO_SENS_SM2";
        private const uint ExpectedSignature = 0x53695748;

        private bool _disposed;

        /// <summary>
        /// Checks whether HWiNFO Shared Memory is available (HWiNFO is running with Shared Memory enabled).
        /// Returns true if the Memory-Mapped File can be opened.
        /// </summary>
        public bool IsHwInfoAvailable()
        {
            try
            {
                using (var mmf = MemoryMappedFile.OpenExisting(MmfName, MemoryMappedFileRights.Read))
                {
                    return true;
                }
            }
            catch (FileNotFoundException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>
        /// Reads all sensor readings from HWiNFO Shared Memory.
        /// Returns an empty list if HWiNFO is not available or an error occurs.
        /// Does NOT throw exceptions to the caller.
        /// </summary>
        public List<HwInfoSensorReading> ReadAllSensors()
        {
            var result = new List<HwInfoSensorReading>();

            if (!IsHwInfoAvailable())
            {
                return result;
            }

           MemoryMappedFile? mmf = null;
            Mutex? mutex = null;

            try
            {
                // Try to acquire mutex for synchronized reading (best effort)
                try
                {
                    mutex = Mutex.OpenExisting(MutexName, System.Security.AccessControl.MutexRights.Synchronize);
                    mutex.WaitOne(1000);
                }
                catch
                {
                    // Mutex not found or not accessible - continue without lock
                }

                mmf = MemoryMappedFile.OpenExisting(MmfName, MemoryMappedFileRights.Read);

                using (var accessor = mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read))
                {
                    // Read header
                    var headerSize = Marshal.SizeOf<HwInfoSharedMemHeader>();
                    byte[] headerBytes = new byte[headerSize];
                    accessor.ReadArray(0, headerBytes, 0, headerSize);
                    HwInfoSharedMemHeader header = BytesToStructure<HwInfoSharedMemHeader>(headerBytes);

                    // Validate signature
                    if (header.Signature != ExpectedSignature)
                    {
                        return result;
                    }

                    // Read sensor elements
                    var sensorElements = ReadSensorElements(accessor, header);

                    // Read reading elements and resolve device names
                    var readingElements = ReadReadingElements(accessor, header);

                    // Build result list with resolved device names
                    foreach (var reading in readingElements)
                    {
                        string deviceName = "Unknown";
                        if ((int)reading.SensorIndex < sensorElements.Count)
                        {
                            deviceName = sensorElements[(int)reading.SensorIndex].SensorNameOrig;
                            if (string.IsNullOrEmpty(deviceName))
                            {
                                deviceName = sensorElements[(int)reading.SensorIndex].SensorNameUser;
                            }
                        }

                        result.Add(new HwInfoSensorReading
                        {
                            DeviceName = deviceName,
                            LabelOrig = reading.LabelOrig,
                            LabelUser = reading.LabelUser,
                            Value = reading.Value,
                            Unit = reading.Unit,
                            ReadingType = reading.ReadingType,
                            SensorIndex = reading.SensorIndex,
                            ReadingID = reading.ReadingID
                        });
                    }
                }
            }
            catch
            {
                // Swallow exceptions - caller should check IsHwInfoAvailable() first
            }
            finally
            {
                mutex?.ReleaseMutex();
                mutex?.Dispose();
                mmf?.Dispose();
            }

            return result;
        }

        private List<HwInfoSensorElement> ReadSensorElements(MemoryMappedViewAccessor accessor, HwInfoSharedMemHeader header)
        {
            var sensors = new List<HwInfoSensorElement>();

            if (header.NumSensorElements == 0 || header.SizeOfSensorElement == 0)
            {
                return sensors;
            }

            int elementSize = Marshal.SizeOf<HwInfoSensorElement>();
            byte[] elementBytes = new byte[elementSize];

            for (uint i = 0; i < header.NumSensorElements; i++)
            {
                accessor.ReadArray(header.OffsetOfSensorSection + i * header.SizeOfSensorElement, elementBytes, 0, elementSize);
                sensors.Add(BytesToStructure<HwInfoSensorElement>(elementBytes));
            }

            return sensors;
        }

        private List<HwInfoReadingElement> ReadReadingElements(MemoryMappedViewAccessor accessor, HwInfoSharedMemHeader header)
        {
            var readings = new List<HwInfoReadingElement>();

            if (header.NumReadingElements == 0 || header.SizeOfReadingElement == 0)
            {
                return readings;
            }

            int elementSize = Marshal.SizeOf<HwInfoReadingElement>();
            byte[] elementBytes = new byte[elementSize];

            for (uint i = 0; i < header.NumReadingElements; i++)
            {
                accessor.ReadArray(header.OffsetOfReadingSection + i * header.SizeOfReadingElement, elementBytes, 0, elementSize);
                readings.Add(BytesToStructure<HwInfoReadingElement>(elementBytes));
            }

            return readings;
        }

         private static T BytesToStructure<T>(byte[] bytes) where T : struct
        {
            GCHandle handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
            try
            {
                return Marshal.PtrToStructure<T>(handle.AddrOfPinnedObject());
            }
            finally
            {
                handle.Free();
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
            }
        }
    }
}
