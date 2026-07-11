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
        await RefreshSharedStateAsync();
    }

    public override Task UpdateDisplayAsync()
    {
        return RefreshAsync(showOk: false);
    }

    public override Task OnSettingsChangedAsync(Dictionary<string, object> settings)
    {
        ApplySonarSettings(settings);
        Log.Info($"Overview settings refresh context={Context} streamMix={SonarSettings.StreamMix} targets={string.Join(",", SonarSettings.OverviewTargets)}");
        return RefreshAsync(showOk: false, useCache: false);
    }

    public override async Task OnKeyDownAsync()
    {
        Log.Info($"Overview keyDown context={Context} streamMix={SonarSettings.StreamMix} targets={string.Join(",", SonarSettings.OverviewTargets)}");
        await RefreshSharedStateAsync();
        await ShowOkAsync();
    }

    private async Task RefreshAsync(bool showOk, bool useCache = true)
    {
        try
        {
            var states = useCache
                ? TryGetCachedStates() ?? await Client.GetOverviewStatesAsync(SonarSettings.OverviewTargets, SonarSettings.StreamMix, DisposeToken)
                : await Client.GetOverviewStatesAsync(SonarSettings.OverviewTargets, SonarSettings.StreamMix, DisposeToken);
            await SetTitleAsync("");
            await SetImageAsync(SonarOverviewRenderer.BuildImageDataUrl(states));
            if (showOk) await ShowOkAsync();
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private IReadOnlyList<SonarOverviewState>? TryGetCachedStates()
    {
        var snapshot = SonarRuntime.State.Current;
        if (snapshot == null || snapshot.Error != null) return null;
        return SonarSettings.OverviewTargets
            .Select(target => snapshot.CurrentStates.TryGetValue(target, out var state)
                ? state
                : new SonarOverviewState(target, SonarSettings.DisplayNameFor(target), SonarSettings.ShortNameFor(target), null, null, "Missing from snapshot"))
            .ToArray();
    }
}
