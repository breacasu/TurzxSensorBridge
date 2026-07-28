# TurzxSensorBridge — Technical Documentation

For a user-friendly overview, see [README.md](README.md).

## Overview

TurzxSensorBridge enables custom hardware sensors to be used in the TURZX screen theme editor's "Data Source" dropdown, alongside the built-in sensors. It injects sensor data directly into TURZX's own data model via a plugin loaded by TurzxPatcher - no modification of TURZX.exe itself.

### Hard Dependency: TurzxPatcher

TurzxSensorBridge has a hard runtime dependency on [TurzxPatcher](https://github.com/breacasu/TurzxPatcher). The sensor-to-TURZX integration happens exclusively through `PatchModule.dll`, which is a plugin loaded by TurzxPatcher's plugin system. Without TurzxPatcher, `SensorConfig.exe` and `SensorService.exe` run independently, but the sensors never appear in TURZX's Theme Editor.

## Architecture

```
┌─────────────────┐     ┌───────────────────┐     ┌─────────────────────────────┐
│  Aquacomputer   │────▶│   SensorService   │────▶│         TurzxPatcher        │
│  Devices (USB)  │     │  (Named Pipe,     │     │  ┌───────────────────────┐  │
│                 │     │   unidirectional  │     │  │  PatchModule.dll      │  │
│                 │     │   push, 1x/sec)   │     │  │  (plugin loaded into  │  │
└─────────────────┘     └───────────────────┘     │  │   TURZX — does ALL    │  │
                                 │                │  │   the injection:      │  │
                                 ▼                │  │   M_Data creation,    │  │
                        ┌───────────────────┐     │  │   AcceptDataList      │  │
                        │  LibreHwAccess    │     │  │   patch, live value   │  │
                        │  (LibreHardware-  │     │  │   push, Rate calc)    │  │
                        │  Monitor driver)  │     │  └───────────────────────┘  │
                        └───────────────────┘     └─────────────────────────────┘
                                                               │
                                                               ▼
                                                     ┌──────────────────┐
                                                     │   TURZX.exe      │
                                                     │  (Theme Editor   │
                                                     │  displays        │
                                                     │  sensor values)  │
                                                     └──────────────────┘
```

## Components

### 1. LibreHwAccess Library
- **Location:** `src/LibreHwAccess/`
- **Purpose:** Reads hardware sensors directly via LibreHardwareMonitorLib (NuGet), independent of any third-party monitoring tool's UI state.
- **Key Classes:** `LibreHwReader`, `LibreSensorReading`
- **Note:** Requires `RuntimeIdentifier=win-x64` in the `.csproj` so the RID-specific native implementation DLL is copied to the output directory.

### 2. HwInfoAccess Library (legacy, kept for reference)
- **Location:** `src/HwInfoAccess/`
- **Purpose:** Reads HWiNFO Shared Memory data. No longer used by `SensorService`, but kept as a reference/fallback implementation.

### 3. SensorService (Background Service)
- **Location:** `src/SensorService/`
- **Purpose:** Reads sensors via `LibreHwAccess` and pushes matched, configured sensors as JSON over a named pipe once per second.
- **Pipe Name:** `TurzxSensorBridge` (unidirectional, `PipeDirection.Out`)
- **Config file:** `%APPDATA%\TurzxSensorBridge\selected_sensors.json`

### 4. PatchModule (TURZX Plugin)
- **Location:** `src/PatchModule/`
- **Purpose:** Injects custom sensors into TURZX and makes them selectable/functional in the Theme Editor.
- **Interface:** Implements `ITurzxPatch` from the shared interface (`shared/ITurzxPatch.cs`).
- **Key classes:**
  - `SensorBridgePatch.EnsureSensorServiceRunning()` - auto-starts `SensorService.exe` and registers `AppDomain.ProcessExit` to kill it when TURZX exits.
  - `DataSourceInjector` - creates `M_Data` objects for each configured sensor via the real constructor (not `FormatterServices.GetUninitializedObject`, so internal fields like `DataQueue` are properly initialized) and adds them to TURZX's static `ObservableCollection<M_Data>`. Uses XPath-safe sanitized DataNames to prevent XPathExceptions.
  - `AcceptListPatcher` - patches each widget's (`GraphItem`) hardcoded `AcceptDataList` whitelist, directly fixes up `DataSourceBox.Items`, restores `DisplayName` after TURZX's translation lookup wipes it, pushes live values into each rendered widget's `m_data.Value` every 500ms, and computes `m_data.Rate` from configured min/max.
  - `PipeClient` - connects to `SensorService` and receives the pushed JSON sensor data.

### 5. SensorConfig (WPF Configuration UI)
- **Location:** `src/SensorConfig/`
- **Purpose:** Graphical user interface for configuring which sensors appear in TURZX, assigning aliases, and setting min/max values. Reads all available sensors via LibreHardwareMonitor (same library as SensorService, so names always match).

## Timing

TURZX's own per-theme update loop refreshes sensor values (built-in and custom) once per second (`Thread.Sleep(1000)` between cycles, confirmed via dnSpy). `SensorService` polls LibreHardwareMonitor and pushes new values over the pipe on the same 1-second interval. `AcceptListPatcher`'s 500ms patch cycle only controls how quickly a *newly received* value is written into the currently rendered widget and how quickly ComboBox/DisplayName fixes apply.

## Rate Calculation (Bar Fill)

TURZX's `M_Data.Rate` (double, 0.0–1.0) drives progress-bar/gauge fill percentage:
```
num4 = (int)((double)this.width * base.m_data.Rate);   // GraphStatuBar.cs:2468
```

For built-in sensors, TURZX's own update loop sets Rate using hardcoded divisors per DataName (e.g. `num5 / 100.0` for temperature, `num5 / 6000.0` for fan RPM). Custom sensors get no such treatment — they fall through to TURZX's generic path which never sets Rate.

TurzxSensorBridge fills this gap in `AcceptListPatcher.PushLiveValueIfOurSensor`:
```
rate = (sensorValue - min) / (max - min)
```
Clamped to 0.0–1.0. For percent sensors without configured min/max, auto-defaults to 0–100.

Rate is set exclusively by AcceptListPatcher, NOT by DataSourceInjector.UpdateSensorValue (which previously set `M_Data.Rate` to the raw sensor value, causing bar overflow).

## JSON Config Schema

`%APPDATA%\TurzxSensorBridge\selected_sensors.json`:
```json
{
  "selectedSensors": [
    {
      "alias": "D5 Water Temp",
      "labelOrig": "Water Temperature",
      "deviceName": "D5Next",
      "readingType": "Temperature",
      "min": 20,
      "max": 45
    }
  ]
}
```

- `alias`: Name shown in TURZX's Data Source dropdown (and used as DataName after XPath sanitization).
- `labelOrig`, `deviceName`, `readingType`: Must match a sensor reported by LibreHardwareMonitor exactly.
- `readingType` is required when a device exposes multiple sensors with identical labels under different reading types.
- `min`, `max` (optional, nullable): Range for bar/gauge fill calculation. Auto-defaults to 0–100 for `%` unit sensors.

## IPC Protocol (Named Pipe)

- **Pipe Name:** `TurzxSensorBridge`
- **Direction:** Server → Client (unidirectional push)
- **Format:** JSON, one message per second
- **Message structure:**
```json
{
  "timestamp": "2026-07-15T14:30:00.0000000Z",
  "sensors": [
    {
      "alias": "D5 Water Temp",
      "labelOrig": "Water Temperature",
      "labelUser": "Water Temperature",
      "deviceName": "D5Next",
      "value": 28.5,
      "unit": "°C",
      "readingType": "Temperature",
      "min": 20,
      "max": 45,
      "isStale": false
    }
  ]
}
```

`min` and `max` are included only when configured (not null). `isStale` is `true` when LibreHardwareMonitor is unavailable.

## Building from Source

```powershell
git clone https://github.com/breacasu/TurzxSensorBridge.git
cd TurzxSensorBridge
dotnet build TurzxSensorBridge.sln -c Release
```

### Deployment

Copy built files into `patches\` subfolder inside your TURZX installation directory:
```
TURZX installation directory\
└── patches\
    ├── PatchModule.dll                ← from src\PatchModule\bin\Release\net48\
    ├── SensorService\                 ← from src\SensorService\bin\Release\net48\win-x64\
    │   ├── SensorService.exe
    │   ├── LibreHardwareMonitorLib.dll
    │   └── (all other files from that output directory)
    └── SensorConfig\                  ← from src\SensorConfig\bin\Release\net48\win-x64\
        ├── SensorConfig.exe
        └── (all other files from that output directory)
```

### Project Structure

```
TurzxSensorBridge/
├── assets/
│   └── icons/
│       └── icon.svg            # Source vector icon
├── src/
│   ├── LibreHwAccess/         # LibreHardwareMonitor-based sensor reader (active)
│   ├── HwInfoAccess/          # HWiNFO Shared Memory API (legacy/reference)
│   ├── SensorConfig/          # WPF configuration UI for sensor mapping
│   ├── SensorService/         # Named pipe server, pushes sensor JSON
│   ├── PatchModule/           # TURZX plugin (ITurzxPatch implementation)
├── tests/
│   ├── LibreHwTest/           # Standalone tool to list all LibreHardwareMonitor sensors
│   └── HwInfoTest/            # Standalone tool to list HWiNFO Shared Memory sensors (legacy)
├── shared/
│   └── ITurzxPatch.cs         # Shared plugin interface (must be byte-identical in TurzxPatcher repo)
├── docs/                      # Internal technical documents
├── tools/                     # Build/deploy/test helper scripts
├── TurzxSensorBridge.sln
├── README.md                  # User-facing documentation
├── TECHNICAL.md               # This file
└── LICENSE
```

### Key Files

- `shared/ITurzxPatch.cs` - Plugin interface (must be identical in TurzxPatcher repo)
- `src/PatchModule/DataSourceInjector.cs` - Creates M_Data objects for each configured sensor
- `src/PatchModule/AcceptListPatcher.cs` - Patches widget AcceptDataList whitelist, pushes live values, computes Rate
- `src/PatchModule/SensorBridgePatch.cs` - Main patch class, auto-starts/kills SensorService, parses pipe JSON
- `src/SensorService/SensorPollingLoop.cs` - Server-side sensor matching + JSON push
- `src/SensorConfig/` - WPF UI for selecting sensors, assigning aliases, setting min/max

### Why LibreHardwareMonitor (not HWiNFO Shared Memory)?

An earlier version read sensor data from HWiNFO's Shared Memory. This was abandoned because HWiNFO only actively updates its Shared Memory while HWiNFO's own sensor window is open and visible. If HWiNFO runs minimized to the tray (a common setup), every sensor value freezes at 0. LibreHardwareMonitor reads sensors directly via its own driver and has no such requirement.

### Why net48 (not .NET 8+)?

TURZX.exe itself runs on .NET Framework 4.8, and reflection-based injection requires loading TURZX's assembly and its types directly in the same process/AppDomain - this only works reliably when the plugin also targets net48.
