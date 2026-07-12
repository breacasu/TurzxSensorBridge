// ============================================================================
// LibreHwAccess - Sensor reading via LibreHardwareMonitorLib
// ============================================================================
// Alternative to HwInfoAccess (HWiNFO Shared Memory). LibreHardwareMonitor
// reads hardware sensors directly through its own driver, independent of
// any third-party monitoring application's UI/window state.
//
// This solves a real-world problem observed with HWiNFO: HWiNFO's Shared
// Memory (Global\HWiNFO_SENS_SM2) only gets actively updated while HWiNFO's
// own sensor window is open and visible. If the user runs HWiNFO minimized
// to the tray (a common setup), the shared memory values freeze at 0 for
// every sensor - not just Aquacomputer devices, but CPU/GPU too. Using
// LibreHardwareMonitor removes this dependency entirely: it works whether
// or not HWiNFO/AIDA64 or any other monitoring tool is even installed.
//
// LibreHardwareMonitor supports Aquacomputer devices (D5 Next, Quadro,
// Leakshield, Farbwerk, Octo, highflow NEXT, etc.) via USB HID, the same
// devices HWiNFO reads.
// ============================================================================

using LibreHardwareMonitor.Hardware;

namespace LibreHwAccess
{
    /// <summary>
    /// Public POCO class for a sensor reading with resolved device name.
    /// Field-compatible with HwInfoAccess.HwInfoSensorReading so callers can
    /// switch reader implementations with minimal changes.
    /// </summary>
    public class LibreSensorReading
    {
        public string DeviceName { get; set; } = string.Empty;
        public string LabelOrig { get; set; } = string.Empty;
        public string LabelUser { get; set; } = string.Empty;
        public double Value { get; set; }
        public string Unit { get; set; } = string.Empty;
        public string ReadingType { get; set; } = string.Empty;
    }

    /// <summary>
    /// Reader for hardware sensors via LibreHardwareMonitorLib.
    /// Keeps a single Computer instance open across calls (creating/opening
    /// it repeatedly is expensive and can cause driver churn), and calls
    /// Update() on every hardware node before reading values.
    /// </summary>
    public class LibreHwReader : IDisposable
    {
        private Computer? _computer;
        private bool _disposed;

        /// <summary>
        /// Returns true if the underlying Computer object could be opened
        /// (drivers loaded successfully). Does not guarantee any specific
        /// hardware is present.
        /// </summary>
        public bool IsAvailable()
        {
            EnsureOpen();
            return _computer != null;
        }

        private void EnsureOpen()
        {
            if (_computer != null || _disposed) return;

            try
            {
                _computer = new Computer
                {
                    IsControllerEnabled = true,
                    IsMotherboardEnabled = true,
                    IsCpuEnabled = true,
                    IsGpuEnabled = true,
                    IsStorageEnabled = true,
                    IsMemoryEnabled = true,
                    IsNetworkEnabled = false,
                    IsPsuEnabled = false,
                };
                _computer.Open();
            }
            catch
            {
                _computer = null;
            }
        }

        /// <summary>
        /// Reads all sensor readings from all detected hardware.
        /// Returns an empty list if LibreHardwareMonitor could not be
        /// initialized (e.g. missing driver permissions) or an error occurs.
        /// Does NOT throw exceptions to the caller.
        /// </summary>
        public List<LibreSensorReading> ReadAllSensors()
        {
            var result = new List<LibreSensorReading>();

            EnsureOpen();
            if (_computer == null) return result;

            try
            {
                foreach (var hardware in _computer.Hardware)
                {
                    ReadHardwareRecursive(hardware, result);
                }
            }
            catch
            {
                // Swallow exceptions - caller should check IsAvailable() first.
            }

            return result;
        }

        private static void ReadHardwareRecursive(IHardware hardware, List<LibreSensorReading> result)
        {
            try
            {
                hardware.Update();

                foreach (var sensor in hardware.Sensors)
                {
                    if (!sensor.Value.HasValue) continue;

                    result.Add(new LibreSensorReading
                    {
                        DeviceName = hardware.Name,
                        LabelOrig = sensor.Name,
                        LabelUser = sensor.Name,
                        Value = sensor.Value.Value,
                        Unit = UnitForSensorType(sensor.SensorType),
                        ReadingType = sensor.SensorType.ToString(),
                    });
                }

                foreach (var sub in hardware.SubHardware)
                {
                    ReadHardwareRecursive(sub, result);
                }
            }
            catch
            {
                // Skip hardware nodes that throw on Update() (e.g. transient
                // USB HID read errors from Aquacomputer devices) - keep
                // reading the rest.
            }
        }

        private static string UnitForSensorType(SensorType type)
        {
            return type switch
            {
                SensorType.Voltage => "V",
                SensorType.Current => "A",
                SensorType.Power => "W",
                SensorType.Clock => "MHz",
                SensorType.Temperature => "°C",
                SensorType.Load => "%",
                SensorType.Fan => "RPM",
                SensorType.Flow => "L/h",
                SensorType.Control => "%",
                SensorType.Level => "%",
                SensorType.Factor => "",
                SensorType.Data => "GB",
                SensorType.SmallData => "MB",
                SensorType.Throughput => "B/s",
                SensorType.TimeSpan => "s",
                SensorType.Energy => "mWh",
                SensorType.Noise => "dBA",
                SensorType.Conductivity => "uS/cm",
                SensorType.Humidity => "%",
                _ => "",
            };
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _computer?.Close(); } catch { }
            _computer = null;
        }
    }
}
