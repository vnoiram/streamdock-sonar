# streamdock-sonar

Mirabox Stream Dock plugin for controlling SteelSeries GG Sonar mixer targets directly from a C# plugin process.

## Version

Current version: `0.3.1`.

## Actions

- `Sonar Mixer Volume`: adjusts one Sonar mixer target. Key press raises volume by `Step`; knob rotation adjusts up/down.
- `Sonar Mixer Mute`: toggles mute for one Sonar mixer target.
- `Sonar Mixer Overview`: shows selected Sonar mixer target states on the Stream Dock key.
- `Sonar ChatMix`: moves ChatMix toward Chat/Game or resets it to center.
- `Sonar Output Device`: switches a Sonar output device by configured Sonar `deviceId`.
- `Diagnostics`: sends Sonar discovery, `/mode`, volume settings shape, and last request status to the Property Inspector.

The normal Property Inspector target list uses user-facing roles:

- `Master`
- `Game`
- `Chat`
- `Media`
- `Aux`
- `Microphone`

When Sonar is in stream mode, `Stream mix` selects which GG mix is controlled:

- `Monitoring`
- `Streaming`

Classic/stream mode itself is not exposed in the normal UI. Diagnostics is the place to inspect mode and route details.

`Sonar Mixer Overview` can display 1 to 6 selected targets. It uses compact labels on the key:

- `MST`: Master
- `GME`: Game
- `CHT`: Chat
- `MED`: Media
- `AUX`: Aux
- `MIC`: Microphone

Numeric values are volume percentages. `M` means muted, and `ERR` means that target could not be read.

## Runtime Behavior

The plugin discovers Sonar from SteelSeries `coreProps.json` and `https://127.0.0.1:6327/subApps`, then reads `subApps.sonar.metadata.webServerAddress`. Only loopback Sonar URLs are accepted.

The plugin reads `/mode` before writes:

- `classic`: uses `/VolumeSettings/classic/...` routes and never calls streamer routes.
- `stream`: uses `/VolumeSettings/streamer/{monitoring|streaming}/...` routes based on `Stream mix`.

Output device switching follows Sonar's redirection routes:

- `classic`: `/ClassicRedirections/{channel}/deviceId/{deviceId}`.
- `stream`: `/StreamRedirections/{monitoring|streaming}/deviceId/{deviceId}`.

The current `Sonar Output Device` action expects a Sonar `deviceId` to be pasted into the Property Inspector. Device discovery and selection UI are intentionally separate follow-up work.

`500 Cannot be called in current mode` and other HTTP errors are shown as action errors and sent to Diagnostics. The plugin does not fall back to Windows device/session control.
The plugin does not use Windows primary device, WASAPI, or helper fallback for normal volume/mute operations.

Action displays share one Sonar state cache. State is refreshed on action appearance, settings changes, plugin operations, manual Overview refresh, and every 60 seconds while the plugin process is running.

## Logs

The C# plugin writes `streamdock-sonar.log` next to `StreamDockSonar.exe`:

```text
stream-dock-sonar.sdPlugin\plugin\streamdock-sonar.log
```

The log includes Stream Dock connection, action discovery, `willAppear`, key/knob events, Sonar discovery, Sonar request routes, and user-visible errors.
If the process fails before logging is configured, `startup-error.log` is written in the same directory.

## Repository Layout

- `plugin-csharp/`: C# Stream Dock plugin source.
- `manifest.json`: Stream Dock plugin manifest pointing to `plugin/StreamDockSonar.exe`.
- `property-inspector.*`: Stream Dock settings UI.
- `icons/`: plugin icon assets.
- `scripts/package-plugin.js`: creates a distributable `.sdPlugin` directory from `dist/plugin`.
- `scripts/release.ps1`: publishes the C# plugin and creates the release zip.

## Build

The supported release build path uses Windows Docker:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-release-in-windows-docker.ps1
```

The script stages WSL paths when needed, copies the sibling `StreamDockSDK` repo into the staging tree, builds the Windows container image, runs `scripts/release.ps1` inside the container, and copies `dist/` back to the working tree.

Automatic local release build:

```powershell
npm run release:zip
```

`release:zip` uses the native PowerShell release on Windows and the Linux Docker release on Linux/WSL.

Explicit Linux Docker build:

```bash
npm run release:zip:linux
```

Explicit Windows local build:

```powershell
npm run release:zip:windows
```

JavaScript and manifest checks:

```bash
npm run check
```

## Output

Release output is written to:

```text
dist/release/streamdock-sonar-0.3.1.zip
```

The packaged plugin directory is:

```text
dist/stream-dock-sonar.sdPlugin
```

## Install

Install the packaged plugin locally:

```powershell
.\scripts\install-local.ps1
```

`git push` and release publishing are intentionally left to the user.
