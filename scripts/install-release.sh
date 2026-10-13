#!/usr/bin/env bash
# Installs an unpacked release archive for the current user (no root, no .NET needed):
# copies it to ~/.local/share/agent-mission-control, links amcd + agentctl into ~/.local/bin and
# enables the systemd user service (skip with AMC_NO_SYSTEMD=1).
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
if [[ -z "${AMC_NO_SYSTEMD:-}" ]] && command -v systemctl >/dev/null && systemctl --user is-system-running >/dev/null 2>&1; then
  mkdir -p "$HOME/.config/systemd/user"
  install -m 0644 "$src/amcd.service" "$HOME/.config/systemd/user/amcd.service"
  systemctl --user daemon-reload && systemctl --user enable amcd.service >/dev/null && systemctl --user restart amcd.service
  echo "Installed and started (systemd user service). Dashboard: http://127.0.0.1:47821"
else
  echo "Installed. Start with: amcd   then open http://127.0.0.1:47821"
fi
