#!/usr/bin/env bash
# Usage: AMC_RELEASE=1.0.0 AMC_RIDS="linux-x64 linux-arm64" scripts/package.sh   (on macOS: AMC_RIDS="osx-arm64 osx-x64")
# Builds self-contained release archives (no .NET install needed on the target machine):
#   dist/<name>-<version>-<rid>.tar.gz. Build macOS archives on macOS: the SDK signs the binaries only there,
#   and Apple Silicon refuses to run unsigned ones.
# Each archive holds amcd/, cli/, launcher.sh, install-release.sh, service.sh, the systemd unit, the launchd agent, LICENSE and docs.
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
# Note: MSBuild turns environment variables into properties, so never use VERSION/VERSIONPREFIX here.
version="${AMC_RELEASE:-$(git -C "$repo" describe --tags --always --dirty 2>/dev/null || echo dev)}"
name="${PRODUCT_ID:-overhive}"
rids="${AMC_RIDS:-linux-x64 linux-arm64}"
# Stamp the binaries with the release version when it is a plain x.y.z (what `overhive version` prints).
props=(); [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] && props=("-p:Version=$version")
mkdir -p "$repo/dist"
for rid in $rids; do
  out="$repo/dist/stage/$name-$version-$rid"
  rm -rf "$out"; mkdir -p "$out"
  for proj in Amc.Daemon:amcd Amc.Cli:cli; do
    dotnet publish "$repo/src/${proj%%:*}" -c Release -r "$rid" --self-contained true \
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none "${props[@]}" \
      -o "$out/${proj##*:}" > "$repo/dist/build-$rid.log" 2>&1 || { grep -E "error" "$repo/dist/build-$rid.log" | head -5; exit 1; }
  done
  install -m 0755 "$repo/scripts/launcher.sh" "$repo/scripts/install-release.sh" "$out/"
  install -m 0644 "$repo/scripts/service.sh" "$repo/scripts/amcd.service" "$repo/scripts/dev.overhive.amcd.plist" "$repo/LICENSE" "$repo/README.md" "$out/"
  [ -f "$repo/NOTICE" ] && install -m 0644 "$repo/NOTICE" "$out/"
  tar -C "$repo/dist/stage" -czf "$repo/dist/$name-$version-$rid.tar.gz" "$(basename "$out")"
  echo "built dist/$name-$version-$rid.tar.gz"
done
rm -rf "$repo/dist/stage"
