using StreamDockSDK;
using StreamDockSDK.Attributes;

namespace StreamDockSonar.Actions;

[SDAction(
    Uuid = "input-device",
    Name = "Sonar Input Device",
    Icon = "icons/plugin",
    Tooltip = "Switch the SteelSeries Sonar microphone input device by device id.",
    Controllers = ["Keypad"],
    PropertyInspectorPath = "property-inspector.html"
)]
[SDActionState(Image = "icons/plugin", Title = "Input")]
public sealed class SonarInputDeviceHandler(
    StreamDockConnection connection,
    string context,
    Dictionary<string, object>? settings) : SonarActionHandler(connection, context, settings)
{
    public override Task OnWillAppearAsync()
    {
        Log.Info($"InputDevice willAppear context={Context}");
        return UpdateDisplayAsync();
    }

    public override async Task OnKeyDownAsync()
    {
        Log.Info($"InputDevice keyDown context={Context}");
        var result = await Client.SetInputDeviceAsync(SonarSettings.DeviceId, DisposeToken);
        if (!result.Success)
        {
            await ShowErrorAsync(result.ErrorSummary ?? "Sonar input device update failed");
            return;
        }

        await SetTitleAsync($"{Label}\nSet");
        await ShowOkAsync();
        await RefreshSharedStateAsync();
    }

    public override Task UpdateDisplayAsync()
    {
        var label = string.IsNullOrWhiteSpace(SonarSettings.TitleLabel) ? "Input" : SonarSettings.TitleLabel!;
        if (string.IsNullOrWhiteSpace(SonarSettings.DeviceId))
            return SetTitleAsync($"{label}\nNo ID");

        var suffix = SonarSettings.DeviceId.Length <= 6
            ? SonarSettings.DeviceId
            : SonarSettings.DeviceId[^6..];
        return SetTitleAsync($"{label}\n...{suffix}");
    }
}
