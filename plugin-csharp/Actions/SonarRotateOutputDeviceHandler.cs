using StreamDockSDK;
using StreamDockSDK.Attributes;

namespace StreamDockSonar.Actions;

[SDAction(
    Uuid = "rotate-output-device",
    Name = "Sonar Rotate Output",
    Icon = "icons/plugin",
    Tooltip = "Rotate a SteelSeries Sonar output target to the next active output device.",
    Controllers = ["Keypad", "Knob"],
    PropertyInspectorPath = "property-inspector.html"
)]
[SDActionState(Image = "icons/plugin", Title = "Output")]
public sealed class SonarRotateOutputDeviceHandler(
    StreamDockConnection connection,
    string context,
    Dictionary<string, object>? settings) : SonarActionHandler(connection, context, settings)
{
    private int _pendingTicks;

    public override Task OnWillAppearAsync()
    {
        Log.Info($"RotateOutput willAppear context={Context} targetRole={SonarSettings.TargetRole} streamMix={SonarSettings.StreamMix}");
        return UpdateDisplayAsync();
    }

    public override async Task OnKeyDownAsync()
    {
        Log.Info($"RotateOutput keyDown context={Context} targetRole={SonarSettings.TargetRole} streamMix={SonarSettings.StreamMix}");
        await RotateAsync(1);
    }

    public override async Task OnDialDownAsync()
    {
        Log.Info($"RotateOutput dialDown context={Context} targetRole={SonarSettings.TargetRole} streamMix={SonarSettings.StreamMix} deviceId={ShortDeviceId(SonarSettings.DeviceId)}");
        await SetConfiguredDeviceAsync();
    }

    public override async Task OnDialRotateAsync(int ticks, bool pressed)
    {
        var adjustedTicks = SonarSettings.InvertKnob ? -ticks : ticks;
        _pendingTicks += adjustedTicks;
        if (Math.Abs(_pendingTicks) < SonarSettings.RotateTicks) return;

        var direction = _pendingTicks < 0 ? -1 : 1;
        _pendingTicks = 0;
        Log.Info($"RotateOutput dialRotate context={Context} direction={direction}");
        await RotateAsync(direction);
    }

    private async Task SetConfiguredDeviceAsync()
    {
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

        await SetTitleAsync("Output\nSet");
        await ShowOkAsync();
        await RefreshSharedStateAsync();
    }

    private async Task RotateAsync(int direction)
    {
        var result = await Client.RotateOutputDeviceAsync(
            SonarSettings.TargetRole,
            SonarSettings.StreamMix,
            SonarSettings.RotationMode,
            SonarSettings.AllowExcludedDevices,
            direction,
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
            "all-monitoring" => "Monitoring",
            "all-streaming" => "Streaming",
            _ => Label
        };
        return SetTitleAsync($"Output\n{label}");
    }
}
