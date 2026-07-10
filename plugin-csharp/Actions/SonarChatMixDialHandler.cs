using StreamDockSDK;
using StreamDockSDK.Attributes;

namespace StreamDockSonar.Actions;

[SDAction(
    Uuid = "chatmix-dial",
    Name = "Sonar ChatMix Dial",
    Icon = "icons/plugin",
    Tooltip = "Adjust SteelSeries Sonar ChatMix with a dial.",
    Controllers = ["Knob"],
    PropertyInspectorPath = "property-inspector.html"
)]
[SDActionState(Image = "icons/plugin", Title = "ChatMix")]
public sealed class SonarChatMixDialHandler(
    StreamDockConnection connection,
    string context,
    Dictionary<string, object>? settings) : SonarActionHandler(connection, context, settings)
{
    private readonly object _lock = new();
    private int _pendingTicks;
    private Timer? _debounceTimer;

    public override async Task OnWillAppearAsync()
    {
        Log.Info($"ChatMixDial willAppear context={Context} step={SonarSettings.ChatMixStep}");
        await RefreshSharedStateAsync();
    }

    public override Task UpdateDisplayAsync()
    {
        return RefreshAsync();
    }

    public override Task OnDialRotateAsync(int ticks, bool pressed)
    {
        Log.Info($"ChatMixDial dialRotate context={Context} ticks={ticks} pressed={pressed}");
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
        Log.Info($"ChatMixDial dialDown context={Context}");
        await SetBalanceAsync(0);
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

        try
        {
            var current = SonarRuntime.State.Current?.ChatMixBalance ?? await Client.GetChatMixBalanceAsync(DisposeToken);
            var delta = ticks * SonarSettings.ChatMixStep / 100.0;
            await SetBalanceAsync(Math.Clamp(current + delta, -1, 1));
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private async Task SetBalanceAsync(double balance)
    {
        var result = await Client.SetChatMixBalanceAsync(balance, DisposeToken);
        if (!result.Success)
        {
            await ShowErrorAsync(result.ErrorSummary ?? "Sonar ChatMix update failed");
            return;
        }

        await ShowChatMixAsync(balance);
        await RefreshSharedStateAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var balance = SonarRuntime.State.Current?.ChatMixBalance ?? await Client.GetChatMixBalanceAsync(DisposeToken);
            await ShowChatMixAsync(balance);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private async Task ShowChatMixAsync(double balance)
    {
        var percent = Math.Round(Math.Abs(balance) * 100);
        var side = balance < 0 ? "Game" : balance > 0 ? "Chat" : "Center";
        await SetTitleAsync($"ChatMix\n{side} {percent:0}%");
        await Connection.SetFeedbackAsync(Context, new Dictionary<string, object>
        {
            ["title"] = "ChatMix",
            ["value"] = Math.Round((balance + 1) * 50),
            ["indicator"] = Math.Round((balance + 1) * 50)
        });
    }
}
