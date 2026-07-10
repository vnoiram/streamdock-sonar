namespace StreamDockSonar;

public sealed record SonarSnapshot(
    DateTimeOffset UpdatedAt,
    string Mode,
    string StreamMix,
    IReadOnlyDictionary<string, SonarOverviewState> CurrentStates,
    double? ChatMixBalance,
    string? Error)
{
    public bool TryGetState(string targetRole, out SonarOverviewState state)
    {
        return CurrentStates.TryGetValue(targetRole, out state!);
    }
}
