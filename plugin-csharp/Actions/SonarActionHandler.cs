using System.Text.Json;
using StreamDockSDK;
using StreamDockSDK.Actions;

namespace StreamDockSonar.Actions;

public abstract class SonarActionHandler(
    StreamDockConnection connection,
    string context,
    Dictionary<string, object>? settings) : ActionHandler(connection, context, settings)
{
    protected readonly SonarClient Client = new();
    protected SonarSettings SonarSettings { get; private set; } = SonarSettings.FromDictionary(settings);

    public override Task OnSettingsChangedAsync(Dictionary<string, object> settings)
    {
        UpdateSettings(settings);
        SonarSettings = SonarSettings.FromDictionary(settings);
        return UpdateDisplayAsync();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) Client.Dispose();
        base.Dispose(disposing);
    }

    public override async Task OnSendToPluginAsync(JsonElement payload)
    {
        if (payload.ValueKind == JsonValueKind.Object &&
            payload.TryGetProperty("command", out var command) &&
            string.Equals(command.GetString(), "diagnostics", StringComparison.OrdinalIgnoreCase))
        {
            await SendDiagnosticsAsync();
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
        var diagnostics = await Client.BuildDiagnosticsAsync(DisposeToken);
        await Connection.SendToPropertyInspectorAsync(Context, new
        {
            type = "diagnostics",
            diagnostics,
            settings = new
            {
                SonarSettings.TargetRole,
                SonarSettings.Step,
                SonarSettings.TitleLabel,
                SonarSettings.InvertKnob
            }
        });
    }

    protected static Dictionary<string, object> JsonObjectToDictionary(JsonElement element)
    {
        var values = new Dictionary<string, object>();
        foreach (var property in element.EnumerateObject())
            values[property.Name] = property.Value.Clone();
        return values;
    }
}
