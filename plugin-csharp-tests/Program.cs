using System.Net;
using System.Net.Sockets;
using System.Text;
using StreamDockSonar;

var tests = new (string Name, Func<Task> Run)[]
{
    ("classic mode does not call streamer route", ClassicModeDoesNotCallStreamerAsync),
    ("stream mode calls streamer route", StreamModeCallsStreamerAsync),
    ("mode mismatch 500 is user visible", ModeMismatch500IsUserVisibleAsync)
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

static async Task StreamModeCallsStreamerAsync()
{
    using var server = FakeSonarServer.Start("stream");
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.SetMuteAsync("chatRender", true);

    AssertEqual(true, result.Success, "success");
    AssertEqual("/VolumeSettings/streamer/monitoring/chatRender/isMuted/true", server.LastPutPath, "stream put path");
}

static async Task ModeMismatch500IsUserVisibleAsync()
{
    using var server = FakeSonarServer.Start("stream", failPut: true);
    using var client = new SonarClient(server.BaseUrl);

    var result = await client.SetVolumeAsync("game", 50);

    AssertEqual(false, result.Success, "success");
    AssertEqual(500, result.StatusCode, "status");
    if (result.ErrorSummary == null || !result.ErrorSummary.Contains("Cannot be called in current mode", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"Expected mode mismatch message, got '{result.ErrorSummary}'");
}

static void AssertEqual<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{label}: expected '{expected}', got '{actual}'");
}

sealed class FakeSonarServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly string _mode;
    private readonly bool _failPut;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;

    private FakeSonarServer(HttpListener listener, int port, string mode, bool failPut)
    {
        _listener = listener;
        _mode = mode;
        _failPut = failPut;
        BaseUrl = $"http://127.0.0.1:{port}";
        _loop = Task.Run(ServeAsync);
    }

    public string BaseUrl { get; }
    public List<string> Requests { get; } = [];
    public string? LastPutPath { get; private set; }

    public static FakeSonarServer Start(string mode, bool failPut = false)
    {
        var port = GetFreeTcpPort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        return new FakeSonarServer(listener, port, mode, failPut);
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
                  "Stream": { "Monitoring": { "Volume": 0.5, "Muted": false } }
                },
                "ChatRender": {
                  "Classic": { "Volume": 0.5, "Muted": false },
                  "Stream": { "Monitoring": { "Volume": 0.5, "Muted": false } }
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
