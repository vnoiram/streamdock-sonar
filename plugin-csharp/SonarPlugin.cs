using System.Reflection;
using StreamDockSDK;
using StreamDockSDK.Attributes;

namespace StreamDockSonar;

[SDPlugin(
    PackageId = "local.streamdock.sonar",
    SdkVersion = 1,
    Name = "Stream Dock Sonar",
    Version = "0.3.0",
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
    public override void RegisterEventHandlers()
    {
        base.RegisterEventHandlers();
        Connection.Connected += (_, _) => HandlerManager.DiscoverHandlers(Assembly.GetExecutingAssembly());
    }
}
