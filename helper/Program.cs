using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace SonarAudioHelper;

[SupportedOSPlatform("windows10.0.17763.0")]
internal static class Program
{
    private const string DefaultPrefix = "http://127.0.0.1:41922/";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ConcurrentDictionary<Guid, ClientState> Clients = new();
    private static readonly AudioController Audio = new();
    private static string? _logFile;

    private static async Task Main(string[] args)
    {
        var prefix = args.FirstOrDefault(arg => arg.StartsWith("--prefix=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1]
            ?? Environment.GetEnvironmentVariable("STREAMDOCK_SONAR_HELPER_PREFIX")
            ?? DefaultPrefix;
        _logFile = args.FirstOrDefault(arg => arg.StartsWith("--log-file=", StringComparison.OrdinalIgnoreCase))?.Split('=', 2)[1]
            ?? Environment.GetEnvironmentVariable("STREAMDOCK_SONAR_HELPER_LOG");

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        listener.Start();
        Console.WriteLine($"Sonar audio helper listening on {prefix}");
        Log($"listening on {prefix}");

        _ = Task.Run(PublishLoopAsync);

        while (true)
        {
            var context = await listener.GetContextAsync();
            _ = Task.Run(() => HandleContextAsync(context));
        }
    }

    private static async Task HandleContextAsync(HttpListenerContext context)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode = 426;
            context.Response.Close();
            return;
        }

        var id = Guid.NewGuid();
        using var webSocketContext = await context.AcceptWebSocketAsync(subProtocol: null);
        var socket = webSocketContext.WebSocket;
        var state = new ClientState(socket);
        Clients[id] = state;

        try
        {
            await ReceiveLoopAsync(state);
        }
        finally
        {
            Clients.TryRemove(id, out _);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
            }
        }
    }

    private static async Task ReceiveLoopAsync(ClientState client)
    {
        var buffer = new byte[4096];
        while (client.Socket.State == WebSocketState.Open)
        {
            var result = await client.Socket.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                break;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
            var command = JsonSerializer.Deserialize<HelperCommand>(text, JsonOptions);
            if (command is null || string.IsNullOrWhiteSpace(command.Target))
            {
                if (command?.Command == "list_targets")
                {
                    await SendAsync(client.Socket, new
                    {
                        @event = "targets",
                        devices = Audio.ListDevices(),
                        sessions = Audio.ListSessions(),
                        deviceDetails = Audio.ListDeviceDetails(),
                        sessionDetails = Audio.ListSessionDetails()
                    });
                }
                continue;
            }

            client.Subscriptions[(command.TargetKind ?? "device") + ":" + command.Target] =
                new TargetRef(command.TargetKind ?? "device", command.Target, Math.Clamp(command.PollMs ?? 1000, 250, 10000));

            if (command.Command == "subscribe")
            {
                Log($"subscribe {command.TargetKind}:{command.Target}");
                await PublishStateAsync(client.Socket, command.TargetKind ?? "device", command.Target);
            }
            else if (command.Command == "toggle_mute")
            {
                Log($"toggle_mute {command.TargetKind}:{command.Target}");
                Audio.ToggleMute(command.TargetKind ?? "device", command.Target);
                await PublishStateAsync(client.Socket, command.TargetKind ?? "device", command.Target);
            }
            else if (command.Command == "volume_delta")
            {
                Log($"volume_delta {command.TargetKind}:{command.Target} {command.Amount}");
                Audio.AdjustVolume(command.TargetKind ?? "device", command.Target, command.Amount);
                await PublishStateAsync(client.Socket, command.TargetKind ?? "device", command.Target);
            }
        }
    }

    private static async Task PublishLoopAsync()
    {
        while (true)
        {
            await Task.Delay(250);
            foreach (var client in Clients.Values)
            {
                foreach (var target in client.Subscriptions.Values)
                {
                    if (client.Socket.State == WebSocketState.Open &&
                        DateTimeOffset.UtcNow - target.LastPublished >= TimeSpan.FromMilliseconds(target.PollMs))
                    {
                        target.LastPublished = DateTimeOffset.UtcNow;
                        await PublishStateAsync(client.Socket, target.TargetKind, target.Target);
                    }
                }
            }
        }
    }

    private static async Task PublishStateAsync(WebSocket socket, string targetKind, string target)
    {
        var state = Audio.TryGetState(targetKind, target);
        if (state is null)
        {
            await SendAsync(socket, new { @event = "unavailable", target });
            return;
        }

        await SendAsync(socket, new
        {
            @event = "state",
            target,
            payload = new
            {
                volume = state.VolumePercent,
                muted = state.Muted,
                available = true
            }
        });
    }

    private static async Task SendAsync(WebSocket socket, object payload)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        var bytes = Encoding.UTF8.GetBytes(json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
    }

    private static void Log(string message)
    {
        var line = $"{DateTimeOffset.Now:O} {message}";
        Console.WriteLine(line);
        if (!string.IsNullOrWhiteSpace(_logFile))
        {
            File.AppendAllText(_logFile, line + Environment.NewLine);
        }
    }

    private sealed record HelperCommand(string? Command, string? TargetKind, string? Target, float Amount, int? PollMs);
    private sealed class TargetRef
    {
        public TargetRef(string targetKind, string target, int pollMs)
        {
            TargetKind = targetKind;
            Target = target;
            PollMs = pollMs;
        }

        public string TargetKind { get; }
        public string Target { get; }
        public int PollMs { get; }
        public DateTimeOffset LastPublished { get; set; } = DateTimeOffset.MinValue;
    }
    private sealed record ClientState(WebSocket Socket)
    {
        public ConcurrentDictionary<string, TargetRef> Subscriptions { get; } = new();
    }
}

