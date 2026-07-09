using System.Text.Json;

namespace StreamDockSonar;

public sealed record SonarSettings(
    string TargetRole,
    int Step,
    string? TitleLabel,
    bool InvertKnob)
{
    public static SonarSettings FromDictionary(Dictionary<string, object>? settings)
    {
        var targetRole = ReadString(settings, "targetRole")
                         ?? LegacyTargetToRole(ReadString(settings, "target"))
                         ?? "game";
        var step = Math.Clamp(ReadInt(settings, "step") ?? ReadInt(settings, "volumeStep") ?? 2, 1, 20);
        var titleLabel = ReadString(settings, "titleLabel");
        var invertKnob = ReadBool(settings, "invertKnob") ?? false;
        return new SonarSettings(targetRole, step, titleLabel, invertKnob);
    }

    public string DisplayName => TargetRole switch
    {
        "master" => "Master",
        "game" => "Game",
        "chatRender" => "Chat",
        "media" => "Media",
        "aux" => "Aux",
        "chatCapture" => "Microphone",
        _ => TargetRole
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
