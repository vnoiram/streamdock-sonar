using StreamDockSDK;
using StreamDockSDK.Attributes;

namespace StreamDockSonar.Actions;

[SDAction(
    Uuid = "volume",
    Name = "Sonar Mixer Volume",
    Icon = "icons/plugin",
    Tooltip = "Adjust a SteelSeries Sonar mixer channel directly.",
    Controllers = ["Keypad", "Knob"],
    PropertyInspectorPath = "property-inspector.html"
)]
[SDActionState(Image = "icons/plugin", Title = "Volume")]
public sealed class SonarVolumeHandler(
    StreamDockConnection connection,
    string context,
    Dictionary<string, object>? settings) : SonarActionHandler(connection, context, settings)
{
    private readonly object _lock = new();
    private int _pendingTicks;
    private Timer? _debounceTimer;

    public override async Task OnWillAppearAsync()
    {
        await RefreshAsync();
    }

    public override Task UpdateDisplayAsync()
    {
        return RefreshAsync();
    }

    public override Task OnDialRotateAsync(int ticks, bool pressed)
    {
        lock (_lock)
        {
            _pendingTicks += SonarSettings.InvertKnob ? -ticks : ticks;
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => _ = ApplyPendingRotateAsync(), null, 80, Timeout.Infinite);
        }

        return Task.CompletedTask;
    }

    public override async Task OnKeyDownAsync()
    {
        await ApplyDeltaAsync(SonarSettings.Step);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _debounceTimer?.Dispose();
        base.Dispose(disposing);
    }

    private async Task ApplyPendingRotateAsync()
    {
        int ticks;
        lock (_lock)
        {
            ticks = _pendingTicks;
            _pendingTicks = 0;
        }

        if (ticks == 0) return;
        await ApplyDeltaAsync(ticks * SonarSettings.Step);
    }

    private async Task ApplyDeltaAsync(int delta)
    {
        try
        {
            var state = await Client.GetChannelStateAsync(SonarSettings.TargetRole, DisposeToken);
            var next = Math.Clamp(state.Volume + delta, 0, 100);
            var result = await Client.SetVolumeAsync(SonarSettings.TargetRole, next, DisposeToken);
            if (!result.Success)
            {
                await ShowErrorAsync(result.ErrorSummary ?? "Sonar volume update failed");
                return;
            }

            await ShowStateAsync(new SonarChannelState(next, state.Muted));
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
            await ShowStateAsync(state);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }
}
