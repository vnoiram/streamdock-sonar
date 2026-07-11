using System.Text.Json;
using log4net;
using StreamDockSDK;
using StreamDockSDK.Actions;

namespace StreamDockSonar.Actions;

public abstract class SonarActionHandler : ActionHandler
{
    protected readonly ILog Log = LogManager.GetLogger(typeof(SonarActionHandler));
    protected readonly SonarClient Client = SonarRuntime.Client;
    protected SonarSettings SonarSettings { get; private set; }

    protected SonarActionHandler(StreamDockConnection connection, string context, Dictionary<string, object>? settings)
        : base(connection, context, settings)
    {
        SonarSettings = SonarSettings.FromDictionary(settings);
        SonarRuntime.State.SetStreamMix(SonarSettings.StreamMix);
        SonarRuntime.State.StateChanged += OnRuntimeStateChangedAsync;
    }

    public override Task OnSettingsChangedAsync(Dictionary<string, object> settings)
    {
        ApplySonarSettings(settings);
        return RefreshSharedStateAsync();
    }

    protected void ApplySonarSettings(Dictionary<string, object> settings)
    {
        UpdateSettings(settings);
        SonarSettings = SonarSettings.FromDictionary(settings);
        SonarRuntime.State.SetStreamMix(SonarSettings.StreamMix);
        Log.Info($"Settings changed context={Context} targetRole={SonarSettings.TargetRole} streamMix={SonarSettings.StreamMix} step={SonarSettings.Step} invert={SonarSettings.InvertKnob}");
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) SonarRuntime.State.StateChanged -= OnRuntimeStateChangedAsync;
        base.Dispose(disposing);
    }

    protected virtual Task OnRuntimeStateChangedAsync(SonarSnapshot snapshot)
    {
        return UpdateDisplayAsync();
    }

    public override async Task OnSendToPluginAsync(JsonElement payload)
    {
        var replyContext = ReadReplyContext(payload);
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("command", out var command) &&
            string.Equals(command.GetString(), "diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            await SendDiagnosticsAsync(replyContext);
            return;
        }

        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("command", out command) &&
            string.Equals(command.GetString(), "devices", StringComparison.OrdinalIgnoreCase))
        {
            var dataFlow = payload.TryGetProperty("dataFlow", out var dataFlowElement) && dataFlowElement.ValueKind == JsonValueKind.String
                ? dataFlowElement.GetString()
                : "render";
            await SendDevicesAsync(dataFlow, replyContext);
            return;
        }

        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("command", out command) &&
            string.Equals(command.GetString(), "profiles", StringComparison.OrdinalIgnoreCase))
        {
            var targetRole = payload.TryGetProperty("targetRole", out var targetElement) && targetElement.ValueKind == JsonValueKind.String
                ? targetElement.GetString()
                : SonarSettings.TargetRole;
            await SendProfilesAsync(targetRole, replyContext);
        }
    }

    protected string Label => string.IsNullOrWhiteSpace(SonarSettings.TitleLabel)
        ? SonarSettings.DisplayName
        : SonarSettings.TitleLabel!;

    protected async Task ShowStateAsync(SonarChannelState state)
    {
        var muted = state.Muted ? "Muted" : $"{Math.Round(state.Volume):0}%";
        await SetTitleAsync($"{Label}\n{muted}");
        await Connection.SetFeedbackAsync(Context, new Dictionary<string, object>
        {
            ["title"] = Label,
            ["value"] = Math.Round(state.Volume),
            ["indicator"] = state.Muted ? 0 : Math.Round(state.Volume),
            ["muted"] = state.Muted
        });
    }

    protected async Task ShowErrorAsync(string message)
    {
        Log.Warn($"Action error context={Context} targetRole={SonarSettings.TargetRole}: {message}");
        await SetTitleAsync($"{Label}\nError");
        await ShowAlertAsync();
        await Connection.SendToPropertyInspectorAsync(Context, new
        {
            type = "error",
            message
        });
    }

    protected async Task SendDiagnosticsAsync(string? replyContext = null)
    {
        var diagnostics = await Client.BuildDiagnosticsAsync(SonarSettings.TargetRole, SonarSettings.StreamMix, DisposeToken);
        await Connection.SendToPropertyInspectorAsync(replyContext ?? Context, new
        {
            type = "diagnostics",
            diagnostics,
            settings = new
            {
                SonarSettings.TargetRole,
                SonarSettings.StreamMix,
                SonarSettings.Step,
                SonarSettings.TitleLabel,
                SonarSettings.InvertKnob
            }
        });
    }

    protected async Task SendDevicesAsync(string? dataFlow, string replyContext)
    {
        try
        {
            var devices = string.Equals(dataFlow, "capture", StringComparison.OrdinalIgnoreCase)
                ? await Client.GetInputDevicesAsync(DisposeToken)
                : await Client.GetOutputDevicesAsync(DisposeToken);
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

    protected async Task SendProfilesAsync(string? targetRole, string replyContext)
    {
        try
        {
            var normalizedTarget = string.IsNullOrWhiteSpace(targetRole) ? SonarSettings.TargetRole : targetRole;
            var profiles = await Client.GetConfigProfilesAsync(normalizedTarget!, DisposeToken);
            var selected = await Client.GetSelectedConfigProfileAsync(normalizedTarget!, DisposeToken);
            await Connection.SendToPropertyInspectorAsync(replyContext, new
            {
                type = "profiles",
                targetRole = normalizedTarget,
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

    protected Task RefreshSharedStateAsync()
    {
        SonarRuntime.State.SetStreamMix(SonarSettings.StreamMix);
        return SonarRuntime.State.RefreshAsync(DisposeToken);
    }

    private string ReadReplyContext(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("replyContext", out var replyContext) &&
            replyContext.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(replyContext.GetString()))
        {
            return replyContext.GetString()!;
        }

        return Context;
    }

    protected static Dictionary<string, object> JsonObjectToDictionary(JsonElement element)
    {
        var values = new Dictionary<string, object>();
        foreach (var property in element.EnumerateObject())
            values[property.Name] = property.Value.Clone();
        return values;
    }
}
