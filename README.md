# TurzxSensorBridge

**Custom hardware sensors for the TURZX theme editor**

## Why This Tool?

With TurzxPatcher, the TURZX editor worked great for the Lian Li display. The next goal: use sensor values from a water-cooled custom loop (water temperature, flow rate, water quality) as data sources in the theme editor.

A `HWiNFO.dll` was sitting in the TURZX directory — suggesting that all values HWiNFO reads should be available in the editor, at least with the HWiNFO Pro license for shared memory access. Unfortunately, that wasn't the case: only the built-in standard sensors were available, and the HWiNFO integration was useless for custom sensors.

Since the TurzxPatcher development had gone smoothly and decompilation provided deep insight into TURZX's data model, TurzxSensorBridge was born: it injects custom sensors directly into TURZX's internal data model — as if they had always been there.

## Features

- **Custom sensors in the theme editor**: Aquacomputer (D5 Next, Quadro, highflow NEXT), motherboard fans, CPU/GPU sensors, and more — everything LibreHardwareMonitor reads becomes a data source
- **No third-party tools required**: Works without HWiNFO, AIDA64, or any other monitoring software — only needs LibreHardwareMonitor (bundled)
- **Configurable min/max per sensor**: Controls the fill range of bar and gauge widgets. `Rate = (value - min) / (max - min)`, clamped to 0–100%
- **Auto-default for percent sensors**: Sensors with `%` unit and no configured min/max automatically use 0–100
- **SensorConfig GUI**: Select sensors, assign aliases, set min/max — all through a graphical interface
- **Units in TURZX style**: ° (temperature), R (RPM), W (power), V (voltage), % (load), L/h (flow)
- **Automatic start and stop**: SensorService starts with TURZX and is terminated when TURZX exits

## Requirements

- **Windows** (x64)
- **.NET Framework 4.8** (included with Windows 10/11)
- **[TurzxPatcher](https://github.com/breacasu/TurzxPatcher)** — TurzxSensorBridge is a plugin and only works when TURZX is launched via TurzxPatcher
- **TURZX** V4.2.1.3 or newer
- **Administrator privileges** — required for LibreHardwareMonitor's driver access

## Installation

### Option A: Installer (recommended)

Download `TurzxPatcherSetup-*.exe` from the [releases page](https://github.com/breacasu/TurzxSensorBridge/releases). The installer installs TurzxPatcher + TurzxSensorBridge together.

### Option B: Manual

1. Install [TurzxPatcher](https://github.com/breacasu/TurzxPatcher) in the TURZX directory.
2. Copy the contents of the `patches\` folder (PatchModule.dll, SensorService\, SensorConfig\) into the `patches\` folder next to `TURZX.exe`.
3. Run `SensorConfig.exe` as Administrator to configure your sensors.
4. Launch TURZX via TurzxPatcher as Administrator:
   - **With Lian Li 8.8" display**: `TurzxPatcher.exe` (default, applies A088 display patch + plugins)
   - **With a natively supported TURZX display**: `TurzxPatcher.exe --no-a088` (skips display patch, loads plugins only)

## Configuring Sensors

1. Run `SensorConfig.exe` as Administrator (otherwise not all sensors are detected).
2. **Left column**: All detected sensors from LibreHardwareMonitor. Use the search box to filter.
3. **Select a sensor**: The sensor name (e.g. "CPU Fan") is auto-filled as the alias — you can edit it.
4. **Add →**: Add the sensor to your active list.
5. **Set min/max**: Below the active list, enter min and max values per sensor to control the fill range of bar widgets.
6. **Save Configuration**: Saves to `%APPDATA%\TurzxSensorBridge\selected_sensors.json`.
7. Launch TURZX via TurzxPatcher — the sensors appear in the theme editor under "Data Source".

### When do you need min/max?

Only for bar and gauge widgets (Status Bar, Arch Bar, Dynamic Bar). Without min/max:
- **Percent sensors** (`%` unit): auto-default to 0–100
- **All others**: bar is not filled (stays at 50% default)

With min/max, e.g. water temperature 20–42°C at a current value of 28°C:
- `Rate = (28 - 20) / (42 - 20) = 0.36` → 36% bar fill

## Important Notes

- **Run as Administrator** — otherwise LibreHardwareMonitor detects fewer sensors (e.g. 212 instead of 391)
- **TurzxPatcher is required** — without it, sensors are not loaded into TURZX
- **Changes are picked up live** — `selected_sensors.json` is watched for changes, no restart needed

## Related Projects

- **[TurzxPatcher](https://github.com/breacasu/TurzxPatcher)** — TURZX patch loader and plugin host (required)
- **[TurzxThemeToolkit](https://github.com/breacasu/TurzxThemeToolkit)** — Extract images and element properties from .turtheme files
- **[TURZX](https://www.turzx.com/)** — Universal Screen Themes for Windows

## Version History

### v2.3.0 (current)
- **Configurable min/max per sensor** for bar/gauge widget fill (`Rate = (value - min) / (max - min)`)
- **Auto-default 0–100** for percent sensors without configured min/max
- **Fixed Rate overflow bug**: raw values (e.g. 25.0) were set directly as Rate instead of 0.25
- **SensorService auto-terminates** when TURZX exits
- **SensorConfig UI improvements**: alias auto-filled from sensor name, layout redesigned, min/max input fields added
- **More sensors detected**: all LibreHardwareMonitor categories enabled, null-value filter removed

### v2.2.0
- Sensors display units in TURZX style (°, R, W, V, %, L/h, µS)

### v2.0.0
- Switched from HWiNFO Shared Memory to LibreHardwareMonitor (independent of third-party tools)
- Fixed Data Source ComboBox visibility (AcceptDataList patch)
- Fixed crash on sensor selection (XPath-safe DataName, initialized M_Data fields)
- Fixed live value update in widgets
- Auto-start of SensorService from PatchModule

### v1.0.0
- Initial version (HWiNFO Shared Memory)

## License

MIT — see [LICENSE](LICENSE).

For technical details, architecture, IPC protocol, and build instructions see [TECHNICAL.md](TECHNICAL.md).

---

**Made with ❤️ by breacasu and AI**
