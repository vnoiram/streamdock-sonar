using StreamDockSDK;
using StreamDockSDK.Attributes;

namespace StreamDockSonar.Actions;

[SDAction(
    Uuid = "profile",
    Name = "Sonar Profile",
    Icon = "icons/plugin",
    Tooltip = "Set a SteelSeries Sonar channel profile.",
    Controllers = ["Keypad"],
    PropertyInspectorPath = "property-inspector.html"
)]
[SDActionState(Image = "icons/plugin", Title = "Profile")]
public sealed class SonarProfileHandler(
    StreamDockConnection connection,
    string context,
    Dictionary<string, object>? settings) : SonarActionHandler(connection, context, settings)
{
    public override async Task OnWillAppearAsync()
    {
        Log.Info($"Profile willAppear context={Context} targetRole={SonarSettings.TargetRole}");
        await UpdateDisplayAsync();
    }

    public override async Task OnKeyDownAsync()
    {
        Log.Info($"Profile keyDown context={Context} targetRole={SonarSettings.TargetRole} profileId={SonarSettings.TargetProfileId}");
        var result = await Client.SelectConfigProfileAsync(SonarSettings.TargetProfileId, DisposeToken);
        if (!result.Success)
        {
            await ShowErrorAsync(result.ErrorSummary ?? "Sonar profile update failed");
            return;
        }

        await ShowOkAsync();
        await UpdateDisplayAsync();
    }

    public override async Task UpdateDisplayAsync()
    {
        try
        {
            if (SonarSettings.TargetRole == "master")
            {
                await SetTitleAsync("Profile\nNo Master");
                return;
            }

            var profiles = await Client.GetConfigProfilesAsync(SonarSettings.TargetRole, DisposeToken);
            var profile = profiles.FirstOrDefault(item => item.Id == SonarSettings.TargetProfileId)
                          ?? await Client.GetSelectedConfigProfileAsync(SonarSettings.TargetRole, DisposeToken);
            var channel = SonarSettings.DisplayName;
            var profileName = profile?.Name ?? "Select";
            await SetTitleAsync($"{channel}\n{TrimTitle(profileName)}");
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(ex.Message);
        }
    }

    private static string TrimTitle(string value)
    {
        if (value.Length <= 12) return value;
        return value[..12];
    }
}
