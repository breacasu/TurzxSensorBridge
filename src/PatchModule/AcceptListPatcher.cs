using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;

namespace PatchModule
{
    /// <summary>
    /// Solves the real "Data Source" ComboBox problem.
    ///
    /// Root cause (confirmed via dnSpy decompilation of TURZX.exe, see
    /// reference\decompiled\UsbMonitorL\ThemeEditForm.xaml.cs around line 3276
    /// and GraphItem.cs):
    ///
    ///   - The "DataSourceBox" ComboBox in ThemeEditForm IS populated from
    ///     the same M_Data.&lt;static field&gt; ObservableCollection&lt;M_Data&gt; that
    ///     DataSourceInjector already fills with our custom sensors.
    ///   - BUT the population loop filters entries:
    ///         if (currentItem.AcceptDataList.Contains(m_Data.DataName)) { ... Items.Add(m_Data); }
    ///     Every GraphItem (widget) instance builds its own hardcoded
    ///     AcceptDataList (List&lt;string&gt;, public get/set) in its constructor
    ///     with ~40 built-in sensor names. Our injected sensors are never in
    ///     that list, so they get filtered out of the ComboBox even though
    ///     they exist in the M_Data collection.
    ///
    /// Fix: periodically walk Monitor.MonitorList (a static, always-available
    /// field - no window/Application.Current lookup needed) -&gt; each
    /// Monitor.currentTheme / editorTheme -&gt; Theme.GraphList
    /// (ObservableCollection&lt;GraphItem&gt;) -&gt; GraphItem.AcceptDataList, and add
    /// our sensor alias names if missing. This is far more robust than
    /// hooking the WPF window/ComboBox directly: no Application.Current
    /// thread-static issues, no EnumWindows/VisualTreeHelper timing races,
    /// and it keeps working even if the user opens/closes the editor
    /// multiple times or switches devices.
    /// </summary>
    public class AcceptListPatcher
    {
        private readonly Assembly _turzxAssembly;
        private Type? _monitorType;
        private Type? _themeType;
        private Type? _graphItemType;
        private Type? _mDataType;

        private PropertyInfo? _monitorListProp;
        private PropertyInfo? _currentThemeProp;
        private PropertyInfo? _editorThemeProp;
        private PropertyInfo? _graphListProp;
        private PropertyInfo? _acceptDataListProp;
        private PropertyInfo? _dataNameProp;
        private PropertyInfo? _mDataDisplayNameProp;
        private PropertyInfo? _mDataValueProp;
        private PropertyInfo? _graphItemMDataProp;
        private PropertyInfo? _graphItemDisplayNameProp;
        private PropertyInfo? _graphItemTypeNameProp;

        private readonly List<string> _aliases = new();
        private readonly Dictionary<string, object> _sensorObjects = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _displayNameByDataName = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _formattedValueByDataName = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _aliasLock = new();

        private Timer? _timer;
        private bool _loggedFirstPatch;
        private bool _loggedNullMonitorList;
        private bool _loggedCounts;
        private bool _loggedVerification;

        public bool IsInitialized { get; private set; }
        public string InitializationError { get; private set; } = string.Empty;

        public AcceptListPatcher(Assembly turzxAssembly)
        {
            _turzxAssembly = turzxAssembly;
        }

