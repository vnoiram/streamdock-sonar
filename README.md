# streamdock-sonar

Mirabox Stream Dock JavaScript/HTML plugin for controlling SteelSeries GG Sonar-related audio targets.

The plugin runs as a Stream Dock Node.js plugin. It controls SteelSeries Sonar's local REST API directly for `Sonar API` targets and can auto-start the bundled Windows audio helper when device/session fallback or battery support is needed.

## Version

Current version: `0.2.2`.

## Actions

- `Sonar Volume`: adjusts one SteelSeries Sonar mixer channel such as `classic:game` or `streamer:monitoring:chat`. This uses the Sonar local API directly and does not require the helper.
- `Sonar Mute`: toggles mute for one Sonar mixer channel. This uses the Sonar local API directly and does not require the helper.
- `Sonar Profile`: applies a named mixer profile made from one or more channel volume/mute entries. This does not require the helper when the profile entries are Sonar API targets.
- `Windows Volume (Helper)`: adjusts a Windows output device or app session. This requires the bundled helper.
- `Windows/Mic Mute (Helper)`: toggles mute for a Windows microphone, device, or session. This requires the bundled helper.
- `Headset Battery (Helper)`: shows headset battery data when SteelSeries GG exposes it locally. This requires the bundled helper.
- `Diagnostics`: helper target refresh, setting import/export, reset, and diagnostic log copy live here.
- `Advanced Control`: legacy mixed action for older setups.

Default helper endpoint:

```text
ws://127.0.0.1:41922
```

Expected helper messages:

- Dock to helper: `{ "command": "subscribe", "targetKind": "device", "target": "Sonar - Gaming" }`.
- Dock to helper: `{ "command": "volume_delta", "targetKind": "device", "target": "Sonar - Gaming", "amount": 2 }`.
- Dock to helper: `{ "command": "toggle_mute", "targetKind": "device", "target": "Sonar - Gaming" }`.
- Helper to Dock: `{ "event": "state", "target": "Sonar - Gaming", "payload": { "volume": 50, "muted": false, "available": true } }`.

## Repository Layout

- `manifest.json`: Stream Dock plugin manifest.
- `plugin/index.js`: Stream Dock Node.js runtime plugin.
- `property-inspector.*`: Stream Dock settings UI.
- `icons/`: plugin icon assets.
- `scripts/package-plugin.js`: creates a distributable `.sdPlugin` directory.
- `helper/`: Windows Core Audio helper source, packaged as a bundled fallback by the release script.

## Stream Dock Plugin

Package this repository root as the plugin directory, or copy these files into a Stream Dock plugin folder:

- `manifest.json`
- `plugin/`
- `property-inspector.html`
- `property-inspector.js`
- `property-inspector.css`
- `icons/`

For normal Sonar use, start with `Sonar Volume` or `Sonar Mute`, choose a channel in `Target`, and leave helper settings alone. The plugin intentionally does nothing when `Target` is empty, so it does not accidentally change the default system volume.

`Target` is a select box. Sonar actions use the built-in Sonar channel list. Helper actions load Device/Session candidates from the helper and store the exact target ID internally, so there is no separate Target ID field in the UI.

The Property Inspector shows a `Mode` row. `Direct Sonar API` means helper-free. `Uses bundled Windows helper` means the helper must be installed and reachable.
If helper target loading fails, the `Mode` row shows `helper offline`. In that case install the release zip with `-InstallHelper`, or open the `Diagnostics` action and use `Refresh` to confirm whether the helper can return targets and battery candidates.

`Targets` and `Settings` controls are only shown on the `Diagnostics` action. Regular actions only show the fields needed for that action.

Use `Title label` to override long target names on the key. `Volume min` and `Volume max` clamp both relative knob changes and profile `setVolume` values. `Invert knob` reverses dial direction. `Images` enables generated state images; `Battery warn` controls when battery images switch to the warning color.

Helper logs are written next to the helper executable when the plugin starts it:

```text
stream-dock-sonar.sdPlugin\helper\SonarAudioHelper.log
```

Build a distributable plugin folder:

```bash
npm run package
```

Clean build output:

```bash
npm run clean
```

The output is written under `dist/`.

Create a release zip with the Windows Docker build:

```powershell
npm run release:zip:windows-docker
```

This builds `streamdock-sonar-helper-build:local` from `Dockerfile.helper.windows`, publishes the Windows helper, packages the plugin, and writes the release zip under `dist/release/`.

