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
# Start on login via a systemd user unit (skip with AMC_NO_SYSTEMD=1).
if [[ -z "${AMC_NO_SYSTEMD:-}" ]] && command -v systemctl >/dev/null && systemctl --user is-system-running >/dev/null 2>&1; then
  mkdir -p "$HOME/.config/systemd/user"
  install -m 0644 "$repo/scripts/amcd.service" "$HOME/.config/systemd/user/amcd.service"
  systemctl --user daemon-reload
  systemctl --user enable amcd.service >/dev/null
  systemctl --user restart amcd.service   # picks up the freshly published binaries
  echo "Installed. amcd runs as a systemd user service (starts on login): systemctl --user status amcd"
else
  echo "Installed. Start the daemon with: amcd   (then open http://127.0.0.1:47821)"
fi
echo "Dashboard: http://127.0.0.1:47821"
