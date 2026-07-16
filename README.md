# streamdock-sonar

Mirabox Stream Dock plugin for controlling SteelSeries GG Sonar mixer targets directly from a C# plugin process.

## Version

Current version: `0.4.0`.

## Support Scope

Version `0.4.0` supports SteelSeries GG Sonar Normal mode only. Normal mode maps to Sonar's internal `classic` mode and means GG is not in Streamer mode.

Streamer mode routes and UI fields may still appear in the plugin for diagnostics and ongoing development, but they are not part of the supported behavior for this release.

## Actions

- `Sonar Mixer Volume`: adjusts one Sonar mixer target. Key press changes volume by `Step`; negative values lower volume. Knob rotation adjusts up/down, and knob press toggles mute.
- `Sonar Mixer Mute`: toggles mute for one Sonar mixer target.
- `Sonar Mixer Overview`: shows selected Sonar mixer target states on the Stream Dock key.
- `Sonar ChatMix`: moves ChatMix toward Chat/Game or resets it to center.
- `Sonar ChatMix Dial`: adjusts Sonar's native ChatMix with a knob; dial press resets to center. If a headset hardware ChatMix dial is active, Sonar may make the digital ChatMix slider read-only and this action cannot override it.
- `Sonar Virtual ChatMix Dial`: simulates ChatMix by changing two selected mixer channel volumes in opposite directions. It defaults to Game/Chat and can be configured to any two Sonar mixer targets.
- `Sonar Output Device`: switches a Sonar output device by configured Sonar `deviceId`.
- `Sonar Rotate Output`: rotates a selected Sonar output target to the next active non-virtual render device; key/knob press applies the configured `deviceId`.
- `Sonar Input Device`: switches the Sonar microphone input device by configured Sonar `deviceId`.
- `Sonar Rotate Input`: rotates the Sonar microphone input to the next active non-virtual capture device; key/knob press applies the configured `deviceId`.
- `Sonar Profile`: selects a Sonar EQ/profile for a mixer target.
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

For mode-aware actions, the Property Inspector exposes GG's mode as `Normal` or `Streamer` instead of the internal `classic` / `stream` names. Volume and mute use `Target` in Normal mode, and `Target` plus `Streamer mix` in Streamer mode. Output-device actions use `Target` in Normal mode, with `All outputs` available for linked GG output setups, and `All monitoring` / `All stream` in Streamer mode because Sonar's streamer output route is mix-based. Input-device actions show the same mode selector and route status for the microphone redirection.

`Sonar Mixer Overview` can display 1 to 6 selected targets as a generated key image. It clears the Stream Dock text title so the selected targets are shown only by the image renderer. It uses compact labels:

- `MST`: Master
- `GME`: Game
- `CHT`: Chat
- `MED`: Media
- `AUX`: Aux
- `MIC`: Microphone

Numeric values are volume percentages. `M` means muted, and `ERR` means that target could not be read.

## Runtime Behavior

The plugin discovers Sonar from SteelSeries `coreProps.json` and `https://127.0.0.1:6327/subApps`, then reads `subApps.sonar.metadata.webServerAddress`. Only loopback Sonar URLs are accepted.
If the cached Sonar endpoint stops accepting connections, the plugin treats three consecutive connection failures as a stale endpoint, discovers Sonar again, and retries the original request once against the refreshed endpoint.

The supported runtime path is Normal mode only:

- `classic`: supported. The plugin uses `/VolumeSettings/classic/...` and `/ClassicRedirections/...` routes.
- `stream`: not supported in `0.4.0`. Streamer routes may be probed for diagnostics, but Streamer mode behavior is not guaranteed.

Output device switching follows Sonar's redirection routes:

- `classic`: `/ClassicRedirections/{1|2|7|8}/deviceId/{deviceId}` for Game, Chat, Media, Aux.
- `stream`: `/StreamRedirections/{1|0}/deviceId/{deviceId}` for Monitoring, Streaming.

