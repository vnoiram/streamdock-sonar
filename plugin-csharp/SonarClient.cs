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
        var route = NormalizeMode(mode) == "stream" ? "/volumeSettings/streamer" : "/volumeSettings/classic";
        var document = await RequestJsonAsync(route, HttpMethod.Get, null, cancellationToken);
        LastResult = SonarOperationResult.Ok(mode, route);
        return document;
    }

    public async Task<SonarChannelState> GetChannelStateAsync(string targetRole, CancellationToken cancellationToken = default)
    {
        var mode = await GetModeAsync(cancellationToken);
        using var settings = await GetVolumeSettingsAsync(mode, cancellationToken);
        var state = ExtractState(settings.RootElement, mode, targetRole);
        LastResult = SonarOperationResult.Ok(mode, NormalizeMode(mode) == "stream" ? "/volumeSettings/streamer" : "/volumeSettings/classic");
        return state;
    }

    public async Task<SonarOperationResult> SetVolumeAsync(string targetRole, double value, CancellationToken cancellationToken = default)
    {
        var mode = await GetModeAsync(cancellationToken);
        var route = BuildPutRoute(mode, targetRole, "volume", Math.Clamp(value / 100.0, 0, 1).ToString("0.00"));
        return await PutAsync(mode, route, cancellationToken);
    }

    public async Task<SonarOperationResult> SetMuteAsync(string targetRole, bool muted, CancellationToken cancellationToken = default)
    {
        var mode = await GetModeAsync(cancellationToken);
        var route = BuildPutRoute(mode, targetRole, "mute", muted ? "true" : "false");
        return await PutAsync(mode, route, cancellationToken);
    }

    public async Task<object> BuildDiagnosticsAsync(CancellationToken cancellationToken = default)
    {
        var discovery = "";
        var mode = "";
        var shape = "";
        try
        {
            discovery = await DiscoverAsync(cancellationToken);
            mode = await GetModeAsync(cancellationToken);
            using var settings = await GetVolumeSettingsAsync(mode, cancellationToken);
            shape = SummarizeVolumeShape(settings.RootElement, mode);
        }
        catch (Exception ex)
        {
            LastResult = SonarOperationResult.Error(mode, LastResult?.Route, LastResult?.StatusCode, ex.Message);
        }

        return new
        {
            discovery,
            mode,
            volumeSettings = shape,
            lastRequest = new
            {
                LastResult?.Route,
                LastResult?.StatusCode,
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
        if (!response.IsSuccessStatusCode)
        {
            LastResult = SonarOperationResult.Error(null, route, (int)response.StatusCode, SummarizeError(response.StatusCode, body));
            throw new InvalidOperationException(LastResult.ErrorSummary);
        }

        LastResult = SonarOperationResult.Ok(null, route, (int)response.StatusCode);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
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

    private static SonarChannelState ExtractState(JsonElement root, string mode, string targetRole)
    {
        var normalizedMode = NormalizeMode(mode);
        JsonElement node;
        if (targetRole == "master")
        {
            node = normalizedMode == "stream"
                ? root.GetProperty("masters").GetProperty("stream").GetProperty("monitoring")
                : root.GetProperty("masters").GetProperty("classic");
        }
        else
        {
            var device = root.GetProperty("devices").GetProperty(targetRole);
            node = normalizedMode == "stream"
                ? device.GetProperty("stream").GetProperty("monitoring")
                : device.GetProperty("classic");
        }

        return new SonarChannelState(ReadVolume(node), ReadMuted(node));
    }

    private static double ReadVolume(JsonElement node)
    {
        if (!node.TryGetProperty("volume", out var volume)) throw new InvalidOperationException("Sonar state is missing volume");
        var scalar = volume.GetDouble();
        return scalar <= 1 ? scalar * 100 : scalar;
    }

    private static bool ReadMuted(JsonElement node)
    {
        if (node.TryGetProperty("muted", out var muted)) return muted.GetBoolean();
        if (node.TryGetProperty("isMuted", out var isMuted)) return isMuted.GetBoolean();
        return false;
    }

    private static string BuildPutRoute(string mode, string targetRole, string operation, string value)
    {
        var normalizedMode = NormalizeMode(mode);
        if (normalizedMode == "stream")
        {
            var field = operation == "mute" ? "isMuted" : "volume";
            return $"/volumeSettings/streamer/monitoring/{HttpChannel(targetRole, normalizedMode)}/{field}/{value}";
        }

        var classicOperation = operation == "mute" ? "Mute" : "Volume";
        return $"/volumeSettings/classic/{HttpChannel(targetRole, normalizedMode)}/{classicOperation}/{value}";
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

    private static string NormalizeMode(string? mode)
    {
        return string.Equals(mode, "streamer", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(mode, "stream", StringComparison.OrdinalIgnoreCase)
            ? "stream"
            : "classic";
    }

    private static string SummarizeVolumeShape(JsonElement root, string mode)
    {
        var hasMasters = root.TryGetProperty("masters", out var masters);
        var hasDevices = root.TryGetProperty("devices", out var devices);
        var deviceCount = hasDevices && devices.ValueKind == JsonValueKind.Object ? devices.EnumerateObject().Count() : 0;
        var masterKeys = hasMasters && masters.ValueKind == JsonValueKind.Object
            ? string.Join(",", masters.EnumerateObject().Select(property => property.Name))
            : "";
        return $"mode={NormalizeMode(mode)} masters=[{masterKeys}] devices={deviceCount}";
    }

    private static string SummarizeError(HttpStatusCode statusCode, string body)
    {
        var summary = $"Sonar HTTP {(int)statusCode}";
        if (string.IsNullOrWhiteSpace(body)) return summary;
        var compact = body.Replace('\r', ' ').Replace('\n', ' ');
        if (compact.Length > 300) compact = compact[..300] + "...";
        return $"{summary}: {compact}";
    }

    private static bool TryGetSonarWebServerAddress(JsonElement root, out string address)
    {
        address = "";
        if (!root.TryGetProperty("subApps", out var subApps)) return false;
        if (!subApps.TryGetProperty("sonar", out var sonar)) return false;
        if (IsFalse(sonar, "isEnabled") || IsFalse(sonar, "isReady") || IsFalse(sonar, "isRunning")) return false;
        if (!sonar.TryGetProperty("metadata", out var metadata)) return false;
        if (!metadata.TryGetProperty("webServerAddress", out var webServerAddress)) return false;
        address = webServerAddress.GetString() ?? "";
        return !string.IsNullOrWhiteSpace(address);
    }

    private static bool IsFalse(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind is JsonValueKind.False;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
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