[SupportedOSPlatform("windows10.0.17763.0")]
internal sealed class AudioController
{
    public AudioState? TryGetState(string targetKind, string target)
    {
        return IsSession(targetKind) ? TryGetSessionState(target) : TryGetDeviceState(target);
    }

    public IReadOnlyList<string> ListDevices()
    {
        return ListDeviceDetails().Select(item => item.Name).ToList();
    }

    public IReadOnlyList<string> ListSessions()
    {
        return ListSessionDetails().Select(item => item.Name).ToList();
    }

    public IReadOnlyList<TargetInfo> ListDeviceDetails()
    {
        var names = new List<TargetInfo>();
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.Active, out var collection);
        collection.GetCount(out var count);
        for (uint i = 0; i < count; i++)
        {
            collection.Item(i, out var device);
            device.GetId(out var idPtr);
            var id = Marshal.PtrToStringUni(idPtr) ?? string.Empty;
            Marshal.FreeCoTaskMem(idPtr);
            names.Add(new TargetInfo(id, ReadFriendlyName(device)));
            Marshal.ReleaseComObject(device);
        }
        Marshal.ReleaseComObject(collection);
        Marshal.ReleaseComObject(enumerator);
        return names.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public IReadOnlyList<TargetInfo> ListSessionDetails()
    {
        var names = new Dictionary<string, TargetInfo>(StringComparer.OrdinalIgnoreCase);
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.Active, out var collection);
        collection.GetCount(out var deviceCount);
        for (uint i = 0; i < deviceCount; i++)
        {
            collection.Item(i, out var device);
            var managerIid = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref managerIid, ClsCtx.All, IntPtr.Zero, out var managerObject);
            var manager = (IAudioSessionManager2)managerObject;
            manager.GetSessionEnumerator(out var sessions);
            sessions.GetCount(out var sessionCount);
            for (var sessionIndex = 0; sessionIndex < sessionCount; sessionIndex++)
            {
                sessions.GetSession(sessionIndex, out var control);
                control.GetSessionInstanceIdentifier(out var sessionIdPtr);
                var sessionId = Marshal.PtrToStringUni(sessionIdPtr) ?? string.Empty;
                if (sessionIdPtr != IntPtr.Zero)
                {
                    Marshal.FreeCoTaskMem(sessionIdPtr);
                }
                var name = ReadSessionName(control);
                names[name] = new TargetInfo(sessionId, name);
                Marshal.ReleaseComObject(control);
            }
            Marshal.ReleaseComObject(sessions);
            Marshal.ReleaseComObject(manager);
            Marshal.ReleaseComObject(device);
        }
        Marshal.ReleaseComObject(collection);
        Marshal.ReleaseComObject(enumerator);
        return names.Values.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public void ToggleMute(string targetKind, string target)
    {
        if (IsSession(targetKind))
        {
            using var session = FindSession(target);
            if (session?.SimpleVolume is null)
            {
                return;
            }
            session.SimpleVolume.GetMute(out var muted);
            session.SimpleVolume.SetMute(!muted, Guid.Empty);
            return;
        }

        using var device = FindDevice(target);
        if (device?.EndpointVolume is null)
        {
            return;
        }
        device.EndpointVolume.GetMute(out var mutedDevice);
        device.EndpointVolume.SetMute(!mutedDevice, Guid.Empty);
    }

    public void AdjustVolume(string targetKind, string target, float amount)
    {
        if (IsSession(targetKind))
        {
            using var session = FindSession(target);
            if (session?.SimpleVolume is null)
            {
                return;
            }
            session.SimpleVolume.GetMasterVolume(out var current);
            session.SimpleVolume.SetMasterVolume(Clamp01(current + amount / 100f), Guid.Empty);
            return;
        }

        using var device = FindDevice(target);
        if (device?.EndpointVolume is null)
        {
            return;
        }
        device.EndpointVolume.GetMasterVolumeLevelScalar(out var currentDevice);
        device.EndpointVolume.SetMasterVolumeLevelScalar(Clamp01(currentDevice + amount / 100f), Guid.Empty);
    }

    private static AudioState? TryGetDeviceState(string target)
    {
        using var device = FindDevice(target);
        if (device?.EndpointVolume is null)
        {
            return null;
        }
        device.EndpointVolume.GetMasterVolumeLevelScalar(out var volume);
        device.EndpointVolume.GetMute(out var muted);
        return new AudioState(volume * 100f, muted);
    }

    private static AudioState? TryGetSessionState(string target)
    {
        using var session = FindSession(target);
        if (session?.SimpleVolume is null)
        {
            return null;
        }
        session.SimpleVolume.GetMasterVolume(out var volume);
        session.SimpleVolume.GetMute(out var muted);
        return new AudioState(volume * 100f, muted);
    }

    private static bool IsSession(string targetKind)
    {
        return targetKind.Equals("session", StringComparison.OrdinalIgnoreCase);
    }

    private static float Clamp01(float value)
    {
        return Math.Max(0f, Math.Min(1f, value));
    }

    private static DeviceHandle? FindDevice(string target)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.Active, out var collection);
        collection.GetCount(out var count);
        for (uint i = 0; i < count; i++)
        {
            collection.Item(i, out var device);
            var name = ReadFriendlyName(device);
            if (!name.Contains(target, StringComparison.OrdinalIgnoreCase))
            {
                Marshal.ReleaseComObject(device);
                continue;
            }

            var iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, ClsCtx.All, IntPtr.Zero, out var endpoint);
            return new DeviceHandle(device, (IAudioEndpointVolume)endpoint, name);
        }

        return null;
    }

    private static SessionHandle? FindSession(string target)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        enumerator.EnumAudioEndpoints(EDataFlow.eRender, DeviceState.Active, out var collection);
        collection.GetCount(out var deviceCount);
        for (uint i = 0; i < deviceCount; i++)
        {
            collection.Item(i, out var device);
            var managerIid = typeof(IAudioSessionManager2).GUID;
            device.Activate(ref managerIid, ClsCtx.All, IntPtr.Zero, out var managerObject);
            var manager = (IAudioSessionManager2)managerObject;
            manager.GetSessionEnumerator(out var sessions);
            sessions.GetCount(out var sessionCount);
            for (var sessionIndex = 0; sessionIndex < sessionCount; sessionIndex++)
            {
                sessions.GetSession(sessionIndex, out var control);
                var control2 = (IAudioSessionControl2)control;
                var name = ReadSessionName(control2);
                if (!name.Contains(target, StringComparison.OrdinalIgnoreCase))
                {
                    Marshal.ReleaseComObject(control);
                    continue;
                }

                return new SessionHandle(device, manager, sessions, control, (ISimpleAudioVolume)control, name);
            }

            Marshal.ReleaseComObject(sessions);
            Marshal.ReleaseComObject(manager);
            Marshal.ReleaseComObject(device);
        }

        return null;
    }

    private static string ReadFriendlyName(IMMDevice device)
    {
        device.OpenPropertyStore(0, out var store);
        var key = PropertyKeys.PkeyDeviceFriendlyName;
        store.GetValue(ref key, out var prop);
        var result = prop.Value ?? string.Empty;
        PropVariantClear(ref prop);
        Marshal.ReleaseComObject(store);
        return result;
    }

    private static string ReadSessionName(IAudioSessionControl2 control)
    {
        control.GetDisplayName(out var displayNamePtr);
        var displayName = Marshal.PtrToStringUni(displayNamePtr) ?? string.Empty;
        if (displayNamePtr != IntPtr.Zero)
        {
            Marshal.FreeCoTaskMem(displayNamePtr);
        }
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        control.GetProcessId(out var processId);
        try
        {
            return Process.GetProcessById((int)processId).ProcessName;
        }
        catch
        {
            return $"pid:{processId}";
        }
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);
}

