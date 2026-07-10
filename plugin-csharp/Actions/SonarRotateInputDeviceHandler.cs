using StreamDockSDK;
using StreamDockSDK.Attributes;

namespace StreamDockSonar.Actions;

[SDAction(
    Uuid = "rotate-input-device",
    Name = "Sonar Rotate Input",
    Icon = "icons/plugin",
    Tooltip = "Rotate the SteelSeries Sonar microphone input to the next active input device.",
    Controllers = ["Keypad"],
    PropertyInspectorPath = "property-inspector.html"
)]
[SDActionState(Image = "icons/plugin", Title = "Rotate Mic")]
public sealed class SonarRotateInputDeviceHandler(
    StreamDockConnection connection,
    string context,
    Dictionary<string, object>? settings) : SonarActionHandler(connection, context, settings)
{
    public override Task OnWillAppearAsync()
    {
        Log.Info($"RotateInput willAppear context={Context}");
        return UpdateDisplayAsync();
    }

    public override async Task OnKeyDownAsync()
    {
        Log.Info($"RotateInput keyDown context={Context}");
        var result = await Client.RotateInputDeviceAsync(DisposeToken);
        if (!result.Success)
        {
            await ShowErrorAsync(result.ErrorSummary ?? "Sonar input device rotation failed");
            return;
        }

        await ShowOkAsync();
        await UpdateDisplayAsync();
    }

    public override Task UpdateDisplayAsync()
    {
        return SetTitleAsync("Rotate\nMic");
    }
}
