using System.Text.Json;

namespace StreamDockSonar;

public sealed record SonarSettings(
    string TargetRole,
    string StreamMix,
    IReadOnlyList<string> OverviewTargets,
    string ChatMixMode,
    int ChatMixStep,
    string DeviceId,
    string TargetProfileId,
    string RotationMode,
    int Step,
    string? TitleLabel,
    bool InvertKnob)
{
    public static readonly string[] AllTargetRoles = ["master", "game", "chatRender", "media", "aux", "chatCapture"];

    public static SonarSettings FromDictionary(Dictionary<string, object>? settings)
    {
        var legacyTarget = ReadString(settings, "target");
        var targetRole = ReadString(settings, "targetRole")
                         ?? LegacyTargetToRole(legacyTarget)
                         ?? "game";
        var streamMix = NormalizeStreamMix(ReadString(settings, "streamMix") ?? LegacyTargetToStreamMix(legacyTarget));
        var overviewTargets = NormalizeOverviewTargets(ReadStringList(settings, "overviewTargets"));
        var chatMixMode = NormalizeChatMixMode(ReadString(settings, "chatMixMode"));
        var chatMixStep = Math.Clamp(ReadInt(settings, "chatMixStep") ?? 10, 1, 100);
        var deviceId = ReadString(settings, "deviceId") ?? "";
        var targetProfileId = ReadString(settings, "targetProfileId") ?? "";
        var rotationMode = NormalizeRotationMode(ReadString(settings, "rotationMode"));
        var step = Math.Clamp(ReadInt(settings, "step") ?? ReadInt(settings, "volumeStep") ?? 2, 1, 20);
        var titleLabel = ReadString(settings, "titleLabel");
        var invertKnob = ReadBool(settings, "invertKnob") ?? false;
        return new SonarSettings(targetRole, streamMix, overviewTargets, chatMixMode, chatMixStep, deviceId, targetProfileId, rotationMode, step, titleLabel, invertKnob);
    }

    public string DisplayName => DisplayNameFor(TargetRole);

    public static string DisplayNameFor(string targetRole) => targetRole switch
    {
        "master" => "Master",
        "game" => "Game",
        "chatRender" => "Chat",
        "media" => "Media",
        "aux" => "Aux",
        "chatCapture" => "Microphone",
        _ => targetRole
    };

    public static string ShortNameFor(string targetRole) => targetRole switch
    {
        "master" => "MST",
        "game" => "GME",
        "chatRender" => "CHT",
        "media" => "MED",
        "aux" => "AUX",
        "chatCapture" => "MIC",
        _ => targetRole.Length <= 3 ? targetRole.ToUpperInvariant() : targetRole[..3].ToUpperInvariant()
    };

    private static string? LegacyTargetToRole(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var channel = target.Split(':', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        return channel switch
        {
            "master" => "master",
            "game" => "game",
            "chat" => "chatRender",
            "chatRender" => "chatRender",
            "media" => "media",
            "aux" => "aux",
            "mic" => "chatCapture",
            "chatCapture" => "chatCapture",
            _ => null
        };
    }

    private static string? LegacyTargetToStreamMix(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return null;
        var parts = target.Split(':', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && string.Equals(parts[0], "streamer", StringComparison.OrdinalIgnoreCase)
            ? NormalizeStreamMix(parts[1])
            : null;
    }

    private static string NormalizeStreamMix(string? streamMix)
    {
        return string.Equals(streamMix, "streaming", StringComparison.OrdinalIgnoreCase) ? "streaming" : "monitoring";
    }

    private static IReadOnlyList<string> NormalizeOverviewTargets(IEnumerable<string>? targets)
    {
        if (targets == null) return AllTargetRoles;
        var known = AllTargetRoles.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var normalized = targets
            .Where(target => known.Contains(target))
            .Select(target => AllTargetRoles.First(knownTarget => string.Equals(knownTarget, target, StringComparison.OrdinalIgnoreCase)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
        return normalized.Length == 0 ? ["game"] : normalized;
    }

    private static string NormalizeChatMixMode(string? mode)
    {
        return mode switch
        {
            "game" => "game",
            "reset" => "reset",
            _ => "chat"
        };
    }

    private static string NormalizeRotationMode(string? mode)
    {
        return mode switch
        {
            "all-auto-detect" => "all-auto-detect",
            "all-classic" => "all-classic",
            "all-streaming" => "all-streaming",
            _ => "target"
        };
    }

    private static string? ReadString(Dictionary<string, object>? settings, string key)
    {
        if (settings == null || !settings.TryGetValue(key, out var value)) return null;
        if (value is string text) return string.IsNullOrWhiteSpace(text) ? null : text;
        if (value is JsonElement element)
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var textValue = element.GetString();
                return string.IsNullOrWhiteSpace(textValue) ? null : textValue;
            }
        }

        return Convert.ToString(value);
    }

    private static IEnumerable<string>? ReadStringList(Dictionary<string, object>? settings, string key)
    {
        if (settings == null || !settings.TryGetValue(key, out var value)) return null;
        if (value is IEnumerable<string> stringValues) return stringValues;
        if (value is JsonElement element && element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray()
                .Where(item => item.ValueKind == JsonValueKind.String)
                .Select(item => item.GetString() ?? "");
        }

        return null;
    }

    private static int? ReadInt(Dictionary<string, object>? settings, string key)
    {
        if (settings == null || !settings.TryGetValue(key, out var value)) return null;
        try
        {
            if (value is int intValue) return intValue;
            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var jsonInt)) return jsonInt;
                if (element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out var parsed)) return parsed;
            }

            return Convert.ToInt32(value);
        }
        catch
        {
            return null;
        }
    }

    private static bool? ReadBool(Dictionary<string, object>? settings, string key)
    {
        if (settings == null || !settings.TryGetValue(key, out var value)) return null;
        try
        {
            if (value is bool boolValue) return boolValue;
            if (value is JsonElement element)
            {
                if (element.ValueKind == JsonValueKind.True) return true;
                if (element.ValueKind == JsonValueKind.False) return false;
                if (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out var parsed)) return parsed;
            }

            return Convert.ToBoolean(value);
        }
        catch
        {
            return null;
        }
    }
}
