using StreamDockSDK;
using StreamDockSDK.Attributes;

namespace StreamDockSonar.Actions;

[SDAction(
    Uuid = "rotate-output-device",
    Name = "Sonar Rotate Output",
    Icon = "icons/plugin",
    Tooltip = "Rotate a SteelSeries Sonar output target to the next active output device.",
    Controllers = ["Keypad"],
    PropertyInspectorPath = "property-inspector.html"
)]
[SDActionState(Image = "icons/plugin", Title = "Rotate")]
public sealed class SonarRotateOutputDeviceHandler(
    StreamDockConnection connection,
    string context,
    Dictionary<string, object>? settings) : SonarActionHandler(connection, context, settings)
{
    public override Task OnWillAppearAsync()
    {
        Log.Info($"RotateOutput willAppear context={Context} targetRole={SonarSettings.TargetRole} streamMix={SonarSettings.StreamMix}");
        return UpdateDisplayAsync();
    }

    public override async Task OnKeyDownAsync()
    {
        Log.Info($"RotateOutput keyDown context={Context} targetRole={SonarSettings.TargetRole} streamMix={SonarSettings.StreamMix}");
        var result = await Client.RotateOutputDeviceAsync(
            SonarSettings.TargetRole,
            SonarSettings.StreamMix,
            SonarSettings.RotationMode,
            SonarSettings.AllowExcludedDevices,
            DisposeToken);
        if (!result.Success)
        {
            await ShowErrorAsync(result.ErrorSummary ?? "Sonar output device rotation failed");
            return;
        }

        await ShowOkAsync();
        await UpdateDisplayAsync();
    }

    public override Task UpdateDisplayAsync()
    {
        var label = SonarSettings.RotationMode switch
        {
            "all-auto-detect" => "Auto",
            "all-classic" => "Classic",
            "all-streaming" => "Stream",
            _ => Label
        };
        return SetTitleAsync($"Rotate\n{label}");
    }
}
