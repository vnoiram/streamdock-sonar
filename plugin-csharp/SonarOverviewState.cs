namespace StreamDockSonar;

public sealed record SonarOverviewState(
    string TargetRole,
    string Label,
    string ShortLabel,
    double? Volume,
    bool? Muted,
    string? Error)
{
    public bool Ok => Error == null;

    public string ValueText => Error != null
        ? "ERR"
        : Muted == true
            ? "M"
            : $"{Math.Round(Volume ?? 0):0}";
}
