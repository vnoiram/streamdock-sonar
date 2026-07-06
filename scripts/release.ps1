param(
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
$Root = Resolve-Path (Join-Path $PSScriptRoot "..")
Set-Location $Root

dotnet publish helper/SonarAudioHelper.csproj -c $Configuration -r $Runtime --self-contained false -o dist/helper
npm run package

$Manifest = Get-Content "manifest.json" -Raw | ConvertFrom-Json
$ReleaseDir = Join-Path $Root "dist/release"
New-Item -ItemType Directory -Force -Path $ReleaseDir | Out-Null
$Zip = Join-Path $ReleaseDir "streamdock-sonar-$($Manifest.Version).zip"
if (Test-Path $Zip) { Remove-Item $Zip -Force }

Compress-Archive -Path @(
  "dist/stream-dock-sonar.sdPlugin",
  "scripts/install-local.ps1"
) -DestinationPath $Zip

Write-Host "Wrote $Zip"
