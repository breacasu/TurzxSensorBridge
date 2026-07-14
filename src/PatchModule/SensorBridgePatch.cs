using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using TurzxShared.Plugins;

namespace PatchModule
{
    public class SensorBridgePatch : ITurzxPatch
    {
        // Mutex name must match SensorService's own single-instance mutex
        // (see SensorService\Program.cs) so we can reliably detect whether
        // it is already running before trying to launch another copy.
        private const string SensorServiceMutexName = @"Global\TurzxSensorServiceActive";

        private DataSourceInjector? _injector;
        private PipeClient? _pipeClient;
        private AcceptListPatcher? _acceptListPatcher;
        private readonly Dictionary<string, string> _aliasToLabel = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _formattedValueByDataName = new(StringComparer.OrdinalIgnoreCase);

        public string Name => "TurzxSensorBridge";
        public int InterfaceVersion => 1;
        public string Author => "breacasu (breacasu@posteo.de)";
        public string Repository => "https://github.com/breacasu";

        public void Apply(Assembly turzxAssembly, string turzxDir)
        {
            try
            {
                EnsureSensorServiceRunning();

                _injector = new DataSourceInjector(turzxAssembly);
                if (!_injector.TryInitialize())
                {
                    Console.WriteLine($"[TurzxSensorBridge] Injector init failed: {_injector.InitializationError}");
                    return;
                }

                // The Theme Editor's "Data Source" ComboBox is populated from
                // the same M_Data collection that DataSourceInjector fills,
                // but filtered per-widget by GraphItem.AcceptDataList (a
                // hardcoded whitelist of sensor names built in each widget's
                // constructor). AcceptListPatcher periodically adds our
                // sensor aliases to that whitelist for every existing
                // GraphItem, found via the static Monitor.MonitorList field -
                // no WPF window/Application.Current lookup needed.
                _acceptListPatcher = new AcceptListPatcher(turzxAssembly);
                if (_acceptListPatcher.TryInitialize())
                {
                    _acceptListPatcher.StartPatching();
                }
                else
                {
                    Console.WriteLine($"[TurzxSensorBridge] AcceptListPatcher init failed: {_acceptListPatcher.InitializationError}");
                }

                _pipeClient = new PipeClient();
                _pipeClient.OnMessageReceived += OnPipeMessage;
                _pipeClient.OnConnected += OnPipeConnected;
                _pipeClient.Start();

                Console.WriteLine("[TurzxSensorBridge] Started, waiting for SensorService data...");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TurzxSensorBridge] Initialization error: {ex.Message}");
            }
        }

