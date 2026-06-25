param(
  [string]$ExePath = "$(Resolve-Path "$PSScriptRoot/../dist/helper/SonarAudioHelper.exe")"
)

$ErrorActionPreference = "Stop"
$Startup = [Environment]::GetFolderPath("Startup")
$ShortcutPath = Join-Path $Startup "StreamDock Sonar Audio Helper.lnk"
$Shell = New-Object -ComObject WScript.Shell
$Shortcut = $Shell.CreateShortcut($ShortcutPath)
$Shortcut.TargetPath = $ExePath
$Shortcut.WorkingDirectory = Split-Path $ExePath
$Shortcut.Save()
Write-Host "Installed startup shortcut: $ShortcutPath"
