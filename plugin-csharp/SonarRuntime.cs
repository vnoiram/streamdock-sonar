using log4net;

namespace StreamDockSonar;

public static class SonarRuntime
{
    public static SonarClient Client { get; } = new();
    public static SonarStateService State { get; } = new(Client);
}

public sealed class SonarStateService : IDisposable
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(SonarStateService));
    private readonly SonarClient _client;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly Timer _timer;
    private string _streamMix = "monitoring";

    public SonarStateService(SonarClient client)
    {
        _client = client;
        _timer = new Timer(_ => _ = RefreshAsync(), null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    public event Func<SonarSnapshot, Task>? StateChanged;

    public SonarSnapshot? Current { get; private set; }

    public void SetStreamMix(string streamMix)
    {
        _streamMix = string.Equals(streamMix, "streaming", StringComparison.OrdinalIgnoreCase) ? "streaming" : "monitoring";
    }

    public async Task<SonarSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            SonarSnapshot snapshot;
            try
            {
                var mode = await _client.GetModeAsync(cancellationToken);
                var states = await _client.GetOverviewStatesAsync(SonarSettings.AllTargetRoles, _streamMix, cancellationToken);
                var chatMix = await _client.GetChatMixBalanceAsync(cancellationToken);
                snapshot = new SonarSnapshot(
                    DateTimeOffset.Now,
                    mode,
                    _streamMix,
                    states.ToDictionary(state => state.TargetRole, StringComparer.OrdinalIgnoreCase),
                    chatMix,
                    null);
            }
            catch (Exception ex)
            {
                Log.Warn($"Sonar state refresh failed: {ex.Message}");
                snapshot = new SonarSnapshot(DateTimeOffset.Now, "", _streamMix, new Dictionary<string, SonarOverviewState>(), null, ex.Message);
            }

            Current = snapshot;
            await NotifyAsync(snapshot);
            return snapshot;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _refreshLock.Dispose();
    }

    private async Task NotifyAsync(SonarSnapshot snapshot)
    {
        var handlers = StateChanged;
        if (handlers == null) return;
        foreach (Func<SonarSnapshot, Task> handler in handlers.GetInvocationList())
        {
            try
            {
                await handler(snapshot);
            }
            catch (Exception ex)
            {
                Log.Warn($"Sonar state listener failed: {ex.Message}");
            }
        }
    }
}
