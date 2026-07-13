using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;

namespace PatchModule
{
    public class DataSourceInjector
    {
        private readonly Assembly _turzxAssembly;
        private Type? _mDataType;
        private Type? _utilType;
        private FieldInfo? _utilXmlDocField;
        private readonly List<IList> _mDataCollections = new();
        private readonly List<string> _mDataCollectionNames = new();
        private readonly List<IList> _stringLists = new();
        private readonly Dictionary<string, object> _sensorObjects = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, object> _sensorObjectsByDataName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _displayNameByDataName = new(StringComparer.OrdinalIgnoreCase);
        private PropertyInfo? _dataNameProp;
        private PropertyInfo? _subNameProp;
        private PropertyInfo? _displayNameProp;
        private PropertyInfo? _showUnitProp;
        private PropertyInfo? _isEnabledProp;
        private PropertyInfo? _valueProp;
        private PropertyInfo? _valueStringProp;
        private Timer? _diagTimer;

        public bool IsInitialized { get; private set; }
        public string InitializationError { get; private set; } = string.Empty;

        public DataSourceInjector(Assembly turzxAssembly)
        {
            _turzxAssembly = turzxAssembly;
        }

        public bool TryInitialize()
        {
            try
            {
                _mDataType = _turzxAssembly.GetType("UsbMonitorL.M_Data");
                if (_mDataType == null)
                {
                    InitializationError = "UsbMonitorL.M_Data type not found";
                    return false;
                }

                _dataNameProp = _mDataType.GetProperty("DataName");
                _subNameProp = _mDataType.GetProperty("SubName");
                _displayNameProp = _mDataType.GetProperty("DisplayName");
                _showUnitProp = _mDataType.GetProperty("ShowUnit");
                _isEnabledProp = _mDataType.GetProperty("IsEnabled");

                // Util.XmlDoc holds the loaded translation XML that
                // M_Data's constructor depends on (via Util.Translate ->
                // Util.XmlDoc.ChildNodes[1].SelectSingleNode(...)). It is
                // populated by Util's static constructor, which may not have
                // run yet at the exact moment our plugin's Apply() executes
                // (very early in TURZX startup). Calling the M_Data
                // constructor before XmlDoc is ready throws a
                // NullReferenceException - harmless (EnsureSensor retries on
                // the next pipe message ~1s later) but noisy in the log. We
                // check readiness before constructing to avoid that.
                _utilType = _turzxAssembly.GetType("UsbMonitorL.Util");
                _utilXmlDocField = _utilType?.GetField("XmlDoc", BindingFlags.Public | BindingFlags.Static);

                _valueProp = FindValueProperty();
                if (_valueProp == null)
                {
                    InitializationError = "Numeric value property not found on M_Data";
                    return false;
                }

                // M_Data.Value (string) is what widgets actually render as
                // text (via M_Data.ValueWithUnit = Value + unit, computed in
                // M_Data's internal refresh method whenever Value's setter
                // runs). M_Data.Rate (double, 0.0-1.0) is only used for
                // progress-bar/gauge fill percentage, NOT for the displayed
                // text - setting only Rate (as this plugin did originally)
                // leaves the widget showing "0" or an empty value forever.
                // Confirmed via dnSpy: M_Data.Value's setter unconditionally
                // enqueues into DataQueue and calls a refresh method that
                // rebuilds ValueWithUnit from the new Value.
                _valueStringProp = _mDataType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);

                var found = FindAllMDataCollections();
                if (found == 0)
                {
                    InitializationError = "ObservableCollection<M_Data> field not found";
                    return false;
                }
                Console.WriteLine($"[TurzxSensorBridge] Found {found} ObservableCollection<M_Data> field(s)");
                FindStringLists();
                DiagnoseLargeStaticFields();

                IsInitialized = true;
                Console.WriteLine("[TurzxSensorBridge] Scheduling periodic DeferredDiagnostic (first in 10s, then every 30s)");
                _diagTimer = new Timer(_ => DeferredDiagnostic(), null, 10000, 30000);
                return true;
            }
            catch (Exception ex)
            {
                InitializationError = ex.Message;
                return false;
            }
        }

