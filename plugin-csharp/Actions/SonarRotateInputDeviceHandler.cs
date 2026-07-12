using StreamDockSDK;
using StreamDockSDK.Attributes;

namespace StreamDockSonar.Actions;

[SDAction(
    Uuid = "rotate-input-device",
    Name = "Sonar Rotate Input",
    Icon = "icons/plugin",
    Tooltip = "Rotate the SteelSeries Sonar microphone input to the next active input device.",
    Controllers = ["Keypad", "Knob"],
    PropertyInspectorPath = "property-inspector.html"
)]
[SDActionState(Image = "icons/plugin", Title = "Input")]
public sealed class SonarRotateInputDeviceHandler(
    StreamDockConnection connection,
    string context,
    Dictionary<string, object>? settings) : SonarActionHandler(connection, context, settings)
{
    private int _pendingTicks;

    public override Task OnWillAppearAsync()
    {
        Log.Info($"RotateInput willAppear context={Context}");
        return UpdateDisplayAsync();
    }

    public override async Task OnKeyDownAsync()
    {
        Log.Info($"RotateInput keyDown context={Context} deviceId={ShortDeviceId(SonarSettings.DeviceId)}");
        await SetConfiguredDeviceAsync();
    }

    public override async Task OnDialDownAsync()
    {
        Log.Info($"RotateInput dialDown context={Context} deviceId={ShortDeviceId(SonarSettings.DeviceId)}");
        await SetConfiguredDeviceAsync();
    }

    public override async Task OnDialRotateAsync(int ticks, bool pressed)
    {
        var adjustedTicks = SonarSettings.InvertKnob ? -ticks : ticks;
        _pendingTicks += adjustedTicks;
        if (Math.Abs(_pendingTicks) < SonarSettings.RotateTicks) return;

        var direction = _pendingTicks < 0 ? -1 : 1;
        _pendingTicks = 0;
        Log.Info($"RotateInput dialRotate context={Context} direction={direction}");
        await RotateAsync(direction);
    }

    private async Task SetConfiguredDeviceAsync()
    {
        var result = await Client.SetInputDeviceAsync(SonarSettings.DeviceId, DisposeToken);
        if (!result.Success)
        {
            await ShowErrorAsync(result.ErrorSummary ?? "Sonar input device update failed");
            return;
        }

        await SetTitleAsync("Input\nSet");
        await ShowOkAsync();
        await RefreshSharedStateAsync();
    }

    private async Task RotateAsync(int direction)
    {
        var result = await Client.RotateInputDeviceAsync(SonarSettings.AllowExcludedDevices, direction, DisposeToken);
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
        return SetTitleAsync("Input");
    }
}
