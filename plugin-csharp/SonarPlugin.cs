using System.Reflection;
using System.Text.Json;
using log4net;
using StreamDockSDK;
using StreamDockSDK.Attributes;
using StreamDockSDK.Events;

namespace StreamDockSonar;

[SDPlugin(
    PackageId = "local.streamdock.sonar",
    SdkVersion = 1,
    Name = "Stream Dock Sonar",
    Version = "0.3.10",
    Author = "local",
    Description = "Control SteelSeries GG Sonar mixer volume and mute directly.",
    Category = "GG-Sonar",
    CategoryIcon = "icons/plugin",
    Icon = "icons/plugin",
    CodePath = "plugin/StreamDockSonar.exe",
    CodePathWin = "plugin/StreamDockSonar.exe",
    PropertyInspectorPath = "property-inspector.html",
    MinimumVersionOfSoftware = "3.10.188.226"
)]
[SDPluginOS(Platform = "windows", MinimumVersion = "10")]
public sealed class SonarPlugin : StreamDockPlugin
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(SonarPlugin));

    public override void RegisterEventHandlers()
    {
        base.RegisterEventHandlers();
        Connection.Connected += (_, _) =>
        {
            Log.Info("Connected to Stream Dock; discovering Sonar action handlers");
            HandlerManager.DiscoverHandlers(Assembly.GetExecutingAssembly());
        };
        Connection.SendToPlugin += async (_, e) => await OnFallbackSendToPluginAsync(e);
        Connection.Disconnected += (_, _) => Log.Warn("Disconnected from Stream Dock");
    }

    private async Task OnFallbackSendToPluginAsync(SendToPluginEventArgs e)
    {
        if (e.Payload.ValueKind != JsonValueKind.Object ||
            !e.Payload.TryGetProperty("command", out var commandElement) ||
            commandElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var command = commandElement.GetString();
        var replyContext = ReadReplyContext(e.Payload, e.Context);
        try
        {
            switch (command)
            {
                case "devices":
                    await SendDevicesAsync(e, replyContext);
                    break;
                case "profiles":
                    await SendProfilesAsync(e, replyContext);
                    break;
                case "diagnostics":
                    await SendDiagnosticsAsync(e, replyContext);
                    break;
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Fallback sendToPlugin failed command={command}: {ex.Message}");
        }
    }

    private async Task SendDevicesAsync(SendToPluginEventArgs e, string replyContext)
    {
        try
        {
            var dataFlow = ReadString(e.Payload, "dataFlow") ?? "render";
            var devices = string.Equals(dataFlow, "capture", StringComparison.OrdinalIgnoreCase)
                ? await SonarRuntime.Client.GetInputDevicesAsync()
                : await SonarRuntime.Client.GetOutputDevicesAsync();
            await Connection.SendToPropertyInspectorAsync(replyContext, new
            {
                type = "devices",
                dataFlow = string.Equals(dataFlow, "capture", StringComparison.OrdinalIgnoreCase) ? "capture" : "render",
                devices = devices.Select(device => new
                {
                    id = device.Id,
                    name = device.FriendlyName,
                    role = device.Role,
                    dataFlow = device.DataFlow
                }).ToArray()
            });
        }
        catch (Exception ex)
        {
            await Connection.SendToPropertyInspectorAsync(replyContext, new
            {
                type = "error",
                source = "devices",
                message = ex.Message
            });
        }
    }

    private async Task SendProfilesAsync(SendToPluginEventArgs e, string replyContext)
    {
        try
        {
            var targetRole = ReadString(e.Payload, "targetRole") ?? "game";
            var profiles = await SonarRuntime.Client.GetConfigProfilesAsync(targetRole);
            var selected = await SonarRuntime.Client.GetSelectedConfigProfileAsync(targetRole);
            await Connection.SendToPropertyInspectorAsync(replyContext, new
            {
                type = "profiles",
                targetRole,
                selectedProfileId = selected?.Id,
                profiles = profiles.Select(profile => new
                {
                    id = profile.Id,
                    name = profile.Name,
                    virtualAudioDevice = profile.VirtualAudioDevice,
                    isPreset = profile.IsPreset
                }).ToArray()
            });
        }
        catch (Exception ex)
        {
            await Connection.SendToPropertyInspectorAsync(replyContext, new
            {
                type = "error",
                source = "profiles",
                message = ex.Message
            });
        }
    }

    private async Task SendDiagnosticsAsync(SendToPluginEventArgs e, string replyContext)
    {
        var targetRole = ReadString(e.Payload, "targetRole") ?? "game";
        var streamMix = ReadString(e.Payload, "streamMix") ?? "monitoring";
        var diagnostics = await SonarRuntime.Client.BuildDiagnosticsAsync(targetRole, streamMix);
        await Connection.SendToPropertyInspectorAsync(replyContext, new
        {
            type = "diagnostics",
            diagnostics
        });
    }

    private static string ReadReplyContext(JsonElement payload, string fallback)
    {
        return ReadString(payload, "replyContext") ?? fallback;
    }

    private static string? ReadString(JsonElement payload, string propertyName)
    {
        return payload.ValueKind == JsonValueKind.Object &&
               payload.TryGetProperty(propertyName, out var value) &&
               value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }
}
