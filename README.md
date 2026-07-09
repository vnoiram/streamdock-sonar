# streamdock-sonar

Mirabox Stream Dock plugin for controlling SteelSeries GG Sonar mixer targets directly from a C# plugin process.

## Version

Current version: `0.3.0`.

## Actions

- `Sonar Mixer Volume`: adjusts one Sonar mixer target. Key press raises volume by `Step`; knob rotation adjusts up/down.
- `Sonar Mixer Mute`: toggles mute for one Sonar mixer target.
- `Diagnostics`: sends Sonar discovery, `/mode`, volume settings shape, and last request status to the Property Inspector.

The normal Property Inspector target list intentionally uses user-facing roles only:

- `Master`
- `Game`
- `Chat`
- `Media`
- `Aux`
- `Microphone`

Classic/stream mode and monitoring/streaming route details are not exposed in the normal UI. Diagnostics is the place to inspect those details.

## Runtime Behavior

The plugin discovers Sonar from SteelSeries `coreProps.json` and `https://127.0.0.1:6327/subApps`, then reads `subApps.sonar.metadata.webServerAddress`. Only loopback Sonar URLs are accepted.

The plugin reads `/mode` before writes:

- `classic`: uses `/volumeSettings/classic/...` routes and never calls streamer routes.
- `stream`: uses `/volumeSettings/streamer/monitoring/...` routes.

`500 Cannot be called in current mode` and other HTTP errors are shown as action errors and sent to Diagnostics. The plugin does not fall back to Windows device/session control.

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
dist/release/streamdock-sonar-0.3.0.zip
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
