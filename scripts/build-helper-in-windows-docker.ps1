[CmdletBinding()]
param(
  [string]$Image = "streamdock-sonar-helper-build:local",
  [string]$Configuration = "Release",
  [string]$Runtime = "win-x64",
  [string]$DockerExe
)

$ErrorActionPreference = "Stop"
$Root = (Resolve-Path (Join-Path $PSScriptRoot "..")).ProviderPath
$StagingRoot = Join-Path $env:TEMP "streamdock-sonar-build"
$WorkRoot = $StagingRoot

function Resolve-DockerExe {
  param([string]$Override)

  if ($Override) {
    return $Override
  }

  $cmd = Get-Command docker.exe -ErrorAction SilentlyContinue
  if ($cmd) {
    return $cmd.Source
  }

  $candidate = "C:\Program Files\Docker\Docker\resources\bin\docker.exe"
  if (Test-Path $candidate) {
    return $candidate
  }

  throw "docker.exe was not found. Pass -DockerExe."
}

function Invoke-Docker {
  & $ResolvedDockerExe @args
  if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
  }
}

function Copy-Tree {
  param(
    [string]$Source,
    [string]$Destination
  )

  New-Item -ItemType Directory -Force -Path $Destination | Out-Null
  robocopy $Source $Destination /MIR /XD .git dist node_modules bin obj /XF *.zip | Out-Host
  if ($LASTEXITCODE -gt 7) {
    exit $LASTEXITCODE
  }
}

function Copy-Sdk {
  param(
    [string]$Destination
  )

  $sdkRoot = Resolve-Path (Join-Path $Root "..\StreamDockSDK")
  New-Item -ItemType Directory -Force -Path $Destination | Out-Null
  robocopy $sdkRoot $Destination /MIR /XD .git bin obj /XF *.zip | Out-Host
  if ($LASTEXITCODE -gt 7) {
    exit $LASTEXITCODE
  }
}

$ResolvedDockerExe = Resolve-DockerExe $DockerExe

if (Test-Path $StagingRoot) {
  Remove-Item -Recurse -Force $StagingRoot
}
Copy-Tree $Root $StagingRoot
Copy-Sdk (Join-Path $StagingRoot "Sdk\StreamDockSDK")

Invoke-Docker build `
  --file (Join-Path $WorkRoot "Dockerfile.helper.windows") `
  --tag $Image `
  $WorkRoot

Invoke-Docker run --rm `
  --volume "${WorkRoot}:C:\work" `
  --workdir "C:\work" `
  $Image `
  "C:\Windows\System32\WindowsPowerShell\v1.0\powershell.exe" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "scripts\release.ps1" -Configuration $Configuration -Runtime $Runtime

$SourceDist = Join-Path $StagingRoot "dist"
$TargetDist = Join-Path $Root "dist"
New-Item -ItemType Directory -Force -Path $TargetDist | Out-Null
robocopy $SourceDist $TargetDist /MIR | Out-Host
if ($LASTEXITCODE -gt 7) {
  exit $LASTEXITCODE
}
