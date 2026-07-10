using System.Reflection;
using log4net;
using StreamDockSDK;
using StreamDockSDK.Attributes;

namespace StreamDockSonar;

[SDPlugin(
    PackageId = "local.streamdock.sonar",
    SdkVersion = 1,
    Name = "Stream Dock Sonar",
    Version = "0.3.4",
    Author = "local",
    Description = "Control SteelSeries GG Sonar mixer volume and mute directly.",
    Category = "GG-Sonar",
    CategoryIcon = "icons/plugin",
    Icon = "icons/plugin",
    CodePath = "plugin/StreamDockSonar.exe",
    CodePathWin = "plugin/StreamDockSonar.exe",
    PropertyInspectorPath = "property-inspector.html",
    MinimumVersionOfSoftware = "3.10.188.226"
)]
[SDPluginOS(Platform = "windows", MinimumVersion = "10")]
public sealed class SonarPlugin : StreamDockPlugin
{
    private static readonly ILog Log = LogManager.GetLogger(typeof(SonarPlugin));

    public override void RegisterEventHandlers()
    {
        base.RegisterEventHandlers();
        Connection.Connected += (_, _) =>
        {
            Log.Info("Connected to Stream Dock; discovering Sonar action handlers");
            HandlerManager.DiscoverHandlers(Assembly.GetExecutingAssembly());
        };
        Connection.Disconnected += (_, _) => Log.Warn("Disconnected from Stream Dock");
    }
}