        public void EnsureSensor(string alias, string labelOrig, string deviceName)
        {
            if (!IsInitialized) return;

            if (_sensorObjects.ContainsKey(alias))
                return;

            // Wait until Util.XmlDoc is loaded before constructing M_Data
            // (see field comment on _utilXmlDocField) - avoids a harmless
            // but noisy NullReferenceException on the very first pipe
            // message right after TURZX startup. EnsureSensor is called
            // again automatically on every subsequent pipe message (~1x/s),
            // so simply skipping here is enough; no retry loop needed.
            if (_utilXmlDocField != null && _utilXmlDocField.GetValue(null) == null)
                return;

            try
            {
                // IMPORTANT: M_Data.DataName is used as an XPath expression
                // via Util.Translate("T_DataType_" + DataName) whenever the
                // game needs to look up this sensor's display text (e.g.
                // GraphItem.RefreshDisplayName() / M_Data.RefreshDisplayName()
                // - confirmed via dnSpy decompilation, Util.cs ~line 5714:
                // "Util.XmlDoc.ChildNodes[1].SelectSingleNode(A_0)"). XPath
                // treats spaces and many punctuation characters as syntax
                // (e.g. path separators), so a DataName like "Quadro Temp 1"
                // throws an XPathException ("... is an invalid token") the
                // moment the user selects it in the Theme Editor - crashing
                // TURZX entirely. All built-in sensor DataNames (CPUTEMP,
                // GPUCLOCK, ...) are a single unbroken identifier for exactly
                // this reason. We therefore use a sanitized, XPath-safe
                // DataName (alphanumeric + underscore only) internally, while
                // keeping the original human-readable alias for SubName/
                // DisplayName (what the user actually sees in the ComboBox).
                string safeDataName = SanitizeForXPath(alias);

                // Use the REAL M_Data(string) constructor via Activator
                // instead of FormatterServices.GetUninitializedObject.
                // Confirmed via dnSpy that the constructor initializes
                // several fields required later when the user selects the
                // sensor in the Theme Editor's ComboBox (e.g. DataQueue =
                // new Queue<string>(), queueLen = 10) - M_Data.Value's setter
                // unconditionally accesses DataQueue.Count, so leaving
                // DataQueue null (as GetUninitializedObject would) throws a
                // NullReferenceException and crashes the whole WPF message
                // loop the instant the sensor is selected.
                object newEntry = Activator.CreateInstance(_mDataType!, safeDataName)!;

                _dataNameProp?.SetValue(newEntry, safeDataName);
                _subNameProp?.SetValue(newEntry, labelOrig);

                // The "Data Source" ComboBox in ThemeEditForm binds its
                // DisplayMemberPath to M_Data.DisplayName (confirmed via
                // dnSpy decompilation: DataSourceBox.DisplayMemberPath =
                // "DisplayName"). The constructor already set DisplayName
                // via a "T_DataType_<safeDataName>" lookup that resolves to
                // an empty string (no such translation key exists for our
                // custom sensors), so we override it here with the actual
                // human-readable alias the user configured.
                _displayNameProp?.SetValue(newEntry, alias);
                _showUnitProp?.SetValue(newEntry, false);
                _isEnabledProp?.SetValue(newEntry, true);

                foreach (var col in _mDataCollections)
                {
                    if (!col.Contains(newEntry))
                        col.Add(newEntry);
                }
                foreach (var sl in _stringLists)
                {
                    if (!sl.Contains(safeDataName))
                        sl.Add(safeDataName);
                }
                _sensorObjects[alias] = newEntry!;
                _sensorObjectsByDataName[safeDataName] = newEntry!;
                _displayNameByDataName[safeDataName] = alias;

                Console.WriteLine("[TurzxSensorBridge] Added sensor: " + alias + " (" + labelOrig + ") [DataName=" + safeDataName + "]");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[TurzxSensorBridge] Error adding sensor '" + alias + "': " + ex.Message);
            }
        }

