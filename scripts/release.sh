#!/usr/bin/env bash
set -euo pipefail

configuration="${CONFIGURATION:-Release}"
runtime="${RUNTIME:-win-x64}"

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

if [[ -d "Sdk/StreamDockSDK" ]]; then
  sdk_root="$(cd "Sdk/StreamDockSDK" && pwd)"
elif [[ -d "../StreamDockSDK" ]]; then
  sdk_root="$(cd "../StreamDockSDK" && pwd)"
else
  echo "StreamDockSDK was not found. Expected ../StreamDockSDK or Sdk/StreamDockSDK." >&2
  exit 1
fi

dotnet publish plugin-csharp/StreamDockSonar.csproj \
  -c "$configuration" \
  -r "$runtime" \
  --self-contained true \
  -p:EnableWindowsTargeting=true \
  -p:StreamDockSdkRoot="$sdk_root" \
  -o dist/plugin

npm run package

version="$(node -e "process.stdout.write(JSON.parse(require('fs').readFileSync('manifest.json','utf8')).Version)")"
release_dir="dist/release"
zip_path="$release_dir/streamdock-sonar-$version.zip"
staging_dir="$release_dir/streamdock-sonar-$version"
mkdir -p "$release_dir"
rm -f "$zip_path"
rm -rf "$staging_dir"
mkdir -p "$staging_dir"
cp -R dist/stream-dock-sonar.sdPlugin "$staging_dir/"
cp scripts/install-local.ps1 "$staging_dir/"

(
  cd "$staging_dir"
  zip -qr "$root/$zip_path" .
)
rm -rf "$staging_dir"
echo "Wrote $zip_path"
