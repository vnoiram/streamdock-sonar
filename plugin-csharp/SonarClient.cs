using System.Net;
using System.Text.Json;
using log4net;

namespace StreamDockSonar;

public sealed class SonarClient : IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(SonarClient));
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _httpClient;
    private string? _baseUrl;

    public SonarClient(string? baseUrl = null)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(2.5)
        };
        _baseUrl = baseUrl?.TrimEnd('/');
    }

    public SonarOperationResult? LastResult { get; private set; }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    public async Task<string> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(_baseUrl)) return _baseUrl;

        Exception? lastError = null;
        foreach (var endpoint in CandidateSubAppsUrls().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                Log.Info($"Sonar discovery request {endpoint}");
                using var document = await GetJsonDocumentAsync(endpoint, cancellationToken);
                if (!TryGetSonarWebServerAddress(document.RootElement, out var address))
                    throw new InvalidOperationException("Sonar subApp is not ready");
                if (!IsLoopbackHttpUrl(address))
                    throw new InvalidOperationException("Sonar webServerAddress is not loopback");

                _baseUrl = address.TrimEnd('/');
                Log.Info($"Sonar discovery ok url={_baseUrl}");
                return _baseUrl;
            }
            catch (Exception ex)
            {
                lastError = ex;
                Log.Warn($"Sonar discovery failed {endpoint}: {ex.Message}");
            }
        }

        throw lastError ?? new InvalidOperationException("Sonar endpoint not found");
    }

    public async Task<string> GetModeAsync(CancellationToken cancellationToken = default)
    {
        var route = "/mode";
        using var document = await RequestJsonAsync(route, HttpMethod.Get, null, cancellationToken);
        var mode = document.RootElement.ValueKind == JsonValueKind.String
            ? document.RootElement.GetString()
            : TryGetString(document.RootElement, "mode");

        mode = NormalizeMode(mode);
        LastResult = SonarOperationResult.Ok(mode, route);
        return mode;
    }

    public async Task<JsonDocument> GetVolumeSettingsAsync(string mode, CancellationToken cancellationToken = default)
    {
        var route = VolumeSettingsRoute(mode);
        var document = await RequestJsonAsync(route, HttpMethod.Get, null, cancellationToken);
        LastResult = SonarOperationResult.Ok(mode, route);
        return document;
    }

    public async Task<SonarChannelState> GetChannelStateAsync(string targetRole, string streamMix = "monitoring", CancellationToken cancellationToken = default)
    {
        var mode = await GetModeAsync(cancellationToken);
        using var settings = await GetVolumeSettingsAsync(mode, cancellationToken);
        var state = ExtractState(settings.RootElement, mode, targetRole, streamMix);
        LastResult = SonarOperationResult.Ok(mode, VolumeSettingsRoute(mode));
        return state;
    }

    public async Task<SonarOperationResult> SetVolumeAsync(string targetRole, double value, string streamMix = "monitoring", CancellationToken cancellationToken = default)
    {
        var mode = await GetModeAsync(cancellationToken);
        var route = BuildPutRoute(mode, targetRole, streamMix, "volume", Math.Clamp(value / 100.0, 0, 1).ToString("0.00"));
        return await PutAsync(mode, route, cancellationToken);
    }

    public async Task<SonarOperationResult> SetMuteAsync(string targetRole, bool muted, string streamMix = "monitoring", CancellationToken cancellationToken = default)
    {
        var mode = await GetModeAsync(cancellationToken);
        var route = BuildPutRoute(mode, targetRole, streamMix, "mute", muted ? "true" : "false");
        return await PutAsync(mode, route, cancellationToken);
    }

    public async Task<double> GetChatMixBalanceAsync(CancellationToken cancellationToken = default)
    {
        using var document = await RequestJsonAsync("/ChatMix", HttpMethod.Get, null, cancellationToken);
        if (TryGetPropertyIgnoreCase(document.RootElement, "state", out var state) && IsDisabledChatMixState(state))
        {
            throw new InvalidOperationException("Sonar ChatMix is disabled or unavailable");
        }

        if (TryGetPropertyIgnoreCase(document.RootElement, "balance", out var balance) && balance.ValueKind == JsonValueKind.Number)
        {
            LastResult = SonarOperationResult.Ok(null, "/ChatMix");
            return Math.Clamp(balance.GetDouble(), -1, 1);
        }

        throw new InvalidOperationException("Sonar ChatMix response is missing balance");
    }

    public async Task<SonarOperationResult> SetChatMixBalanceAsync(double balance, CancellationToken cancellationToken = default)
    {
        var normalized = Math.Clamp(balance, -1, 1).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        return await PutAsync("", $"/ChatMix?balance={normalized}", cancellationToken);
    }

    public async Task<SonarOperationResult> SetOutputDeviceAsync(string targetRole, string streamMix, string deviceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return SonarOperationResult.Error(null, null, null, "Sonar deviceId is required");

        string? mode = null;
        try
        {
            mode = await GetModeAsync(cancellationToken);
            var route = BuildDeviceRoute(mode, targetRole, streamMix, deviceId);
            return await PutAsync(mode, route, cancellationToken);
        }
        catch (Exception ex)
        {
            var result = SonarOperationResult.Error(mode, null, null, ex.Message);
            LastResult = result;
            return result;
        }
    }

    public async Task<IReadOnlyList<SonarAudioDevice>> GetOutputDevicesAsync(CancellationToken cancellationToken = default)
    {
        return await GetAudioDevicesAsync("render", cancellationToken);
    }

    public async Task<IReadOnlyList<SonarAudioDevice>> GetInputDevicesAsync(CancellationToken cancellationToken = default)
    {
        return await GetAudioDevicesAsync("capture", cancellationToken);
    }

    public async Task<SonarOperationResult> SetInputDeviceAsync(string deviceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return SonarOperationResult.Error(null, null, null, "Sonar input deviceId is required");

        string? mode = null;
        try
        {
            mode = await GetModeAsync(cancellationToken);
            var route = BuildInputDeviceRoute(mode, deviceId);
            return await PutAsync(mode, route, cancellationToken);
        }
        catch (Exception ex)
        {
            var result = SonarOperationResult.Error(mode, null, null, ex.Message);
            LastResult = result;
            return result;
        }
    }

    public async Task<IReadOnlyList<SonarConfigProfile>> GetConfigProfilesAsync(string targetRole, CancellationToken cancellationToken = default)
    {
        if (targetRole == "master")
            return Array.Empty<SonarConfigProfile>();

        using var document = await RequestJsonAsync("/Configs", HttpMethod.Get, null, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Sonar Configs response is not an array");

        var virtualAudioDevice = HttpChannel(targetRole, "classic");
        var profiles = document.RootElement
            .EnumerateArray()
            .Select(ReadConfigProfile)
            .Where(profile => string.Equals(profile.VirtualAudioDevice, virtualAudioDevice, StringComparison.OrdinalIgnoreCase))
            .OrderBy(profile => profile.IsPreset)
            .ThenBy(profile => profile.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        LastResult = SonarOperationResult.Ok(null, "/Configs");
        return profiles;
    }

    public async Task<SonarConfigProfile?> GetSelectedConfigProfileAsync(string targetRole, CancellationToken cancellationToken = default)
    {
        if (targetRole == "master") return null;

        using var document = await RequestJsonAsync("/Configs/selected", HttpMethod.Get, null, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Sonar Configs/selected response is not an array");

        var virtualAudioDevice = HttpChannel(targetRole, "classic");
        LastResult = SonarOperationResult.Ok(null, "/Configs/selected");
        return document.RootElement
            .EnumerateArray()
            .Select(ReadConfigProfile)
            .FirstOrDefault(profile => string.Equals(profile.VirtualAudioDevice, virtualAudioDevice, StringComparison.OrdinalIgnoreCase));
    }

    public async Task<SonarOperationResult> SelectConfigProfileAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
            return SonarOperationResult.Error(null, null, null, "Sonar profile id is required");

        var route = $"/Configs/{Uri.EscapeDataString(profileId)}/select";
        return await PutAsync("", route, cancellationToken);
    }

    public async Task<SonarOperationResult> RotateOutputDeviceAsync(string targetRole, string streamMix, string rotationMode = "target", bool allowExcludedDevices = false, int direction = 1, CancellationToken cancellationToken = default)
    {
        var mode = await GetModeAsync(cancellationToken);
        var selectedMode = NormalizeRotationMode(rotationMode, mode);
        if (selectedMode == "target" && NormalizeMode(mode) == "stream")
        {
            var result = SonarOperationResult.Error(mode, null, null, "Stream mode output rotation is mix-based; choose All stream or use Monitoring/Streaming mix rotation");
            LastResult = result;
            return result;
        }

        var currentDeviceId = selectedMode switch
        {
            "all-classic" => await GetCurrentOutputDeviceIdAsync("classic", "game", "monitoring", cancellationToken),
            "all-monitoring" => await GetCurrentOutputDeviceIdAsync("stream", "game", "monitoring", cancellationToken),
            "all-streaming" => await GetCurrentOutputDeviceIdAsync("stream", "game", "streaming", cancellationToken),
            _ => await GetCurrentOutputDeviceIdAsync(mode, targetRole, streamMix, cancellationToken)
        };
        var devices = await GetRotatableOutputDevicesAsync(targetRole, currentDeviceId, allowExcludedDevices, cancellationToken);
        if (devices.Count == 0)
            return SonarOperationResult.Error(selectedMode, null, null, "No Sonar output devices are available");

        var currentIndex = FindDeviceIndex(devices, currentDeviceId);
        Log.Info($"Rotate output targetRole={targetRole} selectedMode={selectedMode} currentIndex={currentIndex} direction={direction} candidates={SummarizeDeviceIds(devices)} current={currentDeviceId}");
        if (currentIndex < 0)
        {
            var result = SonarOperationResult.Error(selectedMode, null, null, $"Current Sonar output device is not in the rotatable device list for {targetRole}");
            LastResult = result;
            return result;
        }

        var nextIndex = NextIndex(devices.Count, currentIndex, direction);
        var nextDeviceId = devices[nextIndex].Id;

        return selectedMode switch
        {
            "all-classic" => await SetAllClassicOutputDevicesAsync(nextDeviceId, cancellationToken),
            "all-monitoring" => await SetStreamOutputDeviceAsync("monitoring", nextDeviceId, cancellationToken),
            "all-streaming" => await SetStreamOutputDeviceAsync("streaming", nextDeviceId, cancellationToken),
            _ => await SetOutputDeviceAsync(targetRole, streamMix, nextDeviceId, cancellationToken)
        };
    }

    public async Task<SonarOperationResult> RotateInputDeviceAsync(bool allowExcludedDevices = false, int direction = 1, CancellationToken cancellationToken = default)
    {
        var devices = await GetRotatableInputDevicesAsync(allowExcludedDevices, cancellationToken);
        if (devices.Count == 0)
            return SonarOperationResult.Error(null, null, null, "No Sonar input devices are available");

        var mode = await GetModeAsync(cancellationToken);
        var currentDeviceId = await GetCurrentInputDeviceIdAsync(mode, cancellationToken);
        var currentIndex = FindDeviceIndex(devices, currentDeviceId);
        Log.Info($"Rotate input currentIndex={currentIndex} direction={direction} candidates={SummarizeDeviceIds(devices)} current={currentDeviceId}");
        var nextIndex = NextIndex(devices.Count, currentIndex, direction);
        return await SetInputDeviceAsync(devices[nextIndex].Id, cancellationToken);
    }

    private static int NextIndex(int count, int currentIndex, int direction)
    {
        if (count <= 0) return 0;
        if (currentIndex < 0) return direction < 0 ? count - 1 : 0;
        var delta = direction < 0 ? -1 : 1;
        return (currentIndex + delta + count) % count;
    }

    private static int FindDeviceIndex(IReadOnlyList<SonarAudioDevice> devices, string deviceId)
    {
        for (var index = 0; index < devices.Count; index++)
        {
            if (DeviceIdsEqual(devices[index].Id, deviceId)) return index;
        }

        return -1;
    }

    private static bool ContainsDeviceId(HashSet<string> deviceIds, string deviceId)
    {
        return deviceIds.Any(candidate => DeviceIdsEqual(candidate, deviceId));
    }

    private static bool DeviceIdsEqual(string left, string right)
    {
        return string.Equals(NormalizeDeviceIdForCompare(left), NormalizeDeviceIdForCompare(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDeviceIdForCompare(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try
        {
            return Uri.UnescapeDataString(value).Trim();
        }
        catch
        {
            return value.Trim();
        }
    }

    private static string SummarizeDeviceIds(IReadOnlyList<SonarAudioDevice> devices)
    {
        return string.Join(",", devices.Select(device => device.Id).Take(6));
    }

    private static string SummarizeRedirections(IReadOnlyList<SonarRedirection> redirections)
    {
        return string.Join(",", redirections.Select(redirection => $"{redirection.Id}:{ShortDeviceId(redirection.DeviceId)}").Take(12));
    }

    private static string ShortDeviceId(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return "";
        var normalized = NormalizeDeviceIdForCompare(deviceId);
        return normalized.Length <= 24 ? normalized : normalized[..24] + "...";
    }

    private async Task<IReadOnlyList<SonarAudioDevice>> GetRotatableOutputDevicesAsync(string targetRole, string currentDeviceId, bool allowExcludedDevices, CancellationToken cancellationToken)
    {
        var devices = await GetAudioDevicesAsync("render", allowExcludedDevices, cancellationToken);
        if (allowExcludedDevices) return devices;

        var allowedIds = await GetFallbackDeviceIdsAsync(FallbackChannel(targetRole), cancellationToken);
        var filtered = devices.Where(device => ContainsDeviceId(allowedIds, device.Id)).ToArray();
        if (filtered.Length > 0) return filtered;

        Log.Info($"No fallback devices for targetRole={targetRole}; using active render devices all={SummarizeDeviceIds(devices)} current={currentDeviceId}");
        return devices;
    }

    private async Task<IReadOnlyList<SonarAudioDevice>> GetRotatableInputDevicesAsync(bool allowExcludedDevices, CancellationToken cancellationToken)
    {
        var devices = await GetAudioDevicesAsync("capture", allowExcludedDevices, cancellationToken);
        if (allowExcludedDevices) return devices;

        var allowedIds = await GetFallbackDeviceIdsAsync("chatCapture", cancellationToken);
        var filtered = devices.Where(device => ContainsDeviceId(allowedIds, device.Id)).ToArray();
        if (filtered.Length > 0) return filtered;

        Log.Info($"No fallback devices for input; using active capture devices all={SummarizeDeviceIds(devices)}");
        return devices;
    }

    private async Task<IReadOnlyList<SonarAudioDevice>> GetAudioDevicesAsync(string dataFlow, CancellationToken cancellationToken)
    {
        return await GetAudioDevicesAsync(dataFlow, false, cancellationToken);
    }

    private async Task<IReadOnlyList<SonarAudioDevice>> GetAudioDevicesAsync(string dataFlow, bool includeExcluded, CancellationToken cancellationToken)
    {
        using var document = await RequestJsonAsync("/audioDevices", HttpMethod.Get, null, cancellationToken);
        var devices = new List<SonarAudioDevice>();
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Sonar audioDevices response is not an array");

        foreach (var item in document.RootElement.EnumerateArray())
        {
            var device = ReadAudioDevice(item);
            if (string.Equals(device.DataFlow, dataFlow, StringComparison.OrdinalIgnoreCase) &&
                !device.IsVad &&
                string.Equals(device.Role, "none", StringComparison.OrdinalIgnoreCase) &&
                (includeExcluded || string.IsNullOrWhiteSpace(device.State) || string.Equals(device.State, "active", StringComparison.OrdinalIgnoreCase)))
            {
                devices.Add(device);
            }
        }

        LastResult = SonarOperationResult.Ok(null, "/audioDevices");
        return devices;
    }

    private async Task<HashSet<string>> GetFallbackDeviceIdsAsync(string channel, CancellationToken cancellationToken)
    {
        using var document = await RequestJsonAsync("/FallbackSettings/lists", HttpMethod.Get, null, cancellationToken);
        if (!TryGetPropertyIgnoreCase(document.RootElement, channel, out var devices) || devices.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Sonar fallback settings are missing '{channel}'");

        LastResult = SonarOperationResult.Ok(null, "/FallbackSettings/lists");
        return devices
            .EnumerateArray()
            .Select(ReadFallbackDevice)
            .Where(device => device.IsActive && !device.IsExcluded)
            .Select(device => device.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string FallbackChannel(string targetRole)
    {
        return targetRole switch
        {
            "chatCapture" => "chatCapture",
            "chatRender" => "chatRender",
            "media" => "media",
            "aux" => "aux",
            _ => "game"
        };
    }

    private async Task<string> GetCurrentOutputDeviceIdAsync(string mode, string targetRole, string streamMix, CancellationToken cancellationToken)
    {
        var normalizedMode = NormalizeMode(mode);
        var route = normalizedMode == "stream" ? "/StreamRedirections" : "/ClassicRedirections";
        using var document = await RequestJsonAsync(route, HttpMethod.Get, null, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Sonar {route} response is not an array");

        var desiredId = normalizedMode == "stream" ? StreamRedirectionChannel(streamMix) : ClassicRedirectionChannel(targetRole);
        var seen = new List<SonarRedirection>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var redirection = ReadRedirection(item, normalizedMode);
            seen.Add(redirection);
            if (string.Equals(redirection.Id, desiredId, StringComparison.OrdinalIgnoreCase))
            {
                LastResult = SonarOperationResult.Ok(normalizedMode, route);
                return redirection.DeviceId;
            }
        }

        Log.Warn($"Sonar redirection '{desiredId}' was not found in {route}; seen={SummarizeRedirections(seen)}");
        throw new InvalidOperationException($"Sonar redirection '{desiredId}' was not found");
    }

    private async Task<string> GetCurrentInputDeviceIdAsync(string mode, CancellationToken cancellationToken)
    {
        var normalizedMode = NormalizeMode(mode);
        var route = normalizedMode == "stream" ? "/StreamRedirections" : "/ClassicRedirections";
        using var document = await RequestJsonAsync(route, HttpMethod.Get, null, cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException($"Sonar {route} response is not an array");

        var desiredId = normalizedMode == "stream" ? StreamRedirectionChannel("mic") : ClassicRedirectionChannel("chatCapture");
        var seen = new List<SonarRedirection>();
        foreach (var item in document.RootElement.EnumerateArray())
        {
            var redirection = ReadRedirection(item, normalizedMode);
            seen.Add(redirection);
            if (string.Equals(redirection.Id, desiredId, StringComparison.OrdinalIgnoreCase))
            {
                LastResult = SonarOperationResult.Ok(normalizedMode, route);
                return redirection.DeviceId;
            }
        }

        Log.Warn($"Sonar redirection '{desiredId}' was not found in {route}; seen={SummarizeRedirections(seen)}");
        throw new InvalidOperationException($"Sonar redirection '{desiredId}' was not found");
    }

    private async Task<SonarOperationResult> SetAllClassicOutputDevicesAsync(string deviceId, CancellationToken cancellationToken)
    {
        foreach (var targetRole in new[] { "game", "chatRender", "media", "aux" })
        {
            var result = await PutAsync("classic", BuildDeviceRoute("classic", targetRole, "monitoring", deviceId), cancellationToken);
            if (!result.Success) return result;
        }

        return SonarOperationResult.Ok("classic", "/ClassicRedirections/*/deviceId", 200);
    }

    private async Task<SonarOperationResult> SetStreamOutputDeviceAsync(string streamMix, string deviceId, CancellationToken cancellationToken)
    {
        var normalizedStreamMix = NormalizeStreamMix(streamMix);
        var result = await PutAsync("stream", BuildDeviceRoute("stream", "game", normalizedStreamMix, deviceId), cancellationToken);
        return result.Success
            ? SonarOperationResult.Ok("stream", $"/StreamRedirections/{normalizedStreamMix}/deviceId", 200)
            : result;
    }

    public async Task<IReadOnlyList<SonarOverviewState>> GetOverviewStatesAsync(IEnumerable<string> targetRoles, string streamMix = "monitoring", CancellationToken cancellationToken = default)
    {
        var mode = await GetModeAsync(cancellationToken);
        using var settings = await GetVolumeSettingsAsync(mode, cancellationToken);
        var states = new List<SonarOverviewState>();
        foreach (var targetRole in targetRoles)
        {
            try
            {
                var state = ExtractState(settings.RootElement, mode, targetRole, streamMix);
                states.Add(new SonarOverviewState(
                    targetRole,
                    SonarSettings.DisplayNameFor(targetRole),
                    SonarSettings.ShortNameFor(targetRole),
                    state.Volume,
                    state.Muted,
                    null));
            }
            catch (Exception ex)
            {
                states.Add(new SonarOverviewState(
                    targetRole,
                    SonarSettings.DisplayNameFor(targetRole),
                    SonarSettings.ShortNameFor(targetRole),
                    null,
                    null,
                    ex.Message));
            }
        }

        LastResult = SonarOperationResult.Ok(mode, VolumeSettingsRoute(mode));
        return states;
    }

    public async Task<object> BuildDiagnosticsAsync(string targetRole = "game", string streamMix = "monitoring", CancellationToken cancellationToken = default)
    {
        var discovery = "";
        var mode = "";
        object? classicProbe = null;
        object? streamerProbe = null;
        try
        {
            discovery = await DiscoverAsync(cancellationToken);
            mode = await GetModeAsync(cancellationToken);
            classicProbe = await BuildVolumeSettingsProbeAsync("classic", targetRole, streamMix, cancellationToken);
            streamerProbe = await BuildVolumeSettingsProbeAsync("stream", targetRole, streamMix, cancellationToken);
        }
        catch (Exception ex)
        {
            LastResult = SonarOperationResult.Error(mode, LastResult?.Route, LastResult?.StatusCode, ex.Message);
        }

        return new
        {
            discovery,
            mode,
            targetRole,
            streamMix = NormalizeStreamMix(streamMix),
            probes = new
            {
                classic = classicProbe,
                streamer = streamerProbe
            },
            lastRequest = new
            {
                LastResult?.Route,
                statusCode = LastResult?.StatusCode,
                LastResult?.ErrorSummary,
                LastResult?.Success
            }
        };
    }

    private async Task<SonarOperationResult> PutAsync(string mode, string route, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await SendAsync(route, HttpMethod.Put, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            Log.Info($"Sonar response PUT {route} status={(int)response.StatusCode}");
            if (!response.IsSuccessStatusCode)
            {
                var error = SummarizeError(response.StatusCode, body);
                var result = SonarOperationResult.Error(mode, route, (int)response.StatusCode, error);
                LastResult = result;
                return result;
            }

            var ok = SonarOperationResult.Ok(mode, route, (int)response.StatusCode);
            LastResult = ok;
            return ok;
        }
        catch (Exception ex)
        {
            var result = SonarOperationResult.Error(mode, route, null, ex.Message);
            LastResult = result;
            return result;
        }
    }

    private async Task<JsonDocument> RequestJsonAsync(string route, HttpMethod method, HttpContent? content, CancellationToken cancellationToken)
    {
        using var response = await SendAsync(route, method, cancellationToken, content);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Log.Info($"Sonar response {method} {route} status={(int)response.StatusCode}");
        if (!response.IsSuccessStatusCode)
        {
            LastResult = SonarOperationResult.Error(null, route, (int)response.StatusCode, SummarizeError(response.StatusCode, body));
            throw new InvalidOperationException(LastResult.ErrorSummary);
        }

        LastResult = SonarOperationResult.Ok(null, route, (int)response.StatusCode);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    private async Task<object> BuildVolumeSettingsProbeAsync(string mode, string targetRole, string streamMix, CancellationToken cancellationToken)
    {
        try
        {
            using var settings = await GetVolumeSettingsAsync(mode, cancellationToken);
            return new
            {
                ok = true,
                route = VolumeSettingsRoute(mode),
                shape = SummarizeVolumeShape(settings.RootElement, mode),
                selectedState = SummarizeSelectedState(settings.RootElement, mode, targetRole, streamMix),
                mixProbe = NormalizeMode(mode) == "stream" ? SummarizeStreamMixes(settings.RootElement, targetRole) : null
            };
        }
        catch (Exception ex)
        {
            return new
            {
                ok = false,
                route = VolumeSettingsRoute(mode),
                LastResult?.StatusCode,
                error = ex.Message
            };
        }
    }

    private async Task<HttpResponseMessage> SendAsync(string route, HttpMethod method, CancellationToken cancellationToken, HttpContent? content = null)
    {
        var baseUrl = await DiscoverAsync(cancellationToken);
        var uri = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), route.TrimStart('/'));
        if (!IsLoopbackHttpUrl(uri.ToString()))
            throw new InvalidOperationException("Refusing non-loopback Sonar URL");

        Log.Info($"Sonar request {method} {uri.PathAndQuery}");
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private async Task<JsonDocument> GetJsonDocumentAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(SummarizeError(response.StatusCode, body));
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
    }

    private static SonarChannelState ExtractState(JsonElement root, string mode, string targetRole, string streamMix)
    {
        var normalizedMode = NormalizeMode(mode);
        var normalizedMix = NormalizeStreamMix(streamMix);
        JsonElement node;
        if (targetRole == "master")
        {
            node = normalizedMode == "stream"
                ? GetRequiredProperty(GetRequiredProperty(GetRequiredProperty(root, "masters"), "stream"), normalizedMix)
                : GetRequiredProperty(GetRequiredProperty(root, "masters"), "classic");
        }
        else
        {
            var device = GetRequiredProperty(GetRequiredProperty(root, "devices"), targetRole);
            node = normalizedMode == "stream"
                ? GetRequiredProperty(GetRequiredProperty(device, "stream"), normalizedMix)
                : GetRequiredProperty(device, "classic");
        }

        return new SonarChannelState(ReadVolume(node), ReadMuted(node));
    }

    private static SonarAudioDevice ReadAudioDevice(JsonElement item)
    {
        return new SonarAudioDevice(
            TryGetString(item, "id") ?? "",
            TryGetString(item, "friendlyName") ?? TryGetString(item, "name") ?? "",
            TryGetString(item, "dataFlow") ?? "",
            TryGetString(item, "role") ?? "",
            TryGetString(item, "state") ?? "",
            TryGetBool(item, "isVad") ?? false);
    }

    private static SonarConfigProfile ReadConfigProfile(JsonElement item)
    {
        return new SonarConfigProfile(
            TryGetString(item, "id") ?? "",
            TryGetString(item, "name") ?? "",
            TryGetString(item, "virtualAudioDevice") ?? "",
            TryGetBool(item, "isPreset") ?? false);
    }

    private static SonarRedirection ReadRedirection(JsonElement item, string mode)
    {
        var normalizedMode = NormalizeMode(mode);
        var id = normalizedMode == "stream"
            ? TryGetFirstString(item, "streamRedirectionId", "streamRedirectionType", "redirectionId", "redirectionType", "id", "channelId", "channel", "role", "name")
            : TryGetFirstString(item, "classicRedirectionId", "classicRedirectionType", "redirectionId", "redirectionType", "id", "channelId", "channel", "role", "name");

        return new SonarRedirection(
            normalizedMode == "stream"
                ? NormalizeStreamRedirectionId(id)
                : NormalizeClassicRedirectionId(id),
            TryGetFirstString(item, "deviceId", "deviceID", "audioDeviceId", "audioDeviceID", "idDevice") ?? "",
            TryGetBool(item, "isRunning") ?? false);
    }

    private static SonarFallbackDevice ReadFallbackDevice(JsonElement item)
    {
        return new SonarFallbackDevice(
            TryGetString(item, "id") ?? "",
            TryGetBool(item, "isActive") ?? false,
            TryGetBool(item, "isExcluded") ?? false);
    }

    private static double ReadVolume(JsonElement node)
    {
        if (!TryGetPropertyIgnoreCase(node, "volume", out var volume)) throw new InvalidOperationException("Sonar state is missing volume");
        var scalar = volume.GetDouble();
        return scalar <= 1 ? scalar * 100 : scalar;
    }

    private static bool ReadMuted(JsonElement node)
    {
        if (TryGetPropertyIgnoreCase(node, "muted", out var muted)) return muted.GetBoolean();
        if (TryGetPropertyIgnoreCase(node, "isMuted", out var isMuted)) return isMuted.GetBoolean();
        return false;
    }

    private static string BuildPutRoute(string mode, string targetRole, string streamMix, string operation, string value)
    {
        var normalizedMode = NormalizeMode(mode);
        if (normalizedMode == "stream")
        {
            var field = operation == "mute" ? "isMuted" : "volume";
            return $"/VolumeSettings/streamer/{NormalizeStreamMix(streamMix)}/{HttpChannel(targetRole, normalizedMode)}/{field}/{value}";
        }

        var classicOperation = operation == "mute" ? "Mute" : "Volume";
        return $"/VolumeSettings/classic/{HttpChannel(targetRole, normalizedMode)}/{classicOperation}/{value}";
    }

    private static string BuildDeviceRoute(string mode, string targetRole, string streamMix, string deviceId)
    {
        var escapedDeviceId = Uri.EscapeDataString(deviceId);
        var normalizedMode = NormalizeMode(mode);
        if (normalizedMode == "stream")
            return $"/StreamRedirections/{StreamRedirectionChannel(streamMix)}/deviceId/{escapedDeviceId}";

        if (targetRole == "master")
            throw new InvalidOperationException("Master does not have a classic output redirection route");

        return $"/ClassicRedirections/{ClassicRedirectionChannel(targetRole)}/deviceId/{escapedDeviceId}";
    }

    private static string BuildInputDeviceRoute(string mode, string deviceId)
    {
        var escapedDeviceId = Uri.EscapeDataString(deviceId);
        return NormalizeMode(mode) == "stream"
            ? $"/StreamRedirections/{StreamRedirectionChannel("mic")}/deviceId/{escapedDeviceId}"
            : $"/ClassicRedirections/{ClassicRedirectionChannel("chatCapture")}/deviceId/{escapedDeviceId}";
    }

    private static string VolumeSettingsRoute(string mode)
    {
        return NormalizeMode(mode) == "stream" ? "/VolumeSettings/streamer" : "/VolumeSettings/classic";
    }

    private static string HttpChannel(string targetRole, string mode)
    {
        if (targetRole == "master") return mode == "classic" ? "Master" : "master";
        return targetRole switch
        {
            "game" => "game",
            "chatRender" => "chatRender",
            "media" => "media",
            "aux" => "aux",
            "chatCapture" => "chatCapture",
            _ => throw new InvalidOperationException($"Unsupported Sonar targetRole: {targetRole}")
        };
    }

    private static string ClassicRedirectionChannel(string targetRole)
    {
        return targetRole switch
        {
            "game" => "1",
            "chatRender" => "2",
            "chatCapture" => "3",
            "media" => "7",
            "aux" => "8",
            _ => throw new InvalidOperationException($"Unsupported Sonar classic redirection targetRole: {targetRole}")
        };
    }

    private static string StreamRedirectionChannel(string streamMix)
    {
        return streamMix switch
        {
            "streaming" => "0",
            "monitoring" => "1",
            "mic" => "2",
            _ => NormalizeStreamMix(streamMix) == "streaming" ? "0" : "1"
        };
    }

    private static string NormalizeClassicRedirectionId(string? id)
    {
        return NormalizeRedirectionToken(id) switch
        {
            "game" => "1",
            "chat" or "chatrender" or "chat-render" => "2",
            "mic" or "microphone" or "chatcapture" or "chat-capture" => "3",
            "media" => "7",
            "aux" => "8",
            var value => value
        };
    }

    private static string NormalizeStreamRedirectionId(string? id)
    {
        return NormalizeRedirectionToken(id) switch
        {
            "stream" or "streaming" => "0",
            "monitor" or "monitoring" => "1",
            "mic" or "microphone" or "chatcapture" or "chat-capture" => "2",
            var value => value
        };
    }

    private static string NormalizeRedirectionToken(string? id)
    {
        return string.IsNullOrWhiteSpace(id)
            ? ""
            : id.Trim().Trim('"').Replace("_", "", StringComparison.Ordinal).ToLowerInvariant();
    }

    private static string NormalizeMode(string? mode)
    {
        return string.Equals(mode, "streamer", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(mode, "stream", StringComparison.OrdinalIgnoreCase)
            ? "stream"
            : "classic";
    }

    private static string NormalizeStreamMix(string? streamMix)
    {
        return string.Equals(streamMix, "streaming", StringComparison.OrdinalIgnoreCase) ? "streaming" : "monitoring";
    }

    private static string NormalizeRotationMode(string? rotationMode, string currentMode)
    {
        return rotationMode switch
        {
            "all-classic" => "all-classic",
            "all-monitoring" => "all-monitoring",
            "all-streaming" => "all-streaming",
            "all-auto-detect" => NormalizeMode(currentMode) == "stream" ? "all-monitoring" : "all-classic",
            _ => "target"
        };
    }

    private static string SummarizeVolumeShape(JsonElement root, string mode)
    {
        var hasMasters = TryGetPropertyIgnoreCase(root, "masters", out var masters);
        var hasDevices = TryGetPropertyIgnoreCase(root, "devices", out var devices);
        var deviceCount = hasDevices && devices.ValueKind == JsonValueKind.Object ? devices.EnumerateObject().Count() : 0;
        var deviceKeys = hasDevices && devices.ValueKind == JsonValueKind.Object
            ? string.Join(",", devices.EnumerateObject().Select(property => property.Name).Take(12))
            : "";
        var masterKeys = hasMasters && masters.ValueKind == JsonValueKind.Object
            ? string.Join(",", masters.EnumerateObject().Select(property => property.Name))
            : "";
        return $"mode={NormalizeMode(mode)} masters=[{masterKeys}] devices={deviceCount} deviceKeys=[{deviceKeys}]";
    }

    private static object SummarizeSelectedState(JsonElement root, string mode, string targetRole, string streamMix)
    {
        try
        {
            var state = ExtractState(root, mode, targetRole, streamMix);
            return new
            {
                ok = true,
                volume = Math.Round(state.Volume, 1),
                muted = state.Muted
            };
        }
        catch (Exception ex)
        {
            return new { ok = false, error = ex.Message };
        }
    }

    private static object SummarizeStreamMixes(JsonElement root, string targetRole)
    {
        return new
        {
            monitoring = SummarizeStreamMix(root, targetRole, "monitoring"),
            streaming = SummarizeStreamMix(root, targetRole, "streaming")
        };
    }

    private static object SummarizeStreamMix(JsonElement root, string targetRole, string streamMix)
    {
        try
        {
            var state = ExtractState(root, "stream", targetRole, streamMix);
            return new
            {
                present = true,
                volume = Math.Round(state.Volume, 1),
                muted = state.Muted
            };
        }
        catch (Exception ex)
        {
            return new { present = false, error = ex.Message };
        }
    }

    private static string SummarizeError(HttpStatusCode statusCode, string body)
    {
        var summary = $"Sonar HTTP {(int)statusCode}";
        if (string.IsNullOrWhiteSpace(body)) return summary;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (TryGetPropertyIgnoreCase(document.RootElement, "Message", out var message) &&
                message.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(message.GetString()))
            {
                return $"{summary}: {message.GetString()}";
            }
        }
        catch
        {
            // Fall back to a compact raw body below.
        }

        var compact = body.Replace('\r', ' ').Replace('\n', ' ');
        if (compact.Length > 300) compact = compact[..300] + "...";
        return $"{summary}: {compact}";
    }

    private static bool TryGetSonarWebServerAddress(JsonElement root, out string address)
    {
        address = "";
        if (!TryGetPropertyIgnoreCase(root, "subApps", out var subApps)) return false;
        if (!TryGetPropertyIgnoreCase(subApps, "sonar", out var sonar)) return false;
        if (IsFalse(sonar, "isEnabled") || IsFalse(sonar, "isReady") || IsFalse(sonar, "isRunning")) return false;
        if (!TryGetPropertyIgnoreCase(sonar, "metadata", out var metadata)) return false;
        if (!TryGetPropertyIgnoreCase(metadata, "webServerAddress", out var webServerAddress)) return false;
        address = webServerAddress.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(address);
    }

    private static bool IsFalse(JsonElement element, string propertyName)
    {
        return TryGetPropertyIgnoreCase(element, propertyName, out var property) &&
               property.ValueKind is JsonValueKind.False;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var property)) return null;
        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.GetRawText(),
            _ => null
        };
    }

    private static string? TryGetFirstString(JsonElement element, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = TryGetString(element, propertyName);
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        return null;
    }

    private static bool? TryGetBool(JsonElement element, string propertyName)
    {
        return TryGetPropertyIgnoreCase(element, propertyName, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;
    }

    private static bool IsDisabledChatMixState(JsonElement state)
    {
        return state.ValueKind switch
        {
            JsonValueKind.Number when state.TryGetInt32(out var value) => value == 0,
            JsonValueKind.String => string.Equals(state.GetString(), "disabled", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(state.GetString(), "0", StringComparison.OrdinalIgnoreCase),
            JsonValueKind.False => true,
            _ => false
        };
    }

    private static JsonElement GetRequiredProperty(JsonElement element, string propertyName)
    {
        if (TryGetPropertyIgnoreCase(element, propertyName, out var property)) return property;
        throw new InvalidOperationException($"Sonar state is missing '{propertyName}'");
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement property)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in element.EnumerateObject())
            {
                if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate.Value;
                    return true;
                }
            }
        }

        property = default;
        return false;
    }

    private static IEnumerable<string> CandidateSubAppsUrls()
    {
        foreach (var file in CandidateCorePropsFiles())
        {
            if (!File.Exists(file)) continue;
            JsonDocument? document = null;
            try
            {
                document = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var key in new[] { "ggEncryptedAddress", "encryptedAddress", "address" })
                {
                    if (document.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String)
                    {
                        var address = value.GetString();
                        if (!string.IsNullOrWhiteSpace(address))
                            yield return $"{(address.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? address : "https://" + address).TrimEnd('/')}/subApps";
                    }
                }
            }
            finally
            {
                document?.Dispose();
            }
        }

        yield return "https://127.0.0.1:6327/subApps";
    }

    private static IEnumerable<string> CandidateCorePropsFiles()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(programData, "SteelSeries", "GG", "coreProps.json");
        yield return Path.Combine(programData, "SteelSeries", "SteelSeries Engine 3", "coreProps.json");
        if (!string.IsNullOrWhiteSpace(localAppData))
            yield return Path.Combine(localAppData, "SteelSeries", "GG", "coreProps.json");
    }

    private static bool IsLoopbackHttpUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        return (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
               (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase));
    }
}
