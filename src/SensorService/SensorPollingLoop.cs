using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LibreHwAccess;

namespace SensorService
{
    public class SensorPollingLoop
    {
        // LibreHardwareMonitor reads sensors directly via its own driver,
        // independent of any third-party monitoring tool's UI state.
        // Switched from HwInfoAccess (HWiNFO Shared Memory) because HWiNFO
        // only actively updates its shared memory while its own sensor
        // window is open/visible - if the user runs HWiNFO minimized to the
        // tray (a common setup), every sensor value freezes at 0. This
        // affects HWiNFO users; AIDA64 has a similar dependency on its own
        // UI. LibreHardwareMonitor has no such requirement and works
        // whether or not any other monitoring tool is even installed,
        // making this the most broadly compatible approach for users
        // regardless of which monitoring tool (or none) they otherwise use.
        private readonly LibreHwReader _reader = new();
        private string _configPath;
        private List<SelectedSensorEntry> _selectedSensors = new();
        private FileSystemWatcher? _watcher;

        public SensorPollingLoop(string configPath)
        {
            _configPath = configPath;
        }

        public void Start()
        {
            LoadConfig();
            SetupFileWatcher();
        }

        public string BuildCurrentSensorJson()
        {
            if (!_reader.IsAvailable())
            {
                return BuildEmptyJson(isStale: true);
            }

            var allSensors = _reader.ReadAllSensors();
            var entries = new List<string>();

            foreach (var selected in _selectedSensors)
            {
                // LibreHardwareMonitor often exposes multiple sensors with
                // the identical label under the same device (e.g. Aqua
                // Computer Quadro's "Fan #1" exists once each as Voltage,
                // Current, Power, AND Fan/RPM). Matching on DeviceName +
                // LabelOrig alone is ambiguous, so ReadingType is required
                // when present in the config to pick the correct one.
                var match = allSensors.FirstOrDefault(s =>
                    s.LabelOrig == selected.LabelOrig &&
                    s.DeviceName == selected.DeviceName &&
                    (string.IsNullOrEmpty(selected.ReadingType) || s.ReadingType == selected.ReadingType));

                if (match == null)
                    continue;

                entries.Add($"{{\"alias\":\"{EscapeJson(selected.Alias)}\"," +
                    $"\"labelOrig\":\"{EscapeJson(match.LabelOrig)}\"," +
                    $"\"labelUser\":\"{EscapeJson(match.LabelUser)}\"," +
                    $"\"deviceName\":\"{EscapeJson(match.DeviceName)}\"," +
                    $"\"value\":{match.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
                    $"\"unit\":\"{EscapeJson(match.Unit)}\"," +
                    $"\"readingType\":\"{match.ReadingType}\"," +
                    $"\"isStale\":false}}");
            }

            string timestamp = DateTime.UtcNow.ToString("O");
            return $"{{\"timestamp\":\"{timestamp}\",\"sensors\":[{string.Join(",", entries)}]}}";
        }

        private string BuildEmptyJson(bool isStale)
        {
            string timestamp = DateTime.UtcNow.ToString("O");
            return $"{{\"timestamp\":\"{timestamp}\",\"sensors\":[],\"isStale\":{(isStale ? "true" : "false")}}}";
        }

        private void LoadConfig()
        {
            try
            {
                if (!File.Exists(_configPath))
                {
                    _selectedSensors = new List<SelectedSensorEntry>();
                    Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Config not found: {_configPath}");
                    return;
                }

                string json = File.ReadAllText(_configPath);
                _selectedSensors = ParseConfig(json);
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Loaded {_selectedSensors.Count} sensor(s) from config");
            }
            catch (Exception ex)
            {
                _selectedSensors = new List<SelectedSensorEntry>();
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Error reading config: {ex.Message}");
            }
        }

        private void SetupFileWatcher()
        {
            string dir = Path.GetDirectoryName(_configPath) ?? ".";
            _watcher = new FileSystemWatcher(dir, "selected_sensors.json")
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime
            };
            _watcher.Changed += (s, e) =>
            {
                Thread.Sleep(200);
                LoadConfig();
            };
            _watcher.EnableRaisingEvents = true;
        }

        private static List<SelectedSensorEntry> ParseConfig(string json)
        {
            var result = new List<SelectedSensorEntry>();
            int idx = json.IndexOf("\"selectedSensors\"", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return result;

            int arrStart = json.IndexOf('[', idx);
            int arrEnd = json.LastIndexOf(']');
            if (arrStart < 0 || arrEnd < 0 || arrEnd <= arrStart) return result;

            string arr = json.Substring(arrStart, arrEnd - arrStart + 1);
            int pos = 0;
            while (pos < arr.Length)
            {
                int objStart = arr.IndexOf('{', pos);
                if (objStart < 0) break;
                int objEnd = arr.IndexOf('}', objStart);
                if (objEnd < 0) break;

                string obj = arr.Substring(objStart, objEnd - objStart + 1);
                string alias = ExtractJsonString(obj, "alias");
                string labelOrig = ExtractJsonString(obj, "labelOrig");
                string deviceName = ExtractJsonString(obj, "deviceName");
                string readingType = ExtractJsonString(obj, "readingType");

                if (!string.IsNullOrEmpty(alias) && !string.IsNullOrEmpty(labelOrig))
                {
                    result.Add(new SelectedSensorEntry { Alias = alias, LabelOrig = labelOrig, DeviceName = deviceName ?? "", ReadingType = readingType ?? "" });
                }

                pos = objEnd + 1;
            }

            return result;
        }

        private static string ExtractJsonString(string json, string key)
        {
            int keyIdx = json.IndexOf($"\"{key}\"", StringComparison.OrdinalIgnoreCase);
            if (keyIdx < 0) return string.Empty;

            int colonIdx = json.IndexOf(':', keyIdx + key.Length + 2);
            if (colonIdx < 0) return string.Empty;

            int valStart = json.IndexOf('"', colonIdx);
            if (valStart < 0) return string.Empty;

            int valEnd = json.IndexOf('"', valStart + 1);
            if (valEnd < 0) return string.Empty;

            return json.Substring(valStart + 1, valEnd - valStart - 1);
        }

        private static string EscapeJson(string s)
        {
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
        }
    }

    public class SelectedSensorEntry
    {
        public string Alias { get; set; } = string.Empty;
        public string LabelOrig { get; set; } = string.Empty;
        public string DeviceName { get; set; } = string.Empty;
        public string ReadingType { get; set; } = string.Empty;
    }
}
