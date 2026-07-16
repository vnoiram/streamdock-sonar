using System.Net;
using System.Net.Sockets;
using System.Text;
using StreamDockSonar;

var tests = new (string Name, Func<Task> Run)[]
{
    ("classic mode does not call streamer route", ClassicModeDoesNotCallStreamerAsync),
    ("stream monitoring mode calls monitoring route", StreamMonitoringModeCallsMonitoringAsync),
    ("stream streaming mode calls streaming route", StreamStreamingModeCallsStreamingAsync),
    ("mode mismatch 500 is user visible", ModeMismatch500IsUserVisibleAsync),
    ("diagnostics probes classic and streamer settings", DiagnosticsProbesClassicAndStreamerAsync),
    ("overview settings normalize targets", OverviewSettingsNormalizeTargets),
    ("overview selected targets render states", OverviewSelectedTargetsRenderStatesAsync),
    ("overview missing target renders error cell", OverviewMissingTargetRendersErrorCellAsync),
    ("chatmix get and set uses ChatMix route", ChatMixGetAndSetUsesRouteAsync),
    ("stale sonar endpoint rediscovered and original request retried", StaleSonarEndpointRediscoveredAndOriginalRequestRetriedAsync),
    ("chatmix disabled is user visible error", ChatMixDisabledIsUserVisibleErrorAsync),
    ("settings support negative step and invert alias", SettingsSupportNegativeStepAndInvertAlias),
    ("classic output device uses classic redirection route", ClassicOutputDeviceUsesRedirectionRouteAsync),
    ("classic all output device updates classic channels", ClassicAllOutputDeviceUpdatesClassicChannelsAsync),
    ("stream output device uses stream redirection route", StreamOutputDeviceUsesRedirectionRouteAsync),
    ("classic master output device is user visible error", ClassicMasterOutputDeviceIsUserVisibleErrorAsync),
    ("output devices filters active non virtual render devices", OutputDevicesFiltersActiveRenderDevicesAsync),
    ("classic input device uses mic redirection route", ClassicInputDeviceUsesMicRedirectionRouteAsync),
    ("stream input device uses mic redirection route", StreamInputDeviceUsesMicRedirectionRouteAsync),
    ("input devices filters active non virtual capture devices", InputDevicesFiltersActiveCaptureDevicesAsync),
    ("config profiles filter by target role", ConfigProfilesFilterByTargetRoleAsync),
    ("select config profile uses select route", SelectConfigProfileUsesRouteAsync),
    ("classic rotate output uses next render device", ClassicRotateOutputUsesNextRenderDeviceAsync),
    ("stream rotate output uses next render device", StreamRotateOutputUsesNextRenderDeviceAsync),
    ("stream target rotate output is user visible error", StreamTargetRotateOutputIsUserVisibleErrorAsync),
    ("classic rotate all output updates classic channels", ClassicRotateAllOutputUpdatesClassicChannelsAsync),
    ("stream rotate monitoring output updates monitoring mix", StreamRotateMonitoringOutputUpdatesMonitoringMixAsync),
    ("stream rotate streaming output updates streaming mix", StreamRotateStreamingOutputUpdatesStreamingMixAsync),
    ("auto rotate output follows current sonar mode", AutoRotateOutputFollowsCurrentModeAsync),
    ("rotate output can include excluded devices", RotateOutputCanIncludeExcludedDevicesAsync),
    ("rotate output falls back when target fallback list is empty", RotateOutputFallsBackWhenTargetFallbackListIsEmptyAsync),
    ("rotate output matches encoded current device id", RotateOutputMatchesEncodedCurrentDeviceIdAsync),
    ("rotate output accepts alternate redirection id fields", RotateOutputAcceptsAlternateRedirectionIdFieldsAsync),
    ("rotate output supports previous direction", RotateOutputSupportsPreviousDirectionAsync),
    ("classic rotate input uses next capture device", ClassicRotateInputUsesNextCaptureDeviceAsync),
    ("classic rotate input accepts alternate redirection names", ClassicRotateInputAcceptsAlternateRedirectionNamesAsync),
    ("stream rotate input uses next capture device", StreamRotateInputUsesNextCaptureDeviceAsync),
    ("legacy streamer target maps stream mix", LegacyStreamerTargetMapsStreamMix)
};

