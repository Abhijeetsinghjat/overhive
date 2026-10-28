#!/usr/bin/env bash
# Writes the winget manifests for a release from its SHA256SUMS (prepared only; nothing is submitted):
#   packaging/winget/winget-manifest.sh 0.3.0 dist/SHA256SUMS out/
# writes out/manifests/a/Abhijeetsinghjat/Overhive/0.3.0/*.yaml, the layout of microsoft/winget-pkgs.
# The release workflow runs this and keeps the result as the "winget-manifests" workflow artifact.
#
# Publishing is manual (the owner's decision; Overhive never opens the pull request itself):
#   1. Download the artifact (or run this script on the release's SHA256SUMS).
#   2. Check it locally on Windows: winget validate --manifest <dir>  and  winget install --manifest <dir>
#      (needs `winget settings --enable LocalManifestFiles` once, as admin).
#   3. Fork microsoft/winget-pkgs, copy manifests/ into the fork, open a pull request and follow its checklist;
#      or use `wingetcreate submit <dir>` with your own GitHub token.
#   The first submission is reviewed by winget moderators; later versions are usually merged automatically.
set -euo pipefail
version="$1"; sums="$2"; out="$3"
zip="overhive-$version-win-x64.zip"
hash="$(awk -v f="$zip" '$2 == f || $2 == "*" f { print toupper($1) }' "$sums")"
[[ -n "$hash" ]] || { echo "missing $zip in $sums" >&2; exit 1; }
here="$(cd "$(dirname "$0")" && pwd)"
dir="$out/manifests/a/Abhijeetsinghjat/Overhive/$version"
mkdir -p "$dir"
for t in "$here"/*.yaml.in; do
  f="$dir/$(basename "${t%.in}")"
  sed -e "s/__VERSION__/$version/g" -e "s/__SHA256__/$hash/g" -e "s/__DATE__/$(date -u +%Y-%m-%d)/g" "$t" > "$f"
  echo "wrote $f" >&2
done
