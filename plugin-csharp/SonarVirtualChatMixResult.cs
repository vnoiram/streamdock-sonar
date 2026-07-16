namespace StreamDockSonar;

public sealed record SonarVirtualChatMixResult(
    bool Success,
    string PrimaryRole,
    string SecondaryRole,
    double PrimaryVolume,
    double SecondaryVolume,
    string? ErrorSummary)
{
    public static SonarVirtualChatMixResult Ok(string primaryRole, string secondaryRole, double primaryVolume, double secondaryVolume)
    {
        return new SonarVirtualChatMixResult(true, primaryRole, secondaryRole, primaryVolume, secondaryVolume, null);
    }

    public static SonarVirtualChatMixResult Error(string primaryRole, string secondaryRole, double primaryVolume, double secondaryVolume, string errorSummary)
    {
        return new SonarVirtualChatMixResult(false, primaryRole, secondaryRole, primaryVolume, secondaryVolume, errorSummary);
    }
}