internal sealed record AudioState(float VolumePercent, bool Muted);
internal sealed record TargetInfo(string Id, string Name);

internal sealed class DeviceHandle : IDisposable
{
    private readonly object _device;
    public DeviceHandle(object device, IAudioEndpointVolume endpointVolume, string name)
    {
        _device = device;
        EndpointVolume = endpointVolume;
        Name = name;
    }

    public IAudioEndpointVolume EndpointVolume { get; }
    public string Name { get; }

    public void Dispose()
    {
        Marshal.ReleaseComObject(EndpointVolume);
        Marshal.ReleaseComObject(_device);
    }
}

internal sealed class SessionHandle : IDisposable
{
    private readonly object _device;
    private readonly object _manager;
    private readonly object _enumerator;
    private readonly object _control;

    public SessionHandle(object device, object manager, object enumerator, object control, ISimpleAudioVolume simpleVolume, string name)
    {
        _device = device;
        _manager = manager;
        _enumerator = enumerator;
        _control = control;
        SimpleVolume = simpleVolume;
        Name = name;
    }

    public ISimpleAudioVolume SimpleVolume { get; }
    public string Name { get; }

    public void Dispose()
    {
        Marshal.ReleaseComObject(_control);
        Marshal.ReleaseComObject(_enumerator);
        Marshal.ReleaseComObject(_manager);
        Marshal.ReleaseComObject(_device);
    }
}