        /// <summary>Produces an XPath-safe token: letters, digits and
        /// underscore only, always starting with a letter (XPath/XML
        /// element name rules), so it can never be misinterpreted as XPath
        /// syntax (spaces, '/', '@', '[', ']', etc. all have special
        /// meaning in XPath expressions).</summary>
        public static string SanitizeForXPath(string input)
        {
            var sb = new System.Text.StringBuilder(input.Length + 1);
            sb.Append("Sensor_");
            foreach (var c in input)
            {
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else sb.Append('_');
            }
            return sb.ToString();
        }


        public int SensorCount => _sensorObjects.Count;

        /// <summary>Snapshot of currently injected M_Data objects (alias -> object).</summary>
        public IReadOnlyDictionary<string, object> SensorObjects => _sensorObjects;

        /// <summary>Snapshot of currently injected M_Data objects keyed by their
        /// sanitized, XPath-safe DataName (what GraphItem.AcceptDataList
        /// actually needs to contain - see SanitizeForXPath).</summary>
        public IReadOnlyDictionary<string, object> SensorObjectsByDataName => _sensorObjectsByDataName;

        /// <summary>Maps sanitized DataName back to the original human-readable
        /// alias, so callers can restore M_Data.DisplayName / GraphItem.DisplayName
        /// after TURZX's own code overwrites them via a failed
        /// "T_DataType_&lt;DataName&gt;" translation lookup (see M_Data.RefreshDisplayName,
        /// called whenever the user selects a sensor in the Theme Editor).</summary>
        public IReadOnlyDictionary<string, string> DisplayNameByDataName => _displayNameByDataName;

