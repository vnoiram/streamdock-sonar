using StreamDockSonar;

Directory.SetCurrentDirectory(AppContext.BaseDirectory);

try
{
    using var plugin = new SonarPlugin();
    await plugin.RunAsync(args);
}
catch (Exception ex)
{
    var logPath = Path.Combine(AppContext.BaseDirectory, "startup-error.log");
    await File.AppendAllTextAsync(logPath, $"[{DateTimeOffset.Now:O}] {ex}\n");
    throw;
}
