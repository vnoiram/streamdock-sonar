#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$script_dir/.." && pwd)"
workspace="$(cd "$root/.." && pwd)"
image="${STREAMDOCK_SONAR_RELEASE_IMAGE:-streamdock-sonar-release-build:local}"
configuration="${CONFIGURATION:-Release}"
runtime="${RUNTIME:-win-x64}"

docker build -f "$root/Dockerfile.release.linux" -t "$image" "$root"
docker run --rm \
  -e CONFIGURATION="$configuration" \
  -e RUNTIME="$runtime" \
  -v "$workspace:/work" \
  -w /work/streamdock-sonar \
  "$image" \
  bash scripts/release.sh
