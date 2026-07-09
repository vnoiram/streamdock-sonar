using StreamDockSDK;
using StreamDockSDK.Attributes;

namespace StreamDockSonar.Actions;

[SDAction(
    Uuid = "diagnostics",
    Name = "Diagnostics",
    Icon = "icons/plugin",
    Tooltip = "Show Sonar discovery and request diagnostics.",
    Controllers = ["Keypad"],
    PropertyInspectorPath = "property-inspector.html"
)]
[SDActionState(Image = "icons/plugin", Title = "Diag")]
public sealed class SonarDiagnosticsHandler(
    StreamDockConnection connection,
    string context,
    Dictionary<string, object>? settings) : SonarActionHandler(connection, context, settings)
{
    public override async Task OnWillAppearAsync()
    {
        await SetTitleAsync("Sonar\nDiag");
        await SendDiagnosticsAsync();
    }

    public override Task UpdateDisplayAsync()
    {
        return SetTitleAsync("Sonar\nDiag");
    }

    public override async Task OnKeyDownAsync()
    {
        await SendDiagnosticsAsync();
        await ShowOkAsync();
    }
}
