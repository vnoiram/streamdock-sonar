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
        UpdateSettings(settings);
        SonarSettings = SonarSettings.FromDictionary(settings);
        SonarRuntime.State.SetStreamMix(SonarSettings.StreamMix);
        Log.Info($"Settings changed context={Context} targetRole={SonarSettings.TargetRole} streamMix={SonarSettings.StreamMix} step={SonarSettings.Step} invert={SonarSettings.InvertKnob}");
        return RefreshSharedStateAsync();
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
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("command", out var command) &&
            string.Equals(command.GetString(), "diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            await SendDiagnosticsAsync();
            return;
        }

        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("command", out command) &&
            string.Equals(command.GetString(), "devices", StringComparison.OrdinalIgnoreCase))
        {
            await SendOutputDevicesAsync();
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

    protected async Task SendDiagnosticsAsync()
    {
        var diagnostics = await Client.BuildDiagnosticsAsync(SonarSettings.TargetRole, SonarSettings.StreamMix, DisposeToken);
        await Connection.SendToPropertyInspectorAsync(Context, new
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

    protected async Task SendOutputDevicesAsync()
    {
        try
        {
            var devices = await Client.GetOutputDevicesAsync(DisposeToken);
            await Connection.SendToPropertyInspectorAsync(Context, new
            {
                type = "devices",
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
            await Connection.SendToPropertyInspectorAsync(Context, new
            {
                type = "error",
                message = ex.Message
            });
        }
    }

    protected Task RefreshSharedStateAsync()
    {
        SonarRuntime.State.SetStreamMix(SonarSettings.StreamMix);
        return SonarRuntime.State.RefreshAsync(DisposeToken);
    }

    protected static Dictionary<string, object> JsonObjectToDictionary(JsonElement element)
    {
        var values = new Dictionary<string, object>();
        foreach (var property in element.EnumerateObject())
            values[property.Name] = property.Value.Clone();
        return values;
    }
}
