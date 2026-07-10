using StreamDockSDK;
using StreamDockSDK.Attributes;

namespace StreamDockSonar.Actions;

[SDAction(
    Uuid = "overview",
    Name = "Sonar Mixer Overview",
    Icon = "icons/plugin",
    Tooltip = "Show selected SteelSeries Sonar mixer channel states.",
    Controllers = ["Keypad"],
    PropertyInspectorPath = "property-inspector.html"
)]
[SDActionState(Image = "icons/plugin", Title = "Overview")]
public sealed class SonarOverviewHandler(
    StreamDockConnection connection,
    string context,
    Dictionary<string, object>? settings) : SonarActionHandler(connection, context, settings)
{
    public override async Task OnWillAppearAsync()
    {
        Log.Info($"Overview willAppear context={Context} streamMix={SonarSettings.StreamMix} targets={string.Join(",", SonarSettings.OverviewTargets)}");
        await RefreshAsync(showOk: false);
    }

    public override Task UpdateDisplayAsync()
    {
        return RefreshAsync(showOk: false);
    }

    public override async Task OnKeyDownAsync()
    {
        Log.Info($"Overview keyDown context={Context} streamMix={SonarSettings.StreamMix} targets={string.Join(",", SonarSettings.OverviewTargets)}");
        await RefreshAsync(showOk: true);
    }

    private async Task RefreshAsync(bool showOk)
    {
        try
        {
            var states = await Client.GetOverviewStatesAsync(SonarSettings.OverviewTargets, SonarSettings.StreamMix, DisposeToken);
            await SetTitleAsync(SonarOverviewRenderer.BuildFallbackTitle(states));
            await SetImageAsync(SonarOverviewRenderer.BuildImageDataUrl(states));
            if (showOk) await ShowOkAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }
}