foreach (var test in tests)
{
    try
    {
        await test.Run();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"FAIL {test.Name}: {ex}");
        return 1;
    }
}

return 0;

static async Task ClassicModeDoesNotCallStreamerAsync()
{
    using var server = FakeSonarServer.Start("classic");
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.SetVolumeAsync("game", 40);

    AssertEqual(true, result.Success, "success");
    AssertEqual("/VolumeSettings/classic/game/Volume/0.40", server.LastPutPath, "classic put path");
    AssertEqual(false, server.Requests.Any(path => path.Contains("/streamer/", StringComparison.OrdinalIgnoreCase)), "streamer route not called");
}

static async Task StreamMonitoringModeCallsMonitoringAsync()
{
    using var server = FakeSonarServer.Start("stream");
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.SetMuteAsync("chatRender", true, "monitoring");

    AssertEqual(true, result.Success, "success");
    AssertEqual("/VolumeSettings/streamer/monitoring/chatRender/isMuted/true", server.LastPutPath, "stream put path");
}

static async Task StreamStreamingModeCallsStreamingAsync()
{
    using var server = FakeSonarServer.Start("stream");
    using var client = new SonarClient(server.BaseUrl);

    var state = await client.GetChannelStateAsync("game", "streaming");
    var result = await client.SetVolumeAsync("game", 35, "streaming");

    AssertEqual(70.0, state.Volume, "streaming state volume");
    AssertEqual(false, state.Muted, "streaming state muted");
    AssertEqual(true, result.Success, "success");
    AssertEqual("/VolumeSettings/streamer/streaming/game/volume/0.35", server.LastPutPath, "streaming put path");
}

