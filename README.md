# streamdock-sonar

Mirabox Stream Dock JavaScript/HTML plugin for controlling SteelSeries GG Sonar-related audio targets.

The plugin runs as a Stream Dock Node.js plugin. It controls SteelSeries Sonar's local REST API directly for `Sonar API` targets and can auto-start the bundled Windows audio helper when device/session fallback or battery support is needed.

## Version

Current version: `0.2.2`.

Notable `0.2.0` updates:

- Added `npm run clean` for removing generated `dist/` output.
- Added `npm run release:zip` as the standard release entry point.
- Release zips now include the manifest version in the filename.

Initial actions:

- Select target channel or virtual device
- Adjust volume by configured knob step
- Toggle mute
- Reflect mute and unavailable states
- Refresh target autocomplete from the helper
- Export/import action settings
- Apply multi-target preset JSON
- Apply named mixer presets with absolute volume and mute values
- Select named presets with a knob and apply them on press, after rotation stops, or both
- Capture current helper-reported target volume/mute states into a named preset
- Dry-run named or JSON presets from the Property Inspector without changing volume
- Property Inspector `Diagnose` and `Reset` for endpoint, target, battery, and volume-range checks
- Common backup export format, Property Inspector diagnostic log copy, and cached state age on offline volume/battery titles
- Copy/paste action settings between keys from the Property Inspector
- Mic mute action
- Per-target poll interval
- Optional exact device/session ID matching
- Direct Sonar REST API channel control with helper fallback
- Bundled helper auto-start when installed from a release zip with `-InstallHelper`
- Headset battery display when SteelSeries GG exposes battery data locally
- Optional title alias for cleaner key labels
- Generated key images for volume, mute, missing target, and headset battery states
- Invert knob direction
- Min/max volume clamp for relative and preset volume changes
- Low-battery warning threshold for generated battery images

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

Configure a target in the Property Inspector before use. The plugin intentionally does nothing when `Target` is empty, so it does not accidentally change the default system volume.

For helper-free operation, set `Target kind` to `Sonar API` and choose one of the `classic:*` or `streamer:*` targets. In this mode the Endpoint and Target ID fields are not used. Device, Session, and Headset Battery actions still require the local helper.

Use `Title label` to override long Windows/Sonar target names on the key. `Volume min` and `Volume max` clamp both relative knob changes and named preset `setVolume` values. `Invert knob` reverses dial direction. `Images` enables generated state images; `Battery warn` controls when battery images switch to the warning color.

Use the Property Inspector's `Refresh` button to populate the target autocomplete list. For `Sonar API` targets this uses the built-in channel list and does not require the helper. For Device and Session targets it asks the helper for current output devices and audio sessions.

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

Set `Target kind` to `Sonar API` to control Sonar's internal mixer channels instead of Windows device/session volume. Use `Refresh` in the Property Inspector to populate known targets.

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

Preset JSON example:

```json
[
  { "targetKind": "device", "target": "Sonar - Gaming", "amount": 2 },
  { "targetKind": "device", "target": "Sonar - Chat", "amount": -2 }
]
```

Named preset example:

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

Set `Preset name` to one of the object keys. Pressing the key applies `setVolume` and `mute` values; rotating a knob still uses `amount`/`volumeStep` for relative changes.

For knob-driven preset selection, set `Preset dial` to `Select preset`. Rotation cycles through the names in `Presets`, and `Apply` controls when the selected preset is sent:

- `Press and rotate stop`: apply on key press and after rotation has been idle for `Apply delay`.
- `Rotate stop only`: apply only after knob rotation stops.
- `Press only`: rotate to preview/select, then press to apply.

Use `Refresh` to load current helper target states, set `Preset name`, then press `Capture` to add or overwrite that named preset in `Presets`. The capture uses the selected `Target kind`: `Device` captures current device volumes/mutes, and `Session` captures current audio-session volumes/mutes.

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
