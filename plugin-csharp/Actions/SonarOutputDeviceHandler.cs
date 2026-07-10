using StreamDockSDK;
using StreamDockSDK.Attributes;

namespace StreamDockSonar.Actions;

[SDAction(
    Uuid = "output-device",
    Name = "Sonar Output Device",
    Icon = "icons/plugin",
    Tooltip = "Switch a SteelSeries Sonar output device by device id.",
    Controllers = ["Keypad"],
    PropertyInspectorPath = "property-inspector.html"
)]
[SDActionState(Image = "icons/plugin", Title = "Output")]
public sealed class SonarOutputDeviceHandler(
    StreamDockConnection connection,
    string context,
    Dictionary<string, object>? settings) : SonarActionHandler(connection, context, settings)
{
    public override Task OnWillAppearAsync()
    {
        Log.Info($"OutputDevice willAppear context={Context} targetRole={SonarSettings.TargetRole} streamMix={SonarSettings.StreamMix}");
        return UpdateDisplayAsync();
    }

    public override async Task OnKeyDownAsync()
    {
        Log.Info($"OutputDevice keyDown context={Context} targetRole={SonarSettings.TargetRole} streamMix={SonarSettings.StreamMix}");
        var result = await Client.SetOutputDeviceAsync(
            SonarSettings.TargetRole,
            SonarSettings.StreamMix,
            SonarSettings.DeviceId,
            DisposeToken);

        if (!result.Success)
        {
            await ShowErrorAsync(result.ErrorSummary ?? "Sonar output device update failed");
            return;
        }

        await SetTitleAsync($"{Label}\nSet");
        await ShowOkAsync();
        await RefreshSharedStateAsync();
    }

    public override Task UpdateDisplayAsync()
    {
        if (string.IsNullOrWhiteSpace(SonarSettings.DeviceId))
            return SetTitleAsync($"{Label}\nNo ID");

        var suffix = SonarSettings.DeviceId.Length <= 6
            ? SonarSettings.DeviceId
            : SonarSettings.DeviceId[^6..];
        return SetTitleAsync($"{Label}\n...{suffix}");
    }
}
