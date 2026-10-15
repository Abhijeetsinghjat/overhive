#!/usr/bin/env bash
# Installs a release archive into a throwaway home, starts the daemon on a spare port and checks that the
# API answers and the CLI runs. Used by CI on Linux and macOS; never touches the real user's install.
#   scripts/smoke-release.sh dist/overhive-<version>-<rid>.tar.gz
set -euo pipefail
archive="$(cd "$(dirname "$1")" && pwd)/$(basename "$1")"
tmp="$(mktemp -d)"; trap 'kill "${pid:-0}" 2>/dev/null || true; rm -rf "$tmp"' EXIT
mkdir -p "$tmp/home"
tar -xzf "$archive" -C "$tmp"
dir="$(find "$tmp" -maxdepth 1 -type d -name 'overhive-*' | head -1)"
HOME="$tmp/home" AMC_NO_SYSTEMD=1 AMC_NO_LAUNCHD=1 "$dir/install-release.sh"
bin="$tmp/home/.local/bin"
"$bin/overhive" version
port=47999
HOME="$tmp/home" AMC_Port=$port AMC_WorkspaceRoots__0="$tmp" "$bin/amcd" > "$tmp/amcd.log" 2>&1 &
pid=$!
for _ in $(seq 1 60); do curl -sf -o /dev/null "http://127.0.0.1:$port/" && break; sleep 0.5; done
sock="$tmp/home/.local/state/agent-mission-control/amcd.sock"
health="$(curl -sf --unix-socket "$sock" http://localhost/api/health)" || { cat "$tmp/amcd.log"; exit 1; }
echo "$health" | head -c 300; echo
echo "$health" | grep -q '"version"' || { echo "unexpected health response"; exit 1; }
HOME="$tmp/home" AMC_Port=$port "$bin/overhive" status
echo "smoke test passed"