        public bool UpdateSensorValue(string alias, double value)
        {
            if (!IsInitialized) return false;
            if (!_sensorObjects.TryGetValue(alias, out var obj)) return false;

            try
            {
                // Rate (double, 0.0-1.0) drives progress-bar/gauge fill only.
                // We don't know the sensor's natural max value, so this stays
                // a best-effort placeholder (0) rather than a meaningless
                // guess; the actual displayed text comes from Value below.
                _valueProp?.SetValue(obj, value);

                // Value (string) is what widgets actually render as text.
                // Use the real property setter (not a backing field) so
                // M_Data's internal DataQueue/ValueWithUnit refresh logic
                // runs exactly like it does for built-in sensors.
                string formatted = value.ToString("0.##", CultureInfo.InvariantCulture);
                _valueStringProp?.SetValue(obj, formatted);

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TurzxSensorBridge] Error updating value for '{alias}': {ex.Message}");
                return false;
            }
        }

        private PropertyInfo? FindValueProperty()
        {
            var props = _mDataType!.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var p in props)
            {
                if (p.PropertyType == typeof(double) || p.PropertyType == typeof(float) ||
                    p.PropertyType == typeof(decimal))
                {
                    return p;
                }
            }
            foreach (var p in props)
            {
                if (p.PropertyType == typeof(int) || p.PropertyType == typeof(long) ||
                    p.PropertyType == typeof(short))
                {
                    return p;
                }
            }
            return null;
        }

        public void Clear()
        {
            if (!IsInitialized) return;

            foreach (var col in _mDataCollections)
            {
                foreach (var obj in _sensorObjects.Values)
                    col.Remove(obj);
            }
            _sensorObjects.Clear();
            Console.WriteLine("[TurzxSensorBridge] Cleared all injected sensors");
        }

        private int FindAllMDataCollections()
        {
            string targetTypeName = _mDataType!.FullName!;
            var found = new List<FieldInfo>();

            try
            {
                var allTypes = _turzxAssembly.GetTypes();
                foreach (var type in allTypes)
                {
                    found.AddRange(FindObservableCollectionFields(type, targetTypeName));
                }
            }
            catch { }

            try
            {
                found.AddRange(FindObservableCollectionFields(_mDataType!, targetTypeName));
            }
            catch { }

            int count = 0;
            foreach (var field in found)
            {
                var val = field.GetValue(null);
                IList? list = val as IList;

                if (list == null)
                {
                    try
                    {
                        list = Activator.CreateInstance(field.FieldType) as IList;
                        field.SetValue(null, list);
                        Console.WriteLine("[TurzxSensorBridge] Created ObservableCollection<M_Data> (was null) for " + field.DeclaringType?.FullName + "." + field.Name);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[TurzxSensorBridge] Failed to create collection " + field.Name + ": " + ex.Message);
                        continue;
                    }
                }

                if (list != null)
                {
                    _mDataCollections.Add(list);
                    string fieldFullName = field.DeclaringType?.FullName + "." + field.Name;
                    _mDataCollectionNames.Add(fieldFullName);
                    Console.WriteLine("[TurzxSensorBridge] Using ObservableCollection<M_Data> field: " + fieldFullName + " (instance hash=" + list.GetHashCode() + ", " + list.Count + " existing items)");
                    count++;
                }
            }

            return count;
        }

        private void FindStringLists()
        {
            try
            {
                int found = 0;
                var allTypes = _turzxAssembly.GetTypes();
                foreach (var type in allTypes)
                {
                    try
                    {
                        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                        foreach (var f in fields)
                        {
                            try
                            {
                                var ft = f.FieldType;
                                if (!ft.IsGenericType) continue;
                                if (ft.GetGenericTypeDefinition() != typeof(List<>)) continue;
                                var args = ft.GetGenericArguments();
                                if (args.Length != 1 || args[0] != typeof(string)) continue;

                                if (type.Name == "Monitor" && f.Name == "fonts") continue;

                                var val = f.GetValue(null);
                                IList? list;
                                if (val is IList il)
                                {
                                    list = il;
                                }
                                else
                                {
                                    list = Activator.CreateInstance(ft) as IList;
                                    f.SetValue(null, list);
                                    Console.WriteLine($"[TurzxSensorBridge] Created List<string> ({type.Name}.{f.Name}) was null");
                                }

                                if (list != null)
                                {
                                    _stringLists.Add(list);
                                    found++;
                                    Console.WriteLine($"[TurzxSensorBridge] Found List<string>: {type.Name}.{f.Name} ({list.Count} items)");
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
                Console.WriteLine($"[TurzxSensorBridge] Found {found} additional List<string> field(s) for DataSource names");
            }
            catch { }
        }

        private void DiagnoseLargeStaticFields()
        {
            try
            {
                Console.WriteLine("[TurzxSensorBridge] === DIAG: static fields with content ===");
                var allTypes = _turzxAssembly.GetTypes();
                foreach (var type in allTypes)
                {
                    try
                    {
                        string tn = type.Name;
                        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                        foreach (var f in fields)
                        {
                            try
                            {
                                var val = f.GetValue(null);
                                if (val == null) continue;

                                if (val is IList list && list.Count >= 3 && list.Count <= 200)
                                {
                                    Console.WriteLine($"[TurzxSensorBridge]   {tn}.{f.Name}: {f.FieldType.Name} ({list.Count} items)");
                                    for (int i = 0; i < Math.Min(list.Count, 5); i++)
                                        Console.WriteLine($"[TurzxSensorBridge]     [{i}] {list[i]}");
                                }
                                else if (val is IDictionary dict && dict.Count >= 2 && dict.Count <= 200)
                                {
                                    Console.WriteLine($"[TurzxSensorBridge]   {tn}.{f.Name}: {f.FieldType.Name} ({dict.Count} entries)");
                                }
                                else if (val is Array arr && arr.Length >= 3 && arr.Length <= 200)
                                {
                                    Console.WriteLine($"[TurzxSensorBridge]   {tn}.{f.Name}: {f.FieldType.Name} ({arr.Length} items)");
                                    for (int i = 0; i < Math.Min(arr.Length, 5); i++)
                                        Console.WriteLine($"[TurzxSensorBridge]     [{i}] {arr.GetValue(i)}");
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }
                Console.WriteLine("[TurzxSensorBridge] === DIAG end ===");
            }
            catch { }
        }

        private void DeferredDiagnostic()
        {
            try
            {
                Console.WriteLine("[TurzxSensorBridge] DeferredDiagnostic START");
                string dumpPath = Path.Combine(Path.GetTempPath(), "turzsensor_dump.txt");
                using var sw = new StreamWriter(dumpPath, false);
                sw.WriteLine($"=== TURZX Sensor Dump {DateTime.Now} ===");
                Console.WriteLine($"[TurzxSensorBridge] Writing dump to: {dumpPath}");

                sw.WriteLine($"--- M_Data collections ({_mDataCollections.Count} refs) ---");
                foreach (var col in _mDataCollections)
                {
                    sw.WriteLine($"ObservableCollection<M_Data> ref #{_mDataCollections.IndexOf(col)}: {col.Count} items");
                    Console.WriteLine($"[TurzxSensorBridge]   Collection has {col.Count} items");
                    for (int i = 0; i < col.Count; i++)
                    {
                        try
                        {
                            var item = col[i];
                            if (item == null) { sw.WriteLine($"  [{i}] NULL"); continue; }
                            sw.WriteLine($"  [{i}] DataName={_dataNameProp?.GetValue(item)}, Value={_valueProp?.GetValue(item)}");
                        }
                        catch { sw.WriteLine($"  [{i}] [error accessing]"); }
                    }
                }

                sw.WriteLine($"\n--- All static M_Data IList fields ---");
                var allTypes2 = _turzxAssembly.GetTypes();
                foreach (var type in allTypes2)
                {
                    try
                    {
                        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                        foreach (var f in fields)
                        {
                            try
                            {
                                var ft = f.FieldType;
                                if (ft.IsGenericType)
                                {
                                    var genArgs = ft.GetGenericArguments();
                                    if (genArgs.Length != 1) continue;
                                    if (genArgs[0].FullName != _mDataType?.FullName) continue;
                                    if (!ft.Name.StartsWith("List`") && !ft.Name.StartsWith("IList`") && !ft.Name.StartsWith("Collection`") && !ft.Name.StartsWith("IEnumerable`")) continue;
                                    var val = f.GetValue(null);
                                    if (val is IList list)
                                    {
                                        sw.WriteLine($"  {type.Name}.{f.Name} ({ft.Name}): {list.Count} items");
                                        for (int i = 0; i < Math.Min(list.Count, 5); i++)
                                            sw.WriteLine($"    [{i}] {list[i]?.GetType().Name} dn={_dataNameProp?.GetValue(list[i])}");
                                    }
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                sw.WriteLine($"\n--- All static non-generic M_Data-related fields ---");
                foreach (var type in allTypes2)
                {
                    try
                    {
                        var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
                        foreach (var f in fields)
                        {
                            try
                            {
                                if (f.FieldType == _mDataType)
                                {
                                    sw.WriteLine($"  {type.Name}.{f.Name} (single M_Data)");
                                }
                            }
                            catch { }
                        }
                    }
                    catch { }
                }

                sw.WriteLine("\n--- String lists ---");
                foreach (var sl in _stringLists)
                {
                    var strs = new List<string>();
                    foreach (var s in sl) if (s is string str) strs.Add(str);
                    sw.WriteLine($"List<string>: {sl.Count} items: {string.Join(", ", strs)}");
                }

                sw.WriteLine("=== END ===");
                sw.Flush();
                Console.WriteLine($"[TurzxSensorBridge] Dump written to: {dumpPath}");
            }
            catch (Exception ex) { Console.WriteLine($"[TurzxSensorBridge] DeferredDiag error: {ex.GetType().Name}: {ex.Message}"); }
        }

        private static List<FieldInfo> FindObservableCollectionFields(Type type, string targetGenericArgFullName)
        {
            var result = new List<FieldInfo>();
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.NonPublic);
            foreach (var field in fields)
            {
                try
                {
                    var ft = field.FieldType;
                    if (!ft.IsGenericType) continue;
                    if (!ft.Name.StartsWith("ObservableCollection`")) continue;

                    var typeArgs = ft.GetGenericArguments();
                    if (typeArgs.Length != 1) continue;

                    if (typeArgs[0].FullName == targetGenericArgFullName)
                        result.Add(field);
                }
                catch { }
            }
            return result;
        }
    }
}