[ComImport]
[Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal sealed class MMDeviceEnumerator
{
}

[ComImport]
[Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    int EnumAudioEndpoints(EDataFlow dataFlow, DeviceState stateMask, out IMMDeviceCollection devices);
}

[ComImport]
[Guid("0BD7A1BE-7A1A-44DB-8397-C0F2EBB11C6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceCollection
{
    int GetCount(out uint count);
    int Item(uint index, out IMMDevice device);
}

[ComImport]
[Guid("D666063F-1587-4E43-81F1-B948E807363F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    int Activate(ref Guid iid, ClsCtx dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object interfacePointer);
    int OpenPropertyStore(int stgmAccess, out IPropertyStore properties);
    int GetId(out IntPtr id);
}

[ComImport]
[Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    int GetCount(out uint propertyCount);
    int GetAt(uint propertyIndex, out PropertyKey key);
    int GetValue(ref PropertyKey key, out PropVariant value);
}

[ComImport]
[Guid("5CDF2C82-841E-4546-9722-0CF74078229A2")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    int RegisterControlChangeNotify(IntPtr client);
    int UnregisterControlChangeNotify(IntPtr client);
    int GetChannelCount(out uint channelCount);
    int SetMasterVolumeLevel(float level, Guid eventContext);
    int SetMasterVolumeLevelScalar(float level, Guid eventContext);
    int GetMasterVolumeLevel(out float level);
    int GetMasterVolumeLevelScalar(out float level);
    int SetChannelVolumeLevel(uint channelNumber, float level, Guid eventContext);
    int SetChannelVolumeLevelScalar(uint channelNumber, float level, Guid eventContext);
    int GetChannelVolumeLevel(uint channelNumber, out float level);
    int GetChannelVolumeLevelScalar(uint channelNumber, out float level);
    int SetMute(bool isMuted, Guid eventContext);
    int GetMute(out bool isMuted);
}

[ComImport]
[Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionManager2
{
    int GetAudioSessionControl(IntPtr audioSessionGuid, uint streamFlags, out IAudioSessionControl2 sessionControl);
    int GetSimpleAudioVolume(IntPtr audioSessionGuid, uint streamFlags, out ISimpleAudioVolume audioVolume);
    int GetSessionEnumerator(out IAudioSessionEnumerator sessionEnumerator);
    int RegisterSessionNotification(IntPtr sessionNotification);
    int UnregisterSessionNotification(IntPtr sessionNotification);
    int RegisterDuckNotification(string sessionId, IntPtr duckNotification);
    int UnregisterDuckNotification(IntPtr duckNotification);
}

[ComImport]
[Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionEnumerator
{
    int GetCount(out int sessionCount);
    int GetSession(int sessionIndex, out IAudioSessionControl2 session);
}

[ComImport]
[Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioSessionControl2
{
    int GetState(out int state);
    int GetDisplayName(out IntPtr displayName);
    int SetDisplayName(string displayName, Guid eventContext);
    int GetIconPath(out IntPtr iconPath);
    int SetIconPath(string iconPath, Guid eventContext);
    int GetGroupingParam(out Guid groupingParam);
    int SetGroupingParam(Guid groupingParam, Guid eventContext);
    int RegisterAudioSessionNotification(IntPtr notifications);
    int UnregisterAudioSessionNotification(IntPtr notifications);
    int GetSessionIdentifier(out IntPtr sessionIdentifier);
    int GetSessionInstanceIdentifier(out IntPtr sessionInstanceIdentifier);
    int GetProcessId(out uint processId);
    int IsSystemSoundsSession();
    int SetDuckingPreference(bool optOut);
}

[ComImport]
[Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISimpleAudioVolume
{
    int SetMasterVolume(float level, Guid eventContext);
    int GetMasterVolume(out float level);
    int SetMute(bool isMuted, Guid eventContext);
    int GetMute(out bool isMuted);
}

internal enum EDataFlow
{
    eRender,
    eCapture,
    eAll
}

[Flags]
internal enum DeviceState
{
    Active = 0x00000001
}

[Flags]
internal enum ClsCtx
{
    InprocServer = 0x1,
    InprocHandler = 0x2,
    LocalServer = 0x4,
    RemoteServer = 0x10,
    All = InprocServer | InprocHandler | LocalServer | RemoteServer
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FmtId;
    public uint Pid;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropVariant
{
    public ushort ValueType;
    private readonly ushort _wReserved1;
    private readonly ushort _wReserved2;
    private readonly ushort _wReserved3;
    private readonly IntPtr _value;
    private readonly int _value2;

    public string? Value => ValueType == 31 ? Marshal.PtrToStringUni(_value) : null;
}

internal static class PropertyKeys
{
    public static readonly PropertyKey PkeyDeviceFriendlyName = new()
    {
        FmtId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        Pid = 14
    };
}
