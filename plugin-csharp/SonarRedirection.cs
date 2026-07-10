namespace StreamDockSonar;

public sealed record SonarRedirection(
    string Id,
    string DeviceId,
    bool IsRunning);
