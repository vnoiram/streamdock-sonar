namespace StreamDockSonar;

public sealed record SonarConfigProfile(
    string Id,
    string Name,
    string VirtualAudioDevice,
    bool IsPreset);
