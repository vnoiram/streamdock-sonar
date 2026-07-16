using StreamDockSDK;
using StreamDockSDK.Attributes;

namespace StreamDockSonar.Actions;

[SDAction(
    Uuid = "virtual-chatmix-dial",
    Name = "Sonar Virtual ChatMix Dial",
    Icon = "icons/plugin",
    Tooltip = "Simulate ChatMix by balancing two SteelSeries Sonar mixer channel volumes.",
    Controllers = ["Knob"],
    PropertyInspectorPath = "property-inspector.html"
)]
[SDActionState(Image = "icons/plugin", Title = "Virtual")]
public sealed class SonarVirtualChatMixDialHandler(
    StreamDockConnection connection,
    string context,
    Dictionary<string, object>? settings) : SonarActionHandler(connection, context, settings)
{
    private readonly object _lock = new();
    private int _pendingTicks;
    private Timer? _debounceTimer;

    public override async Task OnWillAppearAsync()
    {
        Log.Info($"VirtualChatMixDial willAppear context={Context} primary={SonarSettings.VirtualChatMixPrimaryRole} secondary={SonarSettings.VirtualChatMixSecondaryRole} step={SonarSettings.VirtualChatMixStep} rotateTicks={SonarSettings.VirtualChatMixRotateTicks}");
        await RefreshAsync();
    }

    public override Task UpdateDisplayAsync()
    {
        return RefreshAsync();
    }

    public override Task OnDialRotateAsync(int ticks, bool pressed)
    {
        Log.Info($"VirtualChatMixDial dialRotate context={Context} ticks={ticks} pressed={pressed}");
        lock (_lock)
        {
            _pendingTicks += SonarSettings.InvertKnob ? -ticks : ticks;
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => _ = ApplyPendingRotateAsync(), null, 80, Timeout.Infinite);
        }

        return Task.CompletedTask;
    }

    public override async Task OnDialDownAsync()
    {
        Log.Info($"VirtualChatMixDial dialDown context={Context}");
        try
        {
            var result = await Client.ResetVirtualChatMixAsync(
                SonarSettings.VirtualChatMixPrimaryRole,
                SonarSettings.VirtualChatMixSecondaryRole,
                DisposeToken);
            if (!result.Success)
            {
                await ShowVirtualErrorAsync(result.ErrorSummary ?? "Virtual ChatMix reset failed");
                return;
            }

            await ShowVirtualChatMixAsync(result.PrimaryRole, result.SecondaryRole, result.PrimaryVolume, result.SecondaryVolume);
            await RefreshSharedStateAsync();
        }
        catch (Exception ex)
        {
            await ShowVirtualErrorAsync(ex.Message);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _debounceTimer?.Dispose();
        base.Dispose(disposing);
    }

    private async Task ApplyPendingRotateAsync()
    {
        int operations;
        lock (_lock)
        {
            var threshold = Math.Max(1, SonarSettings.VirtualChatMixRotateTicks);
            operations = _pendingTicks / threshold;
            _pendingTicks -= operations * threshold;
        }

        if (operations == 0) return;

        try
        {
            var direction = operations > 0 ? 1 : -1;
            var relativeStep = SonarSettings.VirtualChatMixStep * Math.Abs(operations);
            var result = await Client.SetVirtualChatMixAsync(
                SonarSettings.VirtualChatMixPrimaryRole,
                SonarSettings.VirtualChatMixSecondaryRole,
                relativeStep,
                direction,
                DisposeToken);
            if (!result.Success)
            {
                await ShowVirtualErrorAsync(result.ErrorSummary ?? "Virtual ChatMix update failed");
                return;
            }

            await ShowVirtualChatMixAsync(result.PrimaryRole, result.SecondaryRole, result.PrimaryVolume, result.SecondaryVolume);
            await RefreshSharedStateAsync();
        }
        catch (Exception ex)
        {
            await ShowVirtualErrorAsync(ex.Message);
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var mode = await Client.GetModeAsync(DisposeToken);
            if (!string.Equals(mode, "classic", StringComparison.OrdinalIgnoreCase))
            {
                await ShowVirtualErrorAsync("Virtual ChatMix is only available in Normal mode");
                return;
            }

            var primary = await Client.GetChannelStateAsync(SonarSettings.VirtualChatMixPrimaryRole, "monitoring", DisposeToken);
            var secondary = await Client.GetChannelStateAsync(SonarSettings.VirtualChatMixSecondaryRole, "monitoring", DisposeToken);
            await ShowVirtualChatMixAsync(
                SonarSettings.VirtualChatMixPrimaryRole,
                SonarSettings.VirtualChatMixSecondaryRole,
                primary.Volume,
                secondary.Volume);
        }
        catch (Exception ex)
        {
            await ShowVirtualErrorAsync(ex.Message);
        }
    }

    private async Task ShowVirtualErrorAsync(string message)
    {
        await ShowErrorAsync(message);
        await SetTitleAsync("Virtual\nError");
    }

    private async Task ShowVirtualChatMixAsync(string primaryRole, string secondaryRole, double primaryVolume, double secondaryVolume)
    {
        var diff = primaryVolume - secondaryVolume;
        var indicator = Math.Round(Math.Clamp((diff + 100) / 2.0, 0, 100));
        if (Math.Abs(diff) < 0.5)
        {
            await SetTitleAsync("Virtual\nCenter");
            await Connection.SetFeedbackAsync(Context, new Dictionary<string, object>
            {
                ["title"] = "Virtual",
                ["value"] = indicator,
                ["indicator"] = indicator
            });
            return;
        }

        var side = diff > 0
            ? SonarSettings.DisplayNameFor(primaryRole)
            : SonarSettings.DisplayNameFor(secondaryRole);
        var percent = Math.Round(Math.Abs(diff));
        await SetTitleAsync($"Virtual\n{side} {percent:0}%");
        await Connection.SetFeedbackAsync(Context, new Dictionary<string, object>
        {
            ["title"] = "Virtual",
            ["value"] = indicator,
            ["indicator"] = indicator
        });
    }
}
