# TurzxSensorBridge

**Custom Aquacomputer/Hardware Sensor Support for TURZX**

## Overview

TurzxSensorBridge enables custom hardware sensors (especially Aquacomputer devices like D5 Next, Quadro, highflow NEXT) to be used in the TURZX screen theme editor's "Data Source" dropdown, alongside the built-in sensors. It injects sensor data directly into TURZX's own data model via a plugin loaded by TurzxPatcher - no modification of TURZX.exe itself.

## ⚠️ Hard Dependency: TurzxPatcher is required

**TurzxSensorBridge has a hard runtime dependency on [TurzxPatcher](https://github.com/breacasu/TurzxPatcher).**

The sensor-to-TURZX integration happens exclusively through `PatchModule.dll`, which is a plugin loaded by TurzxPatcher's plugin system. Without TurzxPatcher:
- `SensorConfig.exe` (configuration UI) and `SensorService.exe` (data polling + pipe server) run independently
- **But the sensors never appear in TURZX's Theme Editor** — `PatchModule.dll` is never loaded, so no injection into TURZX's data model occurs

Always launch TURZX via `TurzxPatcher.exe` (see [Installation](#installation)).

## ⚠️ Important: Run TURZX as Administrator

**TURZX itself requires Administrator privileges to read hardware sensors — this is a TURZX/LibreHardwareMonitor requirement, independent of TurzxSensorBridge.**

If TURZX is not run as Administrator, you will see a message that sensors could not be loaded, and no sensor values (built-in or custom) will be available. Always start `TurzxPatcher.exe` (which launches TURZX) via **"Run as administrator"**.

## Features

- **Direct hardware sensor access via LibreHardwareMonitor:** Reads Aquacomputer (and other) sensors directly through its own driver, independent of whether HWiNFO, AIDA64, or any other monitoring tool is installed or running.
- **Plugin System:** Extensible architecture using the `ITurzxPatch` interface, loaded by TurzxPatcher.
- **Auto-start:** `PatchModule` automatically starts `SensorService.exe` when TURZX launches - no need to manually start multiple programs in the right order.
- **Named Pipe Communication:** Unidirectional push IPC between `SensorService` and the TURZX-hosted `PatchModule`.
- **Data Source ComboBox integration:** Custom sensors appear in the Theme Editor's "Data Source" dropdown and can be assigned to any widget, just like built-in sensors, with continuously updating live values.

## Why LibreHardwareMonitor (not HWiNFO Shared Memory)?

An earlier version of this project read sensor data from HWiNFO's Shared Memory (`Global\HWiNFO_SENS_SM2`). This was abandoned because **HWiNFO only actively updates its Shared Memory while HWiNFO's own sensor window is open and visible**. If HWiNFO runs minimized to the tray (a common setup), every sensor value in Shared Memory freezes at 0 - not just Aquacomputer sensors, but CPU/GPU too. AIDA64 has a similar dependency on its own UI being open.

LibreHardwareMonitor reads sensors directly via its own driver and has no such requirement - it works whether or not any other monitoring tool is even installed, making it the most broadly compatible approach regardless of what the user otherwise runs (HWiNFO, AIDA64, or nothing at all).

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
                        │  (LibreHardware-  │     │  │   push)               │  │
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
- **Note:** Requires `RuntimeIdentifier=win-x64` in the `.csproj` so the RID-specific native implementation DLL is copied to the output directory (the NuGet package ships only a metadata-only reference assembly under the plain `net48` target).

### 2. HwInfoAccess Library (legacy, kept for reference)
- **Location:** `src/HwInfoAccess/`
- **Purpose:** Reads HWiNFO Shared Memory data. No longer used by `SensorService` (see "Why LibreHardwareMonitor" above), but kept as a reference/fallback implementation.
- **Key Classes:** `HwInfoReader`, `HwInfoSensorReading`

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
  - `SensorBridgePatch.EnsureSensorServiceRunning()` - auto-starts `SensorService.exe` (expected at `patches\SensorService\SensorService.exe`, relative to `PatchModule.dll`'s own location) if it isn't already running (checked via a named mutex matching `SensorService`'s own single-instance mutex).
  - `DataSourceInjector` - creates `M_Data` objects for each configured sensor (via the real constructor, not `FormatterServices.GetUninitializedObject`, so internal fields like `DataQueue` are properly initialized) and adds them to TURZX's static `ObservableCollection<M_Data>`.
  - `AcceptListPatcher` - patches each widget's (`GraphItem`) hardcoded `AcceptDataList` whitelist to include our sensors (otherwise the Data Source ComboBox filters them out even though they exist in the collection), directly fixes up an already-populated `DataSourceBox.Items` list, restores `DisplayName` after TURZX's own translation lookup wipes it for unrecognized sensor names, and pushes live sensor values directly into each rendered widget's `m_data.Value` every 500ms (TURZX's own per-frame update loop only refreshes an unrecognized sensor's value once, immediately after selection, unlike built-in sensors which get a dedicated per-frame refresh).
  - `PipeClient` - connects to `SensorService` and receives the pushed JSON sensor data.

### Timing

TURZX's own per-theme update loop refreshes sensor values (built-in and custom) once per second (`Thread.Sleep(1000)` between cycles, confirmed via dnSpy). `SensorService` polls LibreHardwareMonitor and pushes new values over the pipe on the same 1-second interval, so custom sensors update in lockstep with built-in ones. `AcceptListPatcher`'s 500ms patch cycle (see above) only controls how quickly a *newly received* value is written into the currently rendered widget and how quickly ComboBox/DisplayName fixes apply after the user interacts with the editor - it does not increase the actual sensor update rate beyond TURZX's native 1-second cadence.

### 5. SensorConfig (WPF Configuration UI)
- **Location:** `src/SensorConfig/`
- **Purpose:** Graphical user interface for configuring which sensors appear in TURZX and what alias they use. Reads all available sensors via LibreHardwareMonitor (same library as SensorService, so names always match), lets the user filter/search, assign aliases, and save the selection to `selected_sensors.json`.

## Installation

### Prerequisites
- [.NET Framework 4.8](https://dotnet.microsoft.com/download/dotnet-framework/net48) (TURZX itself runs on net48, so all components target net48 too)
- [TurzxPatcher](https://github.com/breacasu/TurzxPatcher) (must be installed; handles plugin discovery and loading)
- [TURZX](https://www.turzx.com/2023/03/02/%E7%9B%B4%E9%93%BE%E4%B8%8B%E8%BD%BDdirectdownload/) (v4.2.1.3 or later)

### Build from Source

```powershell
git clone https://github.com/breacasu/TurzxSensorBridge.git
cd TurzxSensorBridge
dotnet build TurzxSensorBridge.sln -c Release
```

## Usage

The following steps must be performed in order:

### Step 1: Install TurzxPatcher into the TURZX directory

Copy `TurzxPatcher.exe` into the same folder where `TURZX.exe` lives (your TURZX installation directory). See the [TurzxPatcher README](https://github.com/breacasu/TurzxPatcher) for details.

### Step 2: Build TurzxSensorBridge

```powershell
dotnet build TurzxSensorBridge.sln -c Release
```

### Step 3: Deploy PatchModule + SensorService into the TURZX directory

Copy the built files into a `patches\` subfolder **inside your TURZX installation directory** (the same folder where `TURZX.exe` and `TurzxPatcher.exe` are located):

```
TURZX installation directory\          ← where TURZX.exe and TurzxPatcher.exe live
└── patches\                           ← create this subfolder
    ├── PatchModule.dll                ← from src\PatchModule\bin\Release\net48\
    └── SensorService\                 ← create this subfolder
        └── SensorService.exe          ← from src\SensorService\bin\Release\net48\win-x64\
            (+ all other files from that output directory)
```

`PatchModule` resolves `SensorService`'s path relative to its own location (`patches\SensorService\SensorService.exe`), checks a named mutex to see if it's already running, and starts it if not - no manual `SensorService.exe` startup step needed.

### Step 4: Configure Sensors

Use `SensorConfig.exe` (recommended GUI tool) to select sensors and assign aliases, or edit `%APPDATA%\TurzxSensorBridge\selected_sensors.json` directly:

```json
{
  "selectedSensors": [
    { "alias": "D5 Water Temp", "labelOrig": "Water Temperature", "deviceName": "D5Next", "readingType": "Temperature" }
  ]
}
```

- `alias`: The name shown in TURZX's Data Source dropdown.
- `labelOrig`, `deviceName`, `readingType`: Must match a sensor reported by LibreHardwareMonitor exactly (device names, e.g. `D5Next` or `QUADRO`, differ from HWiNFO's naming). Use `SensorConfig` (recommended) or `tests/LibreHwTest` to list all detected sensors and their exact names.
- `readingType` is required whenever a device exposes multiple sensors with the identical label under different reading types (e.g. Aqua Computer Quadro's "Fan #1" exists once each as Voltage, Current, Power, and Fan/RPM) - without it, matching is ambiguous and may pick the wrong sensor.

### Step 5: Launch TURZX via TurzxPatcher (as Administrator)

Run `TurzxPatcher.exe` **as Administrator** - it copies itself next to `TURZX.exe`, discovers `PatchModule.dll`, auto-starts `SensorService.exe` if needed, and launches TURZX with the patch applied.

### Step 6: Use in TURZX Theme Editor

Open the Theme Editor (stop the running theme on a device, then click "Edit Theme"), select a widget, and open the "Data Source" dropdown - your configured sensors will appear alongside the built-in ones (may take up to ~1 second to appear after selecting a widget).

## Development

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
├── tools/                     # Build/deploy/test helper scripts
└── TurzxSensorBridge.sln      # Solution file
```

### Key Files

- `shared/ITurzxPatch.cs` - Plugin interface (must be identical in TurzxPatcher repo)
- `src/PatchModule/DataSourceInjector.cs` - Creates M_Data objects for each configured sensor
- `src/PatchModule/AcceptListPatcher.cs` - Patches widget AcceptDataList whitelist + pushes live values
- `src/SensorService/SensorPollingLoop.cs` - Server-side sensor matching + JSON push
- `src/SensorConfig/` - WPF UI for selecting sensors and assigning aliases

### Adding New Sensors

1. Run `SensorConfig.exe` (recommended) or `tests/LibreHwTest` to list all sensors LibreHardwareMonitor detects on your system, with exact `DeviceName`/`LabelOrig`/`ReadingType` values.
2. Add an entry to `selected_sensors.json` (via `SensorConfig`'s UI or by manually editing the file).
3. Changes are picked up automatically by `SensorService`'s `FileSystemWatcher` - no restart needed.

## Troubleshooting

### "Sensors could not be loaded" in TURZX
- Run TurzxPatcher.exe (and therefore TURZX) as Administrator. This is required by LibreHardwareMonitor's driver access, independent of TurzxSensorBridge.

### Custom sensors don't appear in the Data Source dropdown
- Verify `SensorService.exe` exists at `patches\SensorService\SensorService.exe` (relative to `PatchModule.dll`) - if missing, `PatchModule` logs a warning instead of auto-starting it.
- Verify `SensorService` is running and connected (`PatchModule` logs "Connected to SensorService" to the console).
- Verify `selected_sensors.json` entries match LibreHardwareMonitor's exact device/label/reading-type names (use `SensorConfig` or `tests/LibreHwTest` to list them).
- Selecting a widget may take up to ~1 second before newly-added sensors appear in its Data Source dropdown (background patch cycle runs every 500ms, but the fix only applies while the Theme Editor window is open).

### Custom sensor shows "00" or crashes when selected
- Should not happen with the current `DataSourceInjector` (uses the real `M_Data` constructor and an XPath-safe sanitized `DataName`). If it does, check the TurzxPatcher console output for exceptions.

### Custom sensor's displayed value freezes/doesn't update
- Should not happen with the current `AcceptListPatcher.PushLiveValueIfOurSensor`, which pushes the live value directly into the rendered widget every patch cycle (every 500ms), instead of relying on TURZX's own per-frame update loop (which only refreshes an unrecognized sensor's displayed value once, immediately after selection, not on every subsequent frame like it does for built-in sensors).

### TURZX Not Launching
- Verify TURZX.exe path is correct
- Ensure TurzxPatcher is installed and running as Administrator
- Check console output for error messages

## Architecture Decisions

### Why Named Pipes?
- Efficient IPC for local communication
- Message-based protocol (no stream parsing)
- Works across process boundaries
- Standard .NET support via `NamedPipeServerStream`/`NamedPipeClientStream`

### Why Reflection Injection?
- Bypasses TURZX's hardcoded sensor list
- No modification of TURZX.exe required
- Compatible with TURZX updates (as long as internal class/property names don't change)
- Similar to the existing A088 display-resolution patch approach used by TurzxPatcher

### Why net48 (not .NET 8+)?
- TURZX.exe itself runs on .NET Framework 4.8, and reflection-based injection requires loading TURZX's assembly and its types directly in the same process/AppDomain - this only works reliably when the plugin also targets net48.

## License

This project is licensed under the **MIT License** — see [LICENSE](LICENSE).

### Third-Party Dependencies

| Library | License | Purpose | Bundled? |
|---------|---------|---------|----------|
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) (v0.9.6) | [MPL 2.0](https://www.mozilla.org/en-US/MPL/2.0/) | Hardware sensor access (CPU, GPU, Aquacomputer, etc.) via its own kernel driver | Yes — `LibreHardwareMonitorLib.dll` is copied to the SensorService output directory at build time and deployed alongside `SensorService.exe` |
| [Newtonsoft.Json](https://www.newtonsoft.com/json) (v13.0.3) | [MIT](https://opensource.org/licenses/MIT) | JSON serialization in SensorConfig | Yes |
| [System.Memory](https://www.nuget.org/packages/System.Memory) et al. | [MIT](https://opensource.org/licenses/MIT) | .NET Standard backports for net48 | Yes |

LibreHardwareMonitorLib is licensed under the Mozilla Public License 2.0 (MPL 2.0), which is a weak copyleft license. The MPL-licensed `LibreHardwareMonitorLib.dll` is distributed as a separate, unmodified binary alongside this project's own MIT-licensed code. For the full text of the MPL 2.0 license, see https://www.mozilla.org/en-US/MPL/2.0/.

## Disclaimer

This tool is provided as-is for educational and experimental purposes. Use at your own risk. The developers are not responsible for any damage to your hardware or software. Always backup your TURZX.exe before using this patcher.

## Related Projects

- [TurzxPatcher](https://github.com/breacasu/TurzxPatcher) - TURZX patch loader and plugin host
- [TURZX](https://www.turzx.com/) - Universal screen themes for Windows

## Version History

### v2.0.0 (2026-07-11)
- Switched sensor source from HWiNFO Shared Memory to LibreHardwareMonitor (works regardless of HWiNFO/AIDA64 UI state)
- Fixed Data Source ComboBox visibility (AcceptDataList whitelist patch)
- Fixed crash on selecting a custom sensor (XPath-unsafe DataName, uninitialized M_Data fields)
- Fixed DisplayName being wiped after selection
- Fixed live value not updating in widgets (M_Data.Value string property, not just Rate)
- Fixed live value freezing after the initial selection (direct per-frame value push instead of relying on TURZX's one-time fallback refresh)
- Added auto-start of SensorService.exe from PatchModule (no more manual multi-step startup)

### v1.0.0 (2026-07-10)
- Initial implementation
- HWiNFO Shared Memory based sensor reading
- Named pipe IPC
- WPF configuration UI (incomplete)

---

**Made with ❤️ by breacasu and AI**
