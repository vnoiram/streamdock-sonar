param(
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).ProviderPath
Set-Location $Root

$SdkRoot = if (Test-Path "Sdk/StreamDockSDK") {
  (Resolve-Path "Sdk/StreamDockSDK").ProviderPath
} elseif (Test-Path "..\StreamDockSDK") {
  (Resolve-Path "..\StreamDockSDK").ProviderPath
} else {
  throw "StreamDockSDK was not found. Expected ..\StreamDockSDK or Sdk\StreamDockSDK."
}

dotnet publish plugin-csharp/StreamDockSonar.csproj -c $Configuration -r $Runtime --self-contained true -p:EnableWindowsTargeting=true -p:StreamDockSdkRoot="$SdkRoot" -o dist/plugin
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
npm run package
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$Manifest = Get-Content "manifest.json" -Raw | ConvertFrom-Json
$ReleaseDir = Join-Path $Root "dist/release"
New-Item -ItemType Directory -Force -Path $ReleaseDir | Out-Null
$Zip = Join-Path $ReleaseDir "streamdock-sonar-$($Manifest.Version).zip"
if (Test-Path $Zip) { Remove-Item $Zip -Force }

$StagingDir = Join-Path $ReleaseDir "streamdock-sonar-$($Manifest.Version)"
if (Test-Path $StagingDir) { Remove-Item -Recurse -Force $StagingDir }
New-Item -ItemType Directory -Force -Path $StagingDir | Out-Null
Copy-Item -Recurse -Force "dist/stream-dock-sonar.sdPlugin" $StagingDir
Copy-Item -Force "scripts/install-local.ps1" $StagingDir

Compress-Archive -Path (Join-Path $StagingDir "*") -DestinationPath $Zip
Remove-Item -Recurse -Force $StagingDir

Write-Host "Wrote $Zip"
