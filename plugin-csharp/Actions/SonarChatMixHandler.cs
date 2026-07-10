using StreamDockSDK;
using StreamDockSDK.Attributes;

namespace StreamDockSonar.Actions;

[SDAction(
    Uuid = "chatmix",
    Name = "Sonar ChatMix",
    Icon = "icons/plugin",
    Tooltip = "Adjust SteelSeries Sonar ChatMix.",
    Controllers = ["Keypad"],
    PropertyInspectorPath = "property-inspector.html"
)]
[SDActionState(Image = "icons/plugin", Title = "ChatMix")]
public sealed class SonarChatMixHandler(
    StreamDockConnection connection,
    string context,
    Dictionary<string, object>? settings) : SonarActionHandler(connection, context, settings)
{
    public override async Task OnWillAppearAsync()
    {
        Log.Info($"ChatMix willAppear context={Context} mode={SonarSettings.ChatMixMode} step={SonarSettings.ChatMixStep}");
        await RefreshSharedStateAsync();
    }

    public override Task UpdateDisplayAsync()
    {
        return RefreshAsync();
    }

    public override async Task OnKeyDownAsync()
    {
        try
        {
            var current = SonarRuntime.State.Current?.ChatMixBalance ?? await Client.GetChatMixBalanceAsync(DisposeToken);
            var next = SonarSettings.ChatMixMode switch
            {
                "game" => Math.Clamp(current - SonarSettings.ChatMixStep / 100.0, -1, 1),
                "reset" => 0,
                _ => Math.Clamp(current + SonarSettings.ChatMixStep / 100.0, -1, 1)
            };
            Log.Info($"ChatMix set context={Context} mode={SonarSettings.ChatMixMode} from={current:0.##} to={next:0.##}");
            var result = await Client.SetChatMixBalanceAsync(next, DisposeToken);
            if (!result.Success)
            {
                await ShowErrorAsync(result.ErrorSummary ?? "Sonar ChatMix update failed");
                return;
            }

            await ShowChatMixAsync(next);
            await RefreshSharedStateAsync();
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
        var label = SonarSettings.ChatMixMode switch
        {
            "game" => "Game",
            "reset" => "Reset",
            _ => "Chat"
        };
        var percent = Math.Round(Math.Abs(balance) * 100);
        var side = balance < 0 ? "Game" : balance > 0 ? "Chat" : "Center";
        await SetTitleAsync($"{label}\n{side} {percent:0}%");
        await Connection.SetFeedbackAsync(Context, new Dictionary<string, object>
        {
            ["title"] = "ChatMix",
            ["value"] = Math.Round((balance + 1) * 50),
            ["indicator"] = Math.Round((balance + 1) * 50)
        });
    }
}