static async Task ModeMismatch500IsUserVisibleAsync()
{
    using var server = FakeSonarServer.Start("stream", failPut: true);
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.SetVolumeAsync("game", 50, "streaming");

    AssertEqual(false, result.Success, "success");
    AssertEqual(500, result.StatusCode, "status");
    if (result.ErrorSummary == null || !result.ErrorSummary.Contains("Cannot be called in current mode", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"Expected mode mismatch message, got '{result.ErrorSummary}'");
}

static async Task DiagnosticsProbesClassicAndStreamerAsync()
{
    using var server = FakeSonarServer.Start("stream");
    using var client = new SonarClient(server.BaseUrl);

    _ = await client.BuildDiagnosticsAsync("game", "streaming");

    AssertEqual(true, server.Requests.Contains("/VolumeSettings/classic"), "classic probe");
    AssertEqual(true, server.Requests.Contains("/VolumeSettings/streamer"), "streamer probe");
}

static Task OverviewSettingsNormalizeTargets()
{
    var defaults = SonarSettings.FromDictionary(null);
    AssertEqual(6, defaults.OverviewTargets.Count, "default overview target count");
    AssertEqual("master", defaults.OverviewTargets[0], "default first target");

    var empty = SonarSettings.FromDictionary(new Dictionary<string, object>
    {
        ["overviewTargets"] = Array.Empty<string>()
    });
    AssertEqual(1, empty.OverviewTargets.Count, "empty overview target count");
    AssertEqual("game", empty.OverviewTargets[0], "empty overview target fallback");

    var deduped = SonarSettings.FromDictionary(new Dictionary<string, object>
    {
        ["overviewTargets"] = new[] { "game", "game", "chatRender", "invalid", "media" }
    });
    AssertEqual(3, deduped.OverviewTargets.Count, "deduped overview target count");
    AssertEqual("chatRender", deduped.OverviewTargets[1], "deduped second target");
    return Task.CompletedTask;
}

static async Task OverviewSelectedTargetsRenderStatesAsync()
{
    using var server = FakeSonarServer.Start("stream");
    using var client = new SonarClient(server.BaseUrl);
    var settings = SonarSettings.FromDictionary(new Dictionary<string, object>
    {
        ["streamMix"] = "streaming",
        ["overviewTargets"] = new[] { "game", "chatRender" }
    });

    var states = await client.GetOverviewStatesAsync(settings.OverviewTargets, settings.StreamMix);
    var image = SonarOverviewRenderer.BuildImageDataUrl(states);
    var svg = DecodeSvgImageDataUrl(image);

    AssertEqual(2, states.Count, "overview state count");
    AssertEqual("GME", states[0].ShortLabel, "first short label");
    AssertEqual("70", states[0].ValueText, "first value");
    AssertEqual("M", states[1].ValueText, "muted value");
    AssertEqual(true, image.StartsWith("data:image/svg+xml;base64,", StringComparison.Ordinal), "svg data url");
    AssertEqual(true, svg.Contains(">GME<", StringComparison.Ordinal), "svg first label");
    AssertEqual(true, svg.Contains(">CHT<", StringComparison.Ordinal), "svg second label");
    AssertEqual(false, svg.Contains(">MED<", StringComparison.Ordinal), "svg excludes unselected label");
}

static async Task OverviewMissingTargetRendersErrorCellAsync()
{
    using var server = FakeSonarServer.Start("stream");
    using var client = new SonarClient(server.BaseUrl);

    var states = await client.GetOverviewStatesAsync(new[] { "game", "media" }, "streaming");

    AssertEqual("70", states[0].ValueText, "ok cell value");
    AssertEqual("ERR", states[1].ValueText, "error cell value");
}

static async Task ChatMixGetAndSetUsesRouteAsync()
{
    using var server = FakeSonarServer.Start("stream");
    using var client = new SonarClient(server.BaseUrl);

    var balance = await client.GetChatMixBalanceAsync();
    var result = await client.SetChatMixBalanceAsync(-0.25);

    AssertEqual(0.5, balance, "chatmix balance");
    AssertEqual(true, result.Success, "chatmix set success");
    AssertEqual("/ChatMix", server.LastPutPath, "chatmix put path");
    AssertEqual("balance=-0.25", server.LastPutQuery, "chatmix put query");
}

static async Task ChatMixDisabledIsUserVisibleErrorAsync()
{
    using var server = FakeSonarServer.Start("stream", chatMixState: 0);
    using var client = new SonarClient(server.BaseUrl);

    try
    {
        _ = await client.GetChatMixBalanceAsync();
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("disabled", StringComparison.OrdinalIgnoreCase))
    {
        return;
    }

    throw new InvalidOperationException("Expected disabled ChatMix error");
}

static async Task StaleSonarEndpointRediscoveredAndOriginalRequestRetriedAsync()
{
    var staleServer = FakeSonarServer.Start("classic");
    var staleBaseUrl = staleServer.BaseUrl;
    staleServer.Dispose();

    using var newServer = FakeSonarServer.Start("classic");
    using var coreServer = FakeCoreServer.Start(newServer.BaseUrl);
    using var client = new SonarClient(staleBaseUrl, [coreServer.SubAppsUrl]);

    var result = await client.SetChatMixBalanceAsync(-0.25);

    AssertEqual(true, result.Success, "retry success");
    AssertEqual("/ChatMix", newServer.LastPutPath, "retried put path");
    AssertEqual("balance=-0.25", newServer.LastPutQuery, "retried put query");
    AssertEqual(1, coreServer.RequestCount, "rediscovery request count");
}

static Task SettingsSupportNegativeStepAndInvertAlias()
{
    var settings = SonarSettings.FromDictionary(new Dictionary<string, object>
    {
        ["step"] = -3,
        ["invert"] = true,
        ["rotateTicks"] = 5
    });

    AssertEqual(-3, settings.Step, "negative step");
    AssertEqual(true, settings.InvertKnob, "invert setting");
    AssertEqual(5, settings.RotateTicks, "rotate ticks");

    var legacySettings = SonarSettings.FromDictionary(new Dictionary<string, object>
    {
        ["invertKnob"] = true
    });
    AssertEqual(true, legacySettings.InvertKnob, "legacy invertKnob setting");
    return Task.CompletedTask;
}

static async Task ClassicOutputDeviceUsesRedirectionRouteAsync()
{
    using var server = FakeSonarServer.Start("classic");
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.SetOutputDeviceAsync("game", "monitoring", "{game-device}", CancellationToken.None);

    AssertEqual(true, result.Success, "classic output success");
    AssertEqual("/ClassicRedirections/1/deviceId/%7Bgame-device%7D", server.LastPutPath, "classic output path");
}

static async Task ClassicAllOutputDeviceUpdatesClassicChannelsAsync()
{
    using var server = FakeSonarServer.Start("classic");
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.SetOutputDeviceAsync("all", "monitoring", "all-device", CancellationToken.None);

    AssertEqual(true, result.Success, "classic all output success");
    AssertEqual(4, server.PutPaths.Count(path => path.StartsWith("/ClassicRedirections/", StringComparison.Ordinal)), "classic all put count");
    AssertEqual(true, server.PutPaths.Contains("/ClassicRedirections/1/deviceId/all-device"), "game route");
    AssertEqual(true, server.PutPaths.Contains("/ClassicRedirections/2/deviceId/all-device"), "chat route");
    AssertEqual(true, server.PutPaths.Contains("/ClassicRedirections/7/deviceId/all-device"), "media route");
    AssertEqual(true, server.PutPaths.Contains("/ClassicRedirections/8/deviceId/all-device"), "aux route");
}

static async Task StreamOutputDeviceUsesRedirectionRouteAsync()
{
    using var server = FakeSonarServer.Start("stream");
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.SetOutputDeviceAsync("game", "streaming", "stream-device", CancellationToken.None);

    AssertEqual(true, result.Success, "stream output success");
    AssertEqual("/StreamRedirections/0/deviceId/stream-device", server.LastPutPath, "stream output path");
}

static async Task ClassicMasterOutputDeviceIsUserVisibleErrorAsync()
{
    using var server = FakeSonarServer.Start("classic");
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.SetOutputDeviceAsync("master", "monitoring", "device", CancellationToken.None);

    AssertEqual(false, result.Success, "classic master success");
    if (result.ErrorSummary == null || !result.ErrorSummary.Contains("Master does not have", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"Expected master route error, got '{result.ErrorSummary}'");
}

static async Task OutputDevicesFiltersActiveRenderDevicesAsync()
{
    using var server = FakeSonarServer.Start("classic");
    using var client = new SonarClient(server.BaseUrl);

    var devices = await client.GetOutputDevicesAsync();

    AssertEqual(2, devices.Count, "output device count");
    AssertEqual("render-device", devices[0].Id, "output device id");
    AssertEqual("Speakers", devices[0].FriendlyName, "output device name");
}

static async Task ClassicInputDeviceUsesMicRedirectionRouteAsync()
{
    using var server = FakeSonarServer.Start("classic");
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.SetInputDeviceAsync("{mic-device}", CancellationToken.None);

    AssertEqual(true, result.Success, "classic input success");
    AssertEqual("/ClassicRedirections/3/deviceId/%7Bmic-device%7D", server.LastPutPath, "classic input path");
}

static async Task StreamInputDeviceUsesMicRedirectionRouteAsync()
{
    using var server = FakeSonarServer.Start("stream");
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.SetInputDeviceAsync("stream-mic", CancellationToken.None);

    AssertEqual(true, result.Success, "stream input success");
    AssertEqual("/StreamRedirections/2/deviceId/stream-mic", server.LastPutPath, "stream input path");
}

static async Task InputDevicesFiltersActiveCaptureDevicesAsync()
{
    using var server = FakeSonarServer.Start("classic");
    using var client = new SonarClient(server.BaseUrl);

    var devices = await client.GetInputDevicesAsync();

    AssertEqual(2, devices.Count, "input device count");
    AssertEqual("capture-device", devices[0].Id, "input device id");
    AssertEqual("Microphone", devices[0].FriendlyName, "input device name");
}

static async Task ConfigProfilesFilterByTargetRoleAsync()
{
    using var server = FakeSonarServer.Start("classic");
    using var client = new SonarClient(server.BaseUrl);

    var profiles = await client.GetConfigProfilesAsync("game");
    var selected = await client.GetSelectedConfigProfileAsync("game");

    AssertEqual(2, profiles.Count, "game profile count");
    AssertEqual("game-custom", profiles[0].Id, "custom profiles sorted first");
    AssertEqual("game-selected", selected?.Id, "selected game profile");
}

static async Task SelectConfigProfileUsesRouteAsync()
{
    using var server = FakeSonarServer.Start("classic");
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.SelectConfigProfileAsync("game-custom");

    AssertEqual(true, result.Success, "profile select success");
    AssertEqual("/Configs/game-custom/select", server.LastPutPath, "profile select path");
}

static async Task ClassicRotateOutputUsesNextRenderDeviceAsync()
{
    using var server = FakeSonarServer.Start("classic");
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.RotateOutputDeviceAsync("game", "monitoring");

    AssertEqual(true, result.Success, "classic rotate success");
    AssertEqual("/ClassicRedirections/1/deviceId/render-device-2", server.LastPutPath, "classic rotate path");
}

static async Task StreamRotateOutputUsesNextRenderDeviceAsync()
{
    using var server = FakeSonarServer.Start("stream");
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.RotateOutputDeviceAsync("game", "streaming", "all-streaming");

    AssertEqual(true, result.Success, "stream rotate success");
    AssertEqual("/StreamRedirections/0/deviceId/render-device-2", server.LastPutPath, "stream rotate path");
}

static async Task StreamTargetRotateOutputIsUserVisibleErrorAsync()
{
    using var server = FakeSonarServer.Start("stream");
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.RotateOutputDeviceAsync("game", "streaming");

    AssertEqual(false, result.Success, "stream target rotate failure");
    AssertEqual(true, result.ErrorSummary?.Contains("mix-based", StringComparison.OrdinalIgnoreCase) == true, "stream target rotate error");
    AssertEqual(0, server.PutPaths.Count, "stream target rotate should not put");
}

static async Task ClassicRotateAllOutputUpdatesClassicChannelsAsync()
{
    using var server = FakeSonarServer.Start("classic");
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.RotateOutputDeviceAsync("game", "monitoring", "all-classic");

    AssertEqual(true, result.Success, "classic rotate all success");
    AssertEqual(4, server.PutPaths.Count(path => path.StartsWith("/ClassicRedirections/", StringComparison.Ordinal)), "classic put count");
    AssertEqual(true, server.PutPaths.Contains("/ClassicRedirections/1/deviceId/render-device-2"), "game route");
    AssertEqual(true, server.PutPaths.Contains("/ClassicRedirections/2/deviceId/render-device-2"), "chat route");
    AssertEqual(true, server.PutPaths.Contains("/ClassicRedirections/7/deviceId/render-device-2"), "media route");
    AssertEqual(true, server.PutPaths.Contains("/ClassicRedirections/8/deviceId/render-device-2"), "aux route");
}

static async Task StreamRotateMonitoringOutputUpdatesMonitoringMixAsync()
{
    using var server = FakeSonarServer.Start("stream");
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.RotateOutputDeviceAsync("game", "monitoring", "all-monitoring");

    AssertEqual(true, result.Success, "stream rotate monitoring success");
    AssertEqual(1, server.PutPaths.Count(path => path.StartsWith("/StreamRedirections/", StringComparison.Ordinal)), "stream put count");
    AssertEqual(true, server.PutPaths.Contains("/StreamRedirections/1/deviceId/render-device-2"), "monitoring route");
}

static async Task StreamRotateStreamingOutputUpdatesStreamingMixAsync()
{
    using var server = FakeSonarServer.Start("stream");
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.RotateOutputDeviceAsync("game", "streaming", "all-streaming");

    AssertEqual(true, result.Success, "stream rotate streaming success");
    AssertEqual(1, server.PutPaths.Count(path => path.StartsWith("/StreamRedirections/", StringComparison.Ordinal)), "stream put count");
    AssertEqual(true, server.PutPaths.Contains("/StreamRedirections/0/deviceId/render-device-2"), "streaming route");
}

static async Task AutoRotateOutputFollowsCurrentModeAsync()
{
    using var classicServer = FakeSonarServer.Start("classic");
    using var classicClient = new SonarClient(classicServer.BaseUrl);
    using var streamServer = FakeSonarServer.Start("stream");
    using var streamClient = new SonarClient(streamServer.BaseUrl);

    _ = await classicClient.RotateOutputDeviceAsync("game", "monitoring", "all-auto-detect");
    _ = await streamClient.RotateOutputDeviceAsync("game", "monitoring", "all-auto-detect");

    AssertEqual(4, classicServer.PutPaths.Count(path => path.StartsWith("/ClassicRedirections/", StringComparison.Ordinal)), "classic auto count");
    AssertEqual(1, streamServer.PutPaths.Count(path => path.StartsWith("/StreamRedirections/", StringComparison.Ordinal)), "stream auto count");
}

static async Task RotateOutputCanIncludeExcludedDevicesAsync()
{
    using var server = FakeSonarServer.Start("classic");
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.RotateOutputDeviceAsync("game", "monitoring", "target", allowExcludedDevices: true);

    AssertEqual(true, result.Success, "rotate with excluded success");
    AssertEqual(false, server.Requests.Contains("/FallbackSettings/lists"), "fallback list not used");
    AssertEqual("/ClassicRedirections/1/deviceId/render-device-2", server.LastPutPath, "rotate with excluded path");
}

static async Task RotateOutputFallsBackWhenTargetFallbackListIsEmptyAsync()
{
    using var server = FakeSonarServer.Start("classic");
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.RotateOutputDeviceAsync("chatRender", "monitoring");

    AssertEqual(true, result.Success, "rotate empty fallback success");
    AssertEqual("/ClassicRedirections/2/deviceId/render-device-2", server.LastPutPath, "rotate empty fallback path");
}

static async Task RotateOutputMatchesEncodedCurrentDeviceIdAsync()
{
    using var server = FakeSonarServer.Start("classic", encodedGameDevice: true);
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.RotateOutputDeviceAsync("game", "monitoring");

    AssertEqual(true, result.Success, "rotate encoded current success");
    AssertEqual("/ClassicRedirections/1/deviceId/render-device-2", server.LastPutPath, "rotate encoded current path");
}

static async Task RotateOutputAcceptsAlternateRedirectionIdFieldsAsync()
{
    using var server = FakeSonarServer.Start("classic", alternateRedirections: true);
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.RotateOutputDeviceAsync("game", "monitoring");

    AssertEqual(true, result.Success, "rotate alternate id success");
    AssertEqual("/ClassicRedirections/1/deviceId/render-device-2", server.LastPutPath, "rotate alternate id path");
}

static async Task RotateOutputSupportsPreviousDirectionAsync()
{
    using var server = FakeSonarServer.Start("classic");
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.RotateOutputDeviceAsync("game", "monitoring", direction: -1);

    AssertEqual(true, result.Success, "rotate previous success");
    AssertEqual("/ClassicRedirections/1/deviceId/render-device-2", server.LastPutPath, "rotate previous wraps to last path");
}

static async Task ClassicRotateInputUsesNextCaptureDeviceAsync()
{
    using var server = FakeSonarServer.Start("classic");
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.RotateInputDeviceAsync();

    AssertEqual(true, result.Success, "classic rotate input success");
    AssertEqual("/ClassicRedirections/3/deviceId/capture-device-2", server.LastPutPath, "classic rotate input path");
}

static async Task ClassicRotateInputAcceptsAlternateRedirectionNamesAsync()
{
    using var server = FakeSonarServer.Start("classic", alternateRedirections: true);
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.RotateInputDeviceAsync();

    AssertEqual(true, result.Success, "classic rotate input alternate names success");
    AssertEqual("/ClassicRedirections/3/deviceId/capture-device-2", server.LastPutPath, "classic rotate input alternate names path");
}

static async Task StreamRotateInputUsesNextCaptureDeviceAsync()
{
    using var server = FakeSonarServer.Start("stream");
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.RotateInputDeviceAsync();

    AssertEqual(true, result.Success, "stream rotate input success");
    AssertEqual("/StreamRedirections/2/deviceId/capture-device-2", server.LastPutPath, "stream rotate input path");
}

static Task LegacyStreamerTargetMapsStreamMix()
{
    var settings = SonarSettings.FromDictionary(new Dictionary<string, object>
    {
        ["target"] = "streamer:streaming:chat",
        ["volumeStep"] = 4
    });

    AssertEqual("chatRender", settings.TargetRole, "targetRole");
    AssertEqual("streaming", settings.StreamMix, "streamMix");
    AssertEqual(4, settings.Step, "step");
    return Task.CompletedTask;
}

static void AssertEqual<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{label}: expected '{expected}', got '{actual}'");
}

static string DecodeSvgImageDataUrl(string image)
{
    const string prefix = "data:image/svg+xml;base64,";
    if (!image.StartsWith(prefix, StringComparison.Ordinal))
        throw new InvalidOperationException("Expected SVG data URL");
    return Encoding.UTF8.GetString(Convert.FromBase64String(image[prefix.Length..]));
}

sealed class FakeSonarServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _mode;
    private readonly bool _failPut;
    private readonly int _chatMixState;
    private readonly bool _encodedGameDevice;
    private readonly bool _alternateRedirections;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private FakeSonarServer(HttpListener listener, int port, string mode, bool failPut, int chatMixState, bool encodedGameDevice, bool alternateRedirections)
    {
        _listener = listener;
        _mode = mode;
        _failPut = failPut;
        _chatMixState = chatMixState;
        _encodedGameDevice = encodedGameDevice;
        _alternateRedirections = alternateRedirections;
        BaseUrl = $"http://127.0.0.1:{port}";
        _loop = Task.Run(ServeAsync);
    }

    public string BaseUrl { get; }
    public List<string> Requests { get; } = [];
    public List<string> PutPaths { get; } = [];
    public string? LastPutPath { get; private set; }
    public string? LastPutQuery { get; private set; }

    public static FakeSonarServer Start(string mode, bool failPut = false, int chatMixState = 1, bool encodedGameDevice = false, bool alternateRedirections = false)
    {
        var port = GetFreeTcpPort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return new FakeSonarServer(listener, port, mode, failPut, chatMixState, encodedGameDevice, alternateRedirections);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
        try { _loop.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        Requests.Add(context.Request.Url?.AbsolutePath ?? "");
        if (context.Request.HttpMethod == "PUT")
        {
            LastPutPath = context.Request.Url?.AbsolutePath;
            LastPutQuery = context.Request.Url?.Query.TrimStart('?');
            if (LastPutPath != null) PutPaths.Add(LastPutPath);
            if (_failPut)
            {
                context.Response.StatusCode = 500;
                await WriteAsync(context, "Cannot be called in current mode");
                return;
            }

            await WriteAsync(context, "{}");
            return;
        }

        var path = context.Request.Url?.AbsolutePath ?? "";
        if (path == "/mode")
        {
            await WriteAsync(context, $"\"{_mode}\"");
            return;
        }

        if (path == "/ChatMix")
        {
            await WriteAsync(context, $$"""{"balance":0.5,"state":{{_chatMixState}}}""");
            return;
        }

        if (path == "/audioDevices")
        {
            await WriteAsync(context, """
            [
              { "id": "render-device", "friendlyName": "Speakers", "dataFlow": "render", "role": "none", "state": "active", "isVad": false },
              { "id": "render-device-2", "friendlyName": "Headset", "dataFlow": "render", "role": "none", "state": "active", "isVad": false },
              { "id": "virtual-render", "friendlyName": "Sonar Game", "dataFlow": "render", "role": "game", "state": "active", "isVad": true },
              { "id": "inactive-render", "friendlyName": "Disabled", "dataFlow": "render", "role": "none", "state": "disabled", "isVad": false },
              { "id": "capture-device", "friendlyName": "Microphone", "dataFlow": "capture", "role": "none", "state": "active", "isVad": false },
              { "id": "capture-device-2", "friendlyName": "USB Mic", "dataFlow": "capture", "role": "none", "state": "active", "isVad": false }
            ]
            """);
            return;
        }

        if (path == "/FallbackSettings/lists")
        {
            await WriteAsync(context, """
            {
              "game": [
                { "id": "render-device", "isActive": true, "isExcluded": false },
                { "id": "render-device-2", "isActive": true, "isExcluded": false },
                { "id": "inactive-render", "isActive": false, "isExcluded": false }
              ],
              "chatCapture": [
                { "id": "capture-device", "isActive": true, "isExcluded": false },
                { "id": "capture-device-2", "isActive": true, "isExcluded": false }
              ],
              "chatRender": [],
              "media": [],
              "aux": []
            }
            """);
            return;
        }

        if (path == "/ClassicRedirections")
        {
            var gameDeviceId = _encodedGameDevice ? "render%2Ddevice" : "render-device";
            if (_alternateRedirections)
            {
                await WriteAsync(context, """
                [
                  { "id": "aux", "deviceId": "render-device", "isRunning": true },
                  { "id": "chat", "deviceId": "render-device", "isRunning": true },
                  { "id": "game", "deviceId": "__GAME_DEVICE_ID__", "isRunning": true },
                  { "id": "media", "deviceId": "render-device", "isRunning": true },
                  { "id": "mic", "deviceId": "capture-device", "isRunning": true }
                ]
                """.Replace("__GAME_DEVICE_ID__", gameDeviceId));
                return;
            }

            await WriteAsync(context, """
            [
              { "id": 1, "deviceId": "__GAME_DEVICE_ID__", "isRunning": true },
              { "id": 2, "deviceId": "render-device", "isRunning": true },
              { "id": 7, "deviceId": "render-device", "isRunning": true },
              { "id": 8, "deviceId": "render-device", "isRunning": true },
              { "id": 3, "deviceId": "capture-device", "isRunning": true }
            ]
            """.Replace("__GAME_DEVICE_ID__", gameDeviceId));
            return;
        }

        if (path == "/StreamRedirections")
        {
            if (_alternateRedirections)
            {
                await WriteAsync(context, """
                [
                  { "streamRedirectionType": "monitoring", "audioDeviceId": "render-device", "isRunning": true },
                  { "streamRedirectionType": "streaming", "audioDeviceId": "render-device", "isRunning": true },
                  { "streamRedirectionType": "mic", "audioDeviceId": "capture-device", "isRunning": true }
                ]
                """);
                return;
            }

            await WriteAsync(context, """
            [
              { "streamRedirectionId": 1, "deviceId": "render-device", "isRunning": true },
              { "streamRedirectionId": 0, "deviceId": "render-device", "isRunning": true },
              { "streamRedirectionId": 2, "deviceId": "capture-device", "isRunning": true }
            ]
            """);
            return;
        }

        if (path == "/Configs")
        {
            await WriteAsync(context, """
            [
              { "id": "game-custom", "name": "Game Custom", "virtualAudioDevice": "game", "isPreset": false },
              { "id": "game-preset", "name": "Game Preset", "virtualAudioDevice": "game", "isPreset": true },
              { "id": "chat-profile", "name": "Chat Profile", "virtualAudioDevice": "chatRender", "isPreset": false }
            ]
            """);
            return;
        }

        if (path == "/Configs/selected")
        {
            await WriteAsync(context, """
            [
              { "id": "game-selected", "name": "Game Selected", "virtualAudioDevice": "game", "isPreset": false },
              { "id": "chat-profile", "name": "Chat Profile", "virtualAudioDevice": "chatRender", "isPreset": false }
            ]
            """);
            return;
        }

        if (path is "/VolumeSettings/classic" or "/VolumeSettings/streamer")
        {
            await WriteAsync(context, """
            {
              "Masters": {
                "Classic": { "Volume": 0.5, "Muted": false },
                "Stream": { "Monitoring": { "Volume": 0.5, "Muted": false } }
              },
              "Devices": {
                "Game": {
                  "Classic": { "Volume": 0.5, "Muted": false },
                  "Stream": {
                    "Monitoring": { "Volume": 0.5, "Muted": false },
                    "Streaming": { "Volume": 0.7, "Muted": false }
                  }
                },
                "ChatRender": {
                  "Classic": { "Volume": 0.5, "Muted": false },
                  "Stream": {
                    "Monitoring": { "Volume": 0.5, "Muted": false },
                    "Streaming": { "Volume": 0.7, "Muted": true }
                  }
                }
              }
            }
            """);
            return;
        }

        context.Response.StatusCode = 404;
        await WriteAsync(context, "{}");
    }

    private static async Task WriteAsync(HttpListenerContext context, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

sealed class FakeCoreServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _sonarBaseUrl;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private FakeCoreServer(HttpListener listener, int port, string sonarBaseUrl)
    {
        _listener = listener;
        _sonarBaseUrl = sonarBaseUrl;
        BaseUrl = $"http://127.0.0.1:{port}";
        _loop = Task.Run(ServeAsync);
    }

    public string BaseUrl { get; }
    public string SubAppsUrl => $"{BaseUrl}/subApps";
    public int RequestCount { get; private set; }

    public static FakeCoreServer Start(string sonarBaseUrl)
    {
        var port = GetFreeTcpPort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return new FakeCoreServer(listener, port, sonarBaseUrl);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _listener.Close();
        try { _loop.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts.Dispose();
    }

    private async Task ServeAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        RequestCount++;
        if (context.Request.Url?.AbsolutePath != "/subApps")
        {
            context.Response.StatusCode = 404;
            await WriteAsync(context, "{}");
            return;
        }

        await WriteAsync(context, $$"""
        {
          "subApps": {
            "sonar": {
              "isEnabled": true,
              "isReady": true,
              "isRunning": true,
              "metadata": {
                "webServerAddress": "{{_sonarBaseUrl}}"
              }
            }
          }
        }
        """);
    }

    private static async Task WriteAsync(HttpListenerContext context, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
