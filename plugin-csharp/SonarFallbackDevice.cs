namespace StreamDockSonar;

public sealed record SonarFallbackDevice(
    string Id,
    bool IsActive,
    bool IsExcluded);
