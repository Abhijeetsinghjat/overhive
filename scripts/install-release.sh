#!/usr/bin/env bash
# Installs an unpacked release archive for the current user (no root, no .NET needed):
# copies it to ~/.local/share/agent-mission-control, links amcd + agentctl into ~/.local/bin and
# starts the daemon at login: systemd user service on Linux (skip with AMC_NO_SYSTEMD=1), launchd agent
# on macOS (skip with AMC_NO_LAUNCHD=1).
set -euo pipefail
src="$(cd "$(dirname "$0")" && pwd)"
base="${AMC_INSTALL_DIR:-$HOME/.local/share/agent-mission-control}"
mkdir -p "$base" "$HOME/.local/bin"
rm -rf "$base/amcd" "$base/cli"
cp -r "$src/amcd" "$src/cli" "$base/"
install -m 0755 "$src/launcher.sh" "$base/launcher.sh"
ln -sf "$base/launcher.sh" "$HOME/.local/bin/amcd"
ln -sf "$base/launcher.sh" "$HOME/.local/bin/agentctl"
ln -sf "$base/launcher.sh" "$HOME/.local/bin/overhive"   # friendly alias for agentctl
# shellcheck source=service.sh
source "$src/service.sh"
install_service "$src"
echo "Dashboard: http://127.0.0.1:47821"