        /// <summary>
        /// Auto-starts SensorService.exe if it isn't already running, so
        /// the user doesn't have to manually start two separate programs in
        /// the right order every time. SensorService.exe is expected to be
        /// deployed to a "SensorService" subfolder next to this
        /// PatchModule.dll (i.e. patches\SensorService\SensorService.exe),
        /// resolved relative to this assembly's own location so it works
        /// regardless of where TurzxPatcher/TURZX itself is installed.
        /// </summary>
        private static void EnsureSensorServiceRunning()
        {
            try
            {
                bool alreadyRunning;
                using (var mutex = new Mutex(initiallyOwned: false, SensorServiceMutexName))
                {
                    // OpenExisting-style check: try to acquire without
                    // blocking; if another process holds it, WaitOne(0)
                    // returns false immediately and we must NOT release
                    // a mutex we don't own, so only release when we did
                    // acquire it.
                    try
                    {
                        alreadyRunning = !mutex.WaitOne(TimeSpan.Zero, false);
                        if (!alreadyRunning)
                            mutex.ReleaseMutex();
                    }
                    catch (System.Threading.AbandonedMutexException)
                    {
                        // Previous SensorService crashed and abandoned the mutex.
                        // We now own it — release so the new instance can acquire it.
                        mutex.ReleaseMutex();
                        alreadyRunning = false;
                    }
                }

                if (alreadyRunning)
                {
                    Console.WriteLine("[TurzxSensorBridge] SensorService already running, skipping auto-start");
                    return;
                }

                string pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? ".";
                string exePath = Path.Combine(pluginDir, "SensorService", "SensorService.exe");

                if (!File.Exists(exePath))
                {
                    Console.WriteLine("[TurzxSensorBridge] SensorService.exe not found at " + exePath + " - sensor values will be unavailable until it is started manually");
                    return;
                }

                Console.WriteLine("[TurzxSensorBridge] Auto-starting SensorService: " + exePath);
                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    WorkingDirectory = Path.GetDirectoryName(exePath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                Process.Start(psi);
            }
            catch (Exception ex)
            {
                Console.WriteLine("[TurzxSensorBridge] EnsureSensorServiceRunning error: " + ex.Message);
            }
        }

        private void OnPipeConnected()
        {
            _aliasToLabel.Clear();
            Console.WriteLine("[TurzxSensorBridge] SensorService connected, clearing stale sensors");
        }

        private void OnPipeMessage(string json)
        {
            try
            {
                var sensors = ParseSensorJson(json);
                foreach (var sensor in sensors)
                {
                    _injector?.EnsureSensor(sensor.Alias, sensor.LabelOrig, sensor.DeviceName);
                    _injector?.UpdateSensorValue(sensor.Alias, sensor.Value, NormalizeUnit(sensor.Unit));
                    _aliasToLabel[sensor.Alias] = sensor.LabelOrig;

                    // Keyed by the same sanitized DataName AcceptListPatcher
                    // uses to find graphItem.m_data - see
                    // PushLiveValueIfOurSensor for why this direct push is
                    // needed (TURZX's own update loop only refreshes a
                    // custom sensor's displayed value once, not every frame).
                    string dataName = DataSourceInjector.SanitizeForXPath(sensor.Alias);
                    string unit = NormalizeUnit(sensor.Unit);
                    string formatted = sensor.Value.ToString("0.##", CultureInfo.InvariantCulture);
                    if (!string.IsNullOrEmpty(unit))
                        formatted += " " + unit;
                    _formattedValueByDataName[dataName] = formatted;
                }
                _acceptListPatcher?.SetAliases(_aliasToLabel.Keys);
                if (_injector != null)
                {
                    // AcceptDataList / DataSourceBox both key off M_Data.DataName
                    // (the sanitized, XPath-safe identifier), not our raw
                    // human-readable alias - see DataSourceInjector.SanitizeForXPath.
                    _acceptListPatcher?.SetAliases(_injector.SensorObjectsByDataName.Keys);
                    _acceptListPatcher?.SetSensorObjects(_injector.SensorObjectsByDataName);
                    _acceptListPatcher?.SetDisplayNames(_injector.DisplayNameByDataName);
                    _acceptListPatcher?.SetFormattedValues(_formattedValueByDataName);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TurzxSensorBridge] Parse error: {ex.Message}");
            }
        }

        private static List<SensorEntry> ParseSensorJson(string json)
        {
            var result = new List<SensorEntry>();

            int sensorsIdx = json.IndexOf("\"sensors\"");
            if (sensorsIdx < 0) return result;

            int arrStart = json.IndexOf('[', sensorsIdx);
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

                string inner = arr.Substring(objStart + 1, objEnd - objStart - 1);
                var entry = new SensorEntry
                {
                    Alias = ExtractJsonValue(inner, "alias"),
                    LabelOrig = ExtractJsonValue(inner, "labelOrig"),
                    DeviceName = ExtractJsonValue(inner, "deviceName"),
                    Value = ExtractJsonNumber(inner, "value"),
                    Unit = ExtractJsonValue(inner, "unit")
                };

                if (!string.IsNullOrEmpty(entry.Alias) && !string.IsNullOrEmpty(entry.LabelOrig))
                {
                    result.Add(entry);
                }

                pos = objEnd + 1;
            }

            return result;
        }

        private static string NormalizeUnit(string lhmUnit)
        {
            if (string.IsNullOrEmpty(lhmUnit)) return "";
            switch (lhmUnit.Trim())
            {
                case "°C": return "°";
                case "RPM": return "R";
                case "W":   return "W";
                case "V":   return "V";
                case "%":   return "%";
                case "L/h": return "L/h";
                case "uS/cm": return "µS";
                default: return lhmUnit;
            }
        }

        private static string ExtractJsonValue(string json, string key)
        {
            int keyIdx = json.IndexOf($"\"{key}\"");
            if (keyIdx < 0) return string.Empty;

            int colonIdx = json.IndexOf(':', keyIdx + key.Length + 2);
            if (colonIdx < 0) return string.Empty;

            int valStart = json.IndexOf('"', colonIdx);
            if (valStart < 0) return string.Empty;

            int valEnd = json.IndexOf('"', valStart + 1);
            if (valEnd < 0) return string.Empty;

            return json.Substring(valStart + 1, valEnd - valStart - 1);
        }

        private static double ExtractJsonNumber(string json, string key)
        {
            int keyIdx = json.IndexOf($"\"{key}\"");
            if (keyIdx < 0) return 0;

            int colonIdx = json.IndexOf(':', keyIdx + key.Length + 2);
            if (colonIdx < 0) return 0;

            int valStart = colonIdx + 1;
            while (valStart < json.Length && json[valStart] == ' ') valStart++;

            int valEnd = valStart;
            while (valEnd < json.Length && (char.IsDigit(json[valEnd]) || json[valEnd] == '.' || json[valEnd] == '-' || json[valEnd] == 'e' || json[valEnd] == 'E' || json[valEnd] == '+'))
                valEnd++;

            if (valEnd <= valStart) return 0;
            string numStr = json.Substring(valStart, valEnd - valStart);

            if (double.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out double result))
                return result;

            return 0;
        }

        private struct SensorEntry
        {
            public string Alias;
            public string LabelOrig;
            public string DeviceName;
            public double Value;
            public string Unit;
        }
    }
}