        public bool TryInitialize()
        {
            try
            {
                _monitorType = _turzxAssembly.GetType("UsbMonitorL.Monitor");
                _themeType = _turzxAssembly.GetType("UsbMonitorL.Theme");
                _graphItemType = _turzxAssembly.GetType("UsbMonitorL.GraphItem");
                _mDataType = _turzxAssembly.GetType("UsbMonitorL.M_Data");

                if (_monitorType == null || _themeType == null || _graphItemType == null || _mDataType == null)
                {
                    InitializationError = "One or more required types (Monitor/Theme/GraphItem/M_Data) not found";
                    return false;
                }

                _monitorListProp = _monitorType.GetProperty("MonitorList", BindingFlags.Public | BindingFlags.Static);
                _currentThemeProp = _monitorType.GetProperty("currentTheme", BindingFlags.Public | BindingFlags.Instance);
                _editorThemeProp = _monitorType.GetProperty("editorTheme", BindingFlags.Public | BindingFlags.Instance);
                _graphListProp = _themeType.GetProperty("GraphList", BindingFlags.Public | BindingFlags.Instance);
                _acceptDataListProp = _graphItemType.GetProperty("AcceptDataList", BindingFlags.Public | BindingFlags.Instance);
                _dataNameProp = _mDataType.GetProperty("DataName", BindingFlags.Public | BindingFlags.Instance);
                _mDataDisplayNameProp = _mDataType.GetProperty("DisplayName", BindingFlags.Public | BindingFlags.Instance);
                _mDataValueProp = _mDataType.GetProperty("Value", BindingFlags.Public | BindingFlags.Instance);
                _graphItemMDataProp = _graphItemType.GetProperty("m_data", BindingFlags.Public | BindingFlags.Instance);
                _graphItemDisplayNameProp = _graphItemType.GetProperty("DisplayName", BindingFlags.Public | BindingFlags.Instance);
                _graphItemTypeNameProp = _graphItemType.GetProperty("TypeName", BindingFlags.Public | BindingFlags.Instance);

                if (_monitorListProp == null || _graphListProp == null || _acceptDataListProp == null)
                {
                    InitializationError = "Required properties (MonitorList/GraphList/AcceptDataList) not found";
                    return false;
                }

                IsInitialized = true;
                return true;
            }
            catch (Exception ex)
            {
                InitializationError = ex.Message;
                return false;
            }
        }

        /// <summary>Replace the full set of sensor alias names to whitelist.</summary>
        public void SetAliases(IEnumerable<string> aliases)
        {
            lock (_aliasLock)
            {
                _aliases.Clear();
                foreach (var a in aliases)
                {
                    if (string.IsNullOrEmpty(a)) continue;
                    bool exists = false;
                    foreach (var existing in _aliases)
                    {
                        if (string.Equals(existing, a, StringComparison.OrdinalIgnoreCase)) { exists = true; break; }
                    }
                    if (!exists) _aliases.Add(a);
                }
            }
        }

        /// <summary>Replace the full set of live M_Data sensor objects (alias -> object),
        /// used to insert them directly into an already-populated DataSourceBox.Items
        /// (the ComboBox is only populated once when a widget is selected in
        /// ThemeEditForm, so merely patching AcceptDataList is not enough for
        /// widgets whose ComboBox was already filled before our sensors existed).</summary>
        public void SetSensorObjects(IReadOnlyDictionary<string, object> sensorObjects)
        {
            lock (_aliasLock)
            {
                _sensorObjects.Clear();
                foreach (var kv in sensorObjects)
                    _sensorObjects[kv.Key] = kv.Value;
            }
        }

        /// <summary>Replace the DataName -> human-readable-alias map, used to
        /// restore M_Data.DisplayName / GraphItem.DisplayName after TURZX's
        /// own code overwrites them with an empty string (failed
        /// "T_DataType_&lt;DataName&gt;" translation lookup for our custom
        /// sensors - see M_Data.RefreshDisplayName / GraphItem.RefreshDisplayName,
        /// invoked whenever the user selects a sensor in the Theme Editor's
        /// Data Source ComboBox).</summary>
        public void SetDisplayNames(IReadOnlyDictionary<string, string> displayNameByDataName)
        {
            lock (_aliasLock)
            {
                _displayNameByDataName.Clear();
                foreach (var kv in displayNameByDataName)
                    _displayNameByDataName[kv.Key] = kv.Value;
            }
        }

        /// <summary>Replace the DataName -> formatted current value-string map
        /// (e.g. "27.66"). Used to push live sensor values directly into
        /// every rendered GraphItem's m_data.Value (see PushLiveValueIfOurSensor)
        /// instead of relying on TURZX's own per-frame update loop, whose
        /// generic fallback for unrecognized sensor names
        /// (M_Data.Lookup(DataName) -&gt; copy Value into the clone) only runs
        /// once after a sensor is newly selected and otherwise leaves the
        /// clone's Value frozen at whatever it was at that moment - built-in
        /// sensors get fresh values every frame via dedicated per-DataName
        /// cases in that same switch, custom sensors do not.</summary>
        public void SetFormattedValues(IReadOnlyDictionary<string, string> formattedValueByDataName)
        {
            lock (_aliasLock)
            {
                _formattedValueByDataName.Clear();
                foreach (var kv in formattedValueByDataName)
                    _formattedValueByDataName[kv.Key] = kv.Value;
            }
        }

