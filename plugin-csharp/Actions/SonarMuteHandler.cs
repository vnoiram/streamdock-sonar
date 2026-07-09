using StreamDockSDK;
using StreamDockSDK.Attributes;

namespace StreamDockSonar.Actions;

[SDAction(
    Uuid = "mute",
    Name = "Sonar Mixer Mute",
    Icon = "icons/plugin",
    Tooltip = "Toggle mute for a SteelSeries Sonar mixer channel directly.",
    Controllers = ["Keypad"],
    PropertyInspectorPath = "property-inspector.html"
)]
[SDActionState(Image = "icons/plugin", Title = "Mute")]
public sealed class SonarMuteHandler(
    StreamDockConnection connection,
    string context,
    Dictionary<string, object>? settings) : SonarActionHandler(connection, context, settings)
{
    public override Task OnWillAppearAsync()
    {
        return RefreshAsync();
    }

    public override Task UpdateDisplayAsync()
    {
        return RefreshAsync();
    }

    public override async Task OnKeyDownAsync()
    {
        try
        {
            var state = await Client.GetChannelStateAsync(SonarSettings.TargetRole, DisposeToken);
            var nextMuted = !state.Muted;
            var result = await Client.SetMuteAsync(SonarSettings.TargetRole, nextMuted, DisposeToken);
            if (!result.Success)
            {
                await ShowErrorAsync(result.ErrorSummary ?? "Sonar mute update failed");
                return;
            }

            await SetStateAsync(nextMuted ? 1 : 0);
            await ShowStateAsync(state with { Muted = nextMuted });
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var state = await Client.GetChannelStateAsync(SonarSettings.TargetRole, DisposeToken);
            await SetStateAsync(state.Muted ? 1 : 0);
            await ShowStateAsync(state);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }
}
