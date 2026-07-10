namespace StreamDockSonar;

public sealed record SonarAudioDevice(
    string Id,
    string FriendlyName,
    string DataFlow,
    string Role,
    string State,
    bool IsVad);