        /// <summary>Start periodic patching (every 500ms) so newly created GraphItems
        /// (e.g. when the user adds a new widget in the editor) get patched
        /// too, and so DisplayName corrections (see FixDisplayNameIfOurSensor)
        /// apply almost immediately after the user selects a sensor in the
        /// Data Source ComboBox, instead of being visibly delayed. 500ms is a
        /// middle ground between fast enough reaction after selecting a
        /// sensor and not needlessly re-checking values more often than
        /// TURZX's own 1000ms sensor update cycle (confirmed via dnSpy -
        /// TURZX's per-theme update loop does Thread.Sleep(1000) between
        /// refresh cycles) actually produces new values.</summary>
        public void StartPatching()
        {
            if (!IsInitialized) return;
            _timer = new Timer(_ => SafePatchOnce(), null, 0, 500);
            Console.WriteLine("[TurzxSensorBridge] AcceptListPatcher: periodic patching started (every 500ms)");
        }

        private void SafePatchOnce()
        {
            try
            {
                PatchOnce();
            }
            catch (Exception ex)
            {
                Console.WriteLine("[TurzxSensorBridge] AcceptListPatcher error: " + ex.Message);
            }
        }

        private void PatchOnce()
        {
            List<string> aliasesSnapshot;
            lock (_aliasLock)
            {
                if (_aliases.Count == 0) return;
                aliasesSnapshot = new List<string>(_aliases);
            }

            var monitorList = _monitorListProp!.GetValue(null) as IEnumerable;
            if (monitorList == null)
            {
                if (!_loggedNullMonitorList)
                {
                    _loggedNullMonitorList = true;
                    Console.WriteLine("[TurzxSensorBridge] AcceptListPatcher: Monitor.MonitorList is null");
                }
                return;
            }

            int patchedItems = 0;
            int patchedThemes = 0;
            int monitorCount = 0;
            int themeCount = 0;
            int graphItemCount = 0;
            object? sampleGraphItem = null;

            foreach (var monitor in monitorList)
            {
                monitorCount++;
                if (monitor == null) continue;

                var themes = new List<object>();
                var t1 = _currentThemeProp?.GetValue(monitor);
                if (t1 != null) themes.Add(t1);
                var t2 = _editorThemeProp?.GetValue(monitor);
                if (t2 != null && t2 != t1) themes.Add(t2);

                foreach (var theme in themes)
                {
                    themeCount++;
                    var graphList = _graphListProp!.GetValue(theme) as IEnumerable;
                    if (graphList == null) continue;

                    bool themeTouched = false;
                    foreach (var graphItem in graphList)
                    {
                        if (graphItem == null) continue;
                        graphItemCount++;
                        sampleGraphItem ??= graphItem;
                        if (PatchAcceptList(graphItem, aliasesSnapshot))
                        {
                            patchedItems++;
                            themeTouched = true;
                        }
                        FixDisplayNameIfOurSensor(graphItem);
                        PushLiveValueIfOurSensor(graphItem);
                    }
                    if (themeTouched) patchedThemes++;
                }
            }

            if (!_loggedCounts)
            {
                _loggedCounts = true;
                Console.WriteLine($"[TurzxSensorBridge] AcceptListPatcher: scanned {monitorCount} monitor(s), {themeCount} theme(s), {graphItemCount} GraphItem(s)");
            }

            if ((patchedItems > 0 || patchedThemes > 0) && !_loggedFirstPatch)
            {
                _loggedFirstPatch = true;
                Console.WriteLine($"[TurzxSensorBridge] AcceptListPatcher: patched {patchedItems} GraphItem(s) across {patchedThemes} theme(s) with {aliasesSnapshot.Count} sensor alias(es)");
            }

            // Verification: read back AcceptDataList on a sample GraphItem to
            // conclusively prove our mutation is visible on the actual live
            // object (not a stale/duplicate/wrong-instance list). Uses the
            // exact same List<string>.Contains() semantics the game itself
            // uses in ThemeEditForm (case-sensitive ordinal), to rule out
            // any silent case-mismatch false negative.
            if (sampleGraphItem != null && aliasesSnapshot.Count > 0 && !_loggedVerification)
            {
                _loggedVerification = true;
                try
                {
                    var list = _acceptDataListProp!.GetValue(sampleGraphItem) as IList;
                    string probe = aliasesSnapshot[0];
                    bool exactContains = false;
                    if (list != null)
                    {
                        foreach (var item in list)
                        {
                            if (item is string s && s == probe) { exactContains = true; break; }
                        }
                    }
                    Console.WriteLine($"[TurzxSensorBridge] AcceptListPatcher: VERIFY sample GraphItem type={sampleGraphItem.GetType().Name}, AcceptDataList.Count={list?.Count ?? -1}, exact-Contains('{probe}')={exactContains}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine("[TurzxSensorBridge] AcceptListPatcher: VERIFY error: " + ex.Message);
                }
            }

            // Live fix-up of the actual DataSourceBox ComboBox, if the Theme
            // Editor window happens to be open right now. Confirmed via
            // diagnostics that AcceptDataList patching alone is NOT enough:
            // ThemeEditForm only populates DataSourceBox.Items once, at the
            // moment a widget is selected (SelectionChanged handler). If our
            // sensors weren't in AcceptDataList yet at that exact moment, the
            // already-built Items list never gets refreshed afterwards - even
            // if AcceptDataList is patched correctly a moment later. So we
            // directly insert any missing M_Data sensor objects into
            // DataSourceBox.Items here as well, whenever the editor is open.
            FixDataSourceBoxIfEditorOpen(aliasesSnapshot);
        }

        private bool _loggedEditorNotFound;
        private DateTime _lastEditorInspect = DateTime.MinValue;

        private void FixDataSourceBoxIfEditorOpen(List<string> aliasesSnapshot)
        {
            try
            {
                // Redundant with the 500ms outer timer in StartPatching(),
                // kept as a safety net in case PatchOnce() is ever called
                // more frequently in the future.
                if ((DateTime.Now - _lastEditorInspect).TotalMilliseconds < 500) return;
                _lastEditorInspect = DateTime.Now;

                Type? appType = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name == "PresentationFramework")
                    {
                        appType = a.GetType("System.Windows.Application");
                        break;
                    }
                }
                if (appType == null) return;

                var currentProp = appType.GetProperty("Current", BindingFlags.Public | BindingFlags.Static);
                var app = currentProp?.GetValue(null);
                if (app == null) return;

                // WPF UI objects (Window, ComboBox, ItemCollection, ...) can
                // only be accessed from the UI thread. This method runs on a
                // System.Threading.Timer background thread, so we must
                // marshal the actual read/write through the Dispatcher via
                // Invoke (synchronous) to avoid
                // InvalidOperationException("The calling thread cannot
                // access this object because a different thread owns it").
                var dispatcherProp = appType.GetProperty("Dispatcher");
                var dispatcher = dispatcherProp?.GetValue(app);
                if (dispatcher == null) return;

                var invokeMethod = dispatcher.GetType().GetMethod("Invoke", new[] { typeof(Action) });
                if (invokeMethod == null) return;

                Dictionary<string, object> sensorObjectsSnapshot;
                lock (_aliasLock) { sensorObjectsSnapshot = new Dictionary<string, object>(_sensorObjects, StringComparer.OrdinalIgnoreCase); }

                invokeMethod.Invoke(dispatcher, new object[] { (Action)(() => FixDataSourceBoxOnUiThread(appType, app, aliasesSnapshot, sensorObjectsSnapshot)) });
            }
            catch (Exception ex)
            {
                string detail = ex is TargetInvocationException tie && tie.InnerException != null
                    ? tie.InnerException.GetType().Name + " - " + tie.InnerException.Message
                    : ex.GetType().Name + " - " + ex.Message;
                Console.WriteLine("[TurzxSensorBridge] FixDataSourceBoxIfEditorOpen error: " + detail);
            }
        }

