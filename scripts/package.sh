#!/usr/bin/env bash
# Usage: AMC_RELEASE=1.0.0 AMC_RIDS="linux-x64 linux-arm64" scripts/package.sh   (on macOS: AMC_RIDS="osx-arm64 osx-x64",
#        on Windows in Git Bash: AMC_RIDS=win-x64)
# Builds self-contained release archives (no .NET install needed on the target machine):
#   dist/<name>-<version>-<rid>.tar.gz, or .zip for Windows. Build macOS archives on macOS: the SDK signs the
#   binaries only there, and Apple Silicon refuses to run unsigned ones.
# Each archive holds amcd/, cli/, launcher.sh, install-release.sh, service.sh, the systemd unit, the launchd agent, LICENSE and docs.
# A Windows zip holds amcd/, cli/, install-release.ps1, overhive-windows.ps1, LICENSE and docs instead.
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
# Note: MSBuild turns environment variables into properties, so never use VERSION/VERSIONPREFIX here.
version="${AMC_RELEASE:-$(git -C "$repo" describe --tags --always --dirty 2>/dev/null || echo dev)}"
name="${PRODUCT_ID:-overhive}"
rids="${AMC_RIDS:-linux-x64 linux-arm64}"
# Stamp the binaries with the release version when it is a plain x.y.z (what `overhive version` prints).
props=(); [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+$ ]] && props=("-p:Version=$version")
mkdir -p "$repo/dist"
# zip_dir <parent> <folder> <out.zip>: a zip with <folder>/ at the top, like the tarballs. PowerShell on Windows
# (Git Bash), else Python (Linux/macOS, for cross-builds).
zip_dir() {
  local parent="$1" folder="$2" zipfile="$3" ps=powershell
  rm -f "$zipfile"
  if command -v cygpath >/dev/null; then
    command -v pwsh >/dev/null && ps=pwsh
    parent="$(cygpath -w "$parent")"; zipfile="$(cygpath -w "$zipfile")"
    "$ps" -NoProfile -Command "Add-Type -AssemblyName System.IO.Compression.FileSystem; [IO.Compression.ZipFile]::CreateFromDirectory('$parent\\$folder', '$zipfile', 'Optimal', \$true)"
  else
    python3 -c 'import os, sys, zipfile
parent, folder, out = sys.argv[1:]
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(os.path.join(parent, folder)):
        for f in files:
            p = os.path.join(root, f)
            z.write(p, os.path.relpath(p, parent))' "$parent" "$folder" "$zipfile"
  fi
}
for rid in $rids; do
  out="$repo/dist/stage/$name-$version-$rid"
  rm -rf "$out"; mkdir -p "$out"
  for proj in Amc.Daemon:amcd Amc.Cli:cli; do
    dotnet publish "$repo/src/${proj%%:*}" -c Release -r "$rid" --self-contained true \
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none "${props[@]}" \
      -o "$out/${proj##*:}" > "$repo/dist/build-$rid.log" 2>&1 || { grep -E "error" "$repo/dist/build-$rid.log" | head -5; exit 1; }
  done
  [ -f "$repo/NOTICE" ] && install -m 0644 "$repo/NOTICE" "$out/"
  if [[ "$rid" == win-* ]]; then
    # Signing hook (disabled): free code signing for open-source projects is available from SignPath Foundation
    # once the owner has been accepted. Until then the .exe files are unsigned and SmartScreen may warn.
    # [[ -n "${AMC_SIGN_CMD:-}" ]] && $AMC_SIGN_CMD "$out/amcd/amcd.exe" "$out/cli/agentctl.exe"
    install -m 0644 "$repo/scripts/install-release.ps1" "$repo/scripts/overhive-windows.ps1" "$repo/LICENSE" "$repo/README.md" "$out/"
    zip_dir "$repo/dist/stage" "$(basename "$out")" "$repo/dist/$name-$version-$rid.zip"
    echo "built dist/$name-$version-$rid.zip"
    continue
  fi
  install -m 0755 "$repo/scripts/launcher.sh" "$repo/scripts/install-release.sh" "$out/"
  install -m 0644 "$repo/scripts/service.sh" "$repo/scripts/amcd.service" "$repo/scripts/dev.overhive.amcd.plist" "$repo/LICENSE" "$repo/README.md" "$out/"
  tar -C "$repo/dist/stage" -czf "$repo/dist/$name-$version-$rid.tar.gz" "$(basename "$out")"
  echo "built dist/$name-$version-$rid.tar.gz"
done
rm -rf "$repo/dist/stage"