The underlying script can also be run directly:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-helper-in-windows-docker.ps1
```

For local PowerShell-only debugging, the release script can still be run directly on Windows:

```powershell
npm run release:zip
```

## Helper

The Windows helper lives in `helper/` and is a .NET Windows console app. Once installed into `.sdPlugin/helper/`, the Node plugin starts it automatically when a Device, Session, or Headset Battery configuration needs the helper endpoint. Sonar API targets use SteelSeries GG Sonar's local REST API directly and do not start the helper unless direct control fails and a virtual-device fallback is available.
Release zips include the published helper as a sidecar `helper/` directory. To install that helper into the plugin, run:

```powershell
.\scripts\install-local.ps1 -InstallHelper
```

## Sonar API Targets

Use `Sonar Volume`, `Sonar Mute`, or `Sonar Profile` to control Sonar's internal mixer channels instead of Windows device/session volume. The target list is built into the Property Inspector; no helper refresh is needed for these actions.

Target examples:

```text
classic:master
classic:game
classic:chat
classic:media
classic:aux
classic:mic
streamer:monitoring:game
streamer:streaming:game
```

The plugin discovers Sonar from SteelSeries `coreProps.json` and `https://127.0.0.1:6327/subApps`, then reads `subApps.sonar.metadata.webServerAddress`. Only loopback Sonar URLs are accepted. Because this runs in Node.js, the Sonar self-signed TLS certificate is accepted and browser CORS does not apply. If the direct request still fails, `classic:game`, `classic:chat`, `classic:media`, and `classic:aux` fall back to the matching Sonar virtual output devices through the bundled helper.

### Build

The supported release build path uses Windows Docker:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-helper-in-windows-docker.ps1
```

The script stages WSL paths when needed, builds the Windows container image, runs `scripts/release.ps1` inside the container, and copies `dist/` back to the working tree.

Local Windows debug prerequisites:

- Windows 10 or later.
- .NET SDK 8 or later.

Local debug build:

```powershell
dotnet build helper\SonarAudioHelper.csproj -c Release
```

Run from source:

```powershell
dotnet run --project helper\SonarAudioHelper.csproj
```

Run with a log file:

```powershell
dotnet run --project helper\SonarAudioHelper.csproj -- --log-file "$env:TEMP\streamdock-sonar.log"
```

It listens on `http://127.0.0.1:41922/` for WebSocket upgrades. `targetKind=device` matches active render device friendly names by substring. `targetKind=session` matches audio session display names or process names by substring.

The Property Inspector warns when the helper endpoint is not localhost because device/session names and volume commands will be sent to that endpoint.

It also supports `{ "command": "list_targets" }`, returning current device/session names and IDs for Property Inspector autocomplete.

Battery display uses the helper command `{ "command": "battery", "target": "headset name" }`. The helper first checks `STREAMDOCK_SONAR_BATTERY_JSON`, then tries SteelSeries GG/Engine `coreProps.json` endpoints and scans returned JSON for battery-like fields. SteelSeries endpoints are accepted only when they resolve to localhost/loopback, including values supplied through `STEELSERIES_GG_ENDPOINT`. If GG does not expose the headset battery through those local files or endpoints, the action shows `Battery unknown`.

Sonar Profile example:

```json
{
  "Streaming": [
    { "target": "classic:game", "setVolume": 70 },
    { "target": "classic:chat", "setVolume": 45, "mute": false },
    { "target": "classic:media", "setVolume": 20 }
  ],
  "Quiet": [
    { "target": "classic:game", "setVolume": 35 },
    { "target": "classic:chat", "mute": true }
  ]
}
```

Helper-backed advanced preset example:

```json
{
  "Streaming": [
    { "targetKind": "device", "target": "Sonar - Gaming", "setVolume": 70 },
    { "targetKind": "device", "target": "Sonar - Chat", "setVolume": 45, "mute": false },
    { "targetKind": "session", "target": "Discord", "setVolume": 80 }
  ],
  "Quiet": [
    { "targetKind": "device", "target": "Sonar - Gaming", "setVolume": 35 },
    { "targetKind": "device", "target": "Sonar - Chat", "mute": true }
  ]
}
```

Set `Preset name` to one of the object keys. Pressing the key applies `setVolume` and `mute` values.

For knob-driven preset selection, set `Preset dial` to `Select preset`. Rotation cycles through the names in `Presets`, and `Apply` controls when the selected preset is sent:

- `Press and rotate stop`: apply on key press and after rotation has been idle for `Apply delay`.
- `Rotate stop only`: apply only after knob rotation stops.
- `Press only`: rotate to preview/select, then press to apply.

Use the `Diagnostics` action to refresh helper targets, capture helper-reported Windows target states, reset settings, or export/import raw settings.

## Target Examples

- Device target: `Sonar - Gaming`
- Device target: `Sonar - Chat`
- Session target: `Discord`

Use the Windows sound control panel, SteelSeries GG, or the helper logs to confirm the exact device/session names available on the machine.

## Local Checks

JavaScript and manifest checks:

```bash
npm run check
```

Helper build, on Windows with .NET:

```powershell
npm run build:helper
```

Install the published helper into Windows startup:

```powershell
.\scripts\install-startup.ps1
```