`Sonar Output Device` loads active non-virtual render devices from `/audioDevices` into the Property Inspector. The raw `deviceId` field remains available as a manual fallback.
The Property Inspector requests device lists over `sendToPlugin`; those requests use the Property Inspector connection context, while saved settings continue to use the action context.
`Sonar Rotate Output` reads `/ClassicRedirections` or `/StreamRedirections`, finds the currently assigned device for the configured target/mix, then applies the next active render device with the same redirection routes. Normal mode supports target-specific output rotation, and `All outputs` writes Game, Chat, Media, and Aux together. Streamer mode output redirection is mix-based, so `All monitoring` writes `/StreamRedirections/1/...` and `All stream` writes `/StreamRedirections/0/...`. Keypad press rotates; knob press applies the configured `deviceId` instead of rotating.
`Sonar Input Device` uses the same `/audioDevices` source, filtered to active non-virtual capture devices. It writes `/ClassicRedirections/3/deviceId/{deviceId}` in classic mode and `/StreamRedirections/2/deviceId/{deviceId}` in stream mode.
`Sonar Rotate Input` reads the current `mic` redirection and applies the next active capture device with the same input device routes. Keypad press rotates; knob press applies the configured `deviceId` instead of rotating.
Rotate actions use `/FallbackSettings/lists` by default so excluded devices are skipped. Enabling `Excluded devices` in the Property Inspector rotates across all non-virtual devices from `/audioDevices`.
Rotate actions support knob rotation. `Rotate ticks` controls how many dial ticks are required before a rotated device switch is applied.
`Sonar Profile` loads profiles from `/Configs`, filters by `virtualAudioDevice`, shows the selected profile from `/Configs/selected`, and applies a profile with `/Configs/{profileId}/select`.
The Property Inspector stores `invert` for knob direction. Existing `invertKnob` settings are still accepted for compatibility.
Native ChatMix actions use Sonar's `/ChatMix` route. If a headset hardware ChatMix dial is controlling Sonar, GG can gray out the digital ChatMix slider and return a read-only error such as `Cannot be modified while in readonly mode`; the plugin shows that error and does not fall back to volume changes. If `/ChatMix` reports a disabled state, ChatMix actions show a user-visible error instead of silently applying no audible change.

`Sonar Virtual ChatMix Dial` is separate from native ChatMix. It works only in Normal mode and writes normal channel volumes under `/VolumeSettings/classic/...`. The default pair is Game as primary and Chat as secondary, with advanced settings for any two mixer targets. `Diff step` is the relative difference applied per operation and is stored as an even value from `2` to `100`; odd manual input is rounded up (`1` becomes `2`, `99` becomes `100`). A positive dial operation makes the primary target stronger and the secondary weaker. If one side is already at `0%` or `100%`, the remaining difference is applied to the other side, so a `10%` diff step at Game `100%` lowers Chat by `10%`. `Rotate ticks` controls how many hardware dial ticks are required before one virtual ChatMix step is applied, and defaults to `3`. Pressing the dial resets the pair to center by setting both selected targets to their average volume. The two volume writes are not atomic; if Sonar accepts one update and rejects the other, the plugin reports the error but does not roll back the first write.

`500 Cannot be called in current mode` and other HTTP errors are shown as action errors and sent to Diagnostics. The plugin does not fall back to Windows device/session control.
The plugin does not use Windows primary device, WASAPI, or helper fallback for normal volume/mute operations.

Action displays share one Sonar state cache. State is refreshed on action appearance, settings changes, plugin operations, manual Overview refresh, and every 60 seconds while the plugin process is running.

## Logs

The C# plugin writes `streamdock-sonar.log` next to `StreamDockSonar.exe`:

```text
stream-dock-sonar.sdPlugin\plugin\streamdock-sonar.log
```

The log includes Stream Dock connection, action discovery, `willAppear`, key/knob events, Sonar discovery, Sonar request routes, and user-visible errors.
When a Property Inspector request is delivered, the log contains `Action sendToPlugin ...` or `Fallback sendToPlugin ...` followed by the request type such as `devices`, `profiles`, or `diagnostics`.
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
dist/release/streamdock-sonar-0.4.0.zip
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