        private void FixDataSourceBoxOnUiThread(Type appType, object app, List<string> aliasesSnapshot, Dictionary<string, object> sensorObjectsSnapshot)
        {
            try
            {
                var windowsProp = appType.GetProperty("Windows");
                var windows = windowsProp?.GetValue(app) as IEnumerable;
                if (windows == null) return;

                object? editorWindow = null;
                foreach (var w in windows)
                {
                    if (w != null && w.GetType().Name == "ThemeEditForm") { editorWindow = w; break; }
                }
                if (editorWindow == null)
                {
                    if (!_loggedEditorNotFound)
                    {
                        _loggedEditorNotFound = true;
                        Console.WriteLine("[TurzxSensorBridge] DIAG: ThemeEditForm window not currently open (this message only prints once)");
                    }
                    return;
                }

                var dsBoxField = editorWindow.GetType().GetField("DataSourceBox", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                object? dsBox = dsBoxField?.GetValue(editorWindow);
                if (dsBox == null) { Console.WriteLine("[TurzxSensorBridge] DIAG: DataSourceBox field not found on ThemeEditForm"); return; }

                var itemsProp = dsBox.GetType().GetProperty("Items");
                var items = itemsProp?.GetValue(dsBox) as IList;
                if (items == null) { Console.WriteLine("[TurzxSensorBridge] DIAG: DataSourceBox.Items is null"); return; }

                var currentItemField = editorWindow.GetType().GetField("currentItem", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var currentItem = currentItemField?.GetValue(editorWindow);
                if (currentItem == null) return;

                var acceptList = _acceptDataListProp!.GetValue(currentItem) as IList;

                // Determine which of our sensors are already present (by DataName)
                var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in items)
                {
                    var dn = _dataNameProp?.GetValue(item) as string;
                    if (!string.IsNullOrEmpty(dn)) present.Add(dn);
                }

                int added = 0;
                foreach (var alias in aliasesSnapshot)
                {
                    if (present.Contains(alias)) continue;
                    // Only add if the currently selected widget actually
                    // accepts this sensor (mirrors the game's own filtering
                    // logic in ThemeEditForm - AcceptDataList.Contains check).
                    if (acceptList != null && !Contains(acceptList, alias)) continue;
                    if (!sensorObjectsSnapshot.TryGetValue(alias, out var sensorObj)) continue;

                    items.Add(sensorObj);
                    added++;
                }

                if (added > 0)
                {
                    Console.WriteLine($"[TurzxSensorBridge] FixDataSourceBox: added {added} missing sensor(s) directly into DataSourceBox.Items (now {items.Count} total)");
                }
            }
            catch (Exception ex)
            {
                string detail = ex is TargetInvocationException tie && tie.InnerException != null
                    ? tie.InnerException.GetType().Name + " - " + tie.InnerException.Message
                    : ex.GetType().Name + " - " + ex.Message;
                Console.WriteLine("[TurzxSensorBridge] FixDataSourceBoxOnUiThread error: " + detail);
            }
        }

        private bool PatchAcceptList(object graphItem, List<string> aliasesSnapshot)
        {
            try
            {
                var list = _acceptDataListProp!.GetValue(graphItem) as IList;
                if (list == null)
                {
                    // AcceptDataList should always be initialized by the GraphItem
                    // constructor, but guard against null just in case.
                    list = new List<string>();
                    _acceptDataListProp.SetValue(graphItem, list);
                }

                bool changed = false;
                foreach (var alias in aliasesSnapshot)
                {
                    if (!Contains(list, alias))
                    {
                        list.Add(alias);
                        changed = true;
                    }
                }
                return changed;
            }
            catch
            {
                return false;
            }
        }

        private bool _loggedDisplayNameFix;

        /// <summary>
        /// After the user selects one of our sensors in the Data Source
        /// ComboBox, TURZX calls M_Data.RefreshDisplayName() which does
        /// DisplayName = Translate("T_DataType_" + DataName). No such
        /// translation key exists for our custom sensors, so the lookup
        /// silently resolves to an empty string, wiping out the DisplayName
        /// we set at injection time. Since the "Elements" list column
        /// (ThemeGraphList) renders each GraphItem's own DisplayName as
        /// "{TypeName}--{m_data.DisplayName}" (confirmed via dnSpy,
        /// GraphItem.cs ~line 1298), this makes selected custom sensors show
        /// up with a blank second half (e.g. "Data--" instead of
        /// "Data--Quadro Temp 1"), unlike the built-in sensors. We restore
        /// both M_Data.DisplayName and GraphItem.DisplayName here every
        /// patch cycle (every 2s) so the fix applies shortly after selection
        /// regardless of exactly when TURZX's own refresh logic runs.
        /// </summary>
        private void FixDisplayNameIfOurSensor(object graphItem)
        {
            try
            {
                if (_graphItemMDataProp == null || _mDataDisplayNameProp == null || _dataNameProp == null) return;

                var mData = _graphItemMDataProp.GetValue(graphItem);
                if (mData == null) return;

                var dataName = _dataNameProp.GetValue(mData) as string;
                if (string.IsNullOrEmpty(dataName)) return;

                string? ourDisplayName;
                lock (_aliasLock)
                {
                    if (!_displayNameByDataName.TryGetValue(dataName, out ourDisplayName)) return;
                }

                var currentDisplayName = _mDataDisplayNameProp.GetValue(mData) as string;
                bool mDataChanged = false;
                if (currentDisplayName != ourDisplayName)
                {
                    _mDataDisplayNameProp.SetValue(mData, ourDisplayName);
                    mDataChanged = true;
                }

                if (_graphItemDisplayNameProp != null && _graphItemTypeNameProp != null)
                {
                    var typeName = _graphItemTypeNameProp.GetValue(graphItem) as string ?? "";
                    string expected = typeName + "--" + ourDisplayName;
                    var currentGraphItemDisplayName = _graphItemDisplayNameProp.GetValue(graphItem) as string;
                    if (currentGraphItemDisplayName != expected)
                    {
                        _graphItemDisplayNameProp.SetValue(graphItem, expected);
                        mDataChanged = true;
                    }
                }

                if (mDataChanged && !_loggedDisplayNameFix)
                {
                    _loggedDisplayNameFix = true;
                    Console.WriteLine($"[TurzxSensorBridge] FixDisplayNameIfOurSensor: restored DisplayName for '{dataName}' -> '{ourDisplayName}'");
                }
            }
            catch (Exception ex)
            {
                if (!_loggedDisplayNameFix)
                {
                    _loggedDisplayNameFix = true;
                    Console.WriteLine("[TurzxSensorBridge] FixDisplayNameIfOurSensor error: " + ex.Message);
                }
            }
        }

        private bool _loggedValuePush;

        /// <summary>
        /// Pushes the current live sensor value string directly into
        /// graphItem.m_data.Value (the CLONE that widgets actually render -
        /// distinct from the original object in M_Data.&lt;static field&gt; that
        /// DataSourceInjector.UpdateSensorValue writes to). Confirmed via
        /// dnSpy + live debugging that TURZX's own per-frame update loop
        /// only refreshes this clone's Value once via its generic fallback
        /// path for unrecognized DataName values (immediately after the
        /// user selects the sensor) - it does NOT keep refreshing it every
        /// frame like it does for built-in sensors (which each have a
        /// dedicated case in that same switch that re-reads the live
        /// hardware value every tick). Without this push, a custom sensor's
        /// displayed value freezes at whatever it was the instant it was
        /// selected, even though our own M_Data collection entry keeps
        /// updating correctly in the background.
        /// </summary>
        private void PushLiveValueIfOurSensor(object graphItem)
        {
            try
            {
                if (_graphItemMDataProp == null || _dataNameProp == null || _mDataValueProp == null) return;

                var mData = _graphItemMDataProp.GetValue(graphItem);
                if (mData == null) return;

                var dataName = _dataNameProp.GetValue(mData) as string;
                if (string.IsNullOrEmpty(dataName)) return;

                string? formattedValue;
                lock (_aliasLock)
                {
                    if (!_formattedValueByDataName.TryGetValue(dataName, out formattedValue)) return;
                }

                var currentValue = _mDataValueProp.GetValue(mData) as string;
                if (currentValue != formattedValue)
                {
                    _mDataValueProp.SetValue(mData, formattedValue);
                }
            }
            catch (Exception ex)
            {
                if (!_loggedValuePush)
                {
                    _loggedValuePush = true;
                    Console.WriteLine("[TurzxSensorBridge] PushLiveValueIfOurSensor error: " + ex.Message);
                }
            }
        }

        private static bool Contains(IList list, string value)
        {
            foreach (var item in list)
            {
                if (item is string s && string.Equals(s, value, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
