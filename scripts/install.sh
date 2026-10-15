#!/usr/bin/env bash
# Builds release binaries into ~/.local/share/agent-mission-control and links
# `amcd` and `agentctl` into ~/.local/bin. Does not touch Claude Code settings
# (use `agentctl hooks install` for that, separately and explicitly).
set -euo pipefail
repo="$(cd "$(dirname "$0")/.." && pwd)"
base="${AMC_INSTALL_DIR:-$HOME/.local/share/agent-mission-control}"
dotnet publish "$repo/src/Amc.Daemon" -c Release -o "$base/amcd" --nologo -v q
dotnet publish "$repo/src/Amc.Cli" -c Release -o "$base/cli" --nologo -v q
install -m 0755 "$repo/scripts/launcher.sh" "$base/launcher.sh"
mkdir -p "$HOME/.local/bin"
ln -sf "$base/launcher.sh" "$HOME/.local/bin/amcd"
ln -sf "$base/launcher.sh" "$HOME/.local/bin/agentctl"
ln -sf "$base/launcher.sh" "$HOME/.local/bin/overhive"   # friendly alias for agentctl
# Start at login: systemd user unit on Linux, launchd agent on macOS.
source "$repo/scripts/service.sh"
install_service "$repo/scripts"
echo "Dashboard: http://127.0.0.1:47821"
