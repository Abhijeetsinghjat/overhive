#!/usr/bin/env bash
# Sourced by install.sh and install-release.sh: makes the daemon start at login.
# Linux: systemd user unit (skip with AMC_NO_SYSTEMD=1). macOS: launchd agent (skip with AMC_NO_LAUNCHD=1).
# Usage: install_service <directory containing amcd.service and dev.overhive.amcd.plist>
install_service() {
  local src="$1"
  if [[ "$(uname -s)" == Darwin ]]; then
    if [[ -n "${AMC_NO_LAUNCHD:-}" ]]; then echo "Installed. Start the daemon with: amcd"; return; fi
    local plist="$HOME/Library/LaunchAgents/dev.overhive.amcd.plist" domain="gui/$(id -u)"
    mkdir -p "$HOME/Library/LaunchAgents" "$HOME/Library/Logs/Overhive"
    sed "s#__HOME__#$HOME#g" "$src/dev.overhive.amcd.plist" > "$plist"
    chmod 0644 "$plist"
    launchctl bootout "$domain/dev.overhive.amcd" 2>/dev/null || true
    launchctl bootstrap "$domain" "$plist"
    echo "Installed and started (launchd agent, starts at login). Logs: ~/Library/Logs/Overhive/amcd.log"
  elif [[ -z "${AMC_NO_SYSTEMD:-}" ]] && command -v systemctl >/dev/null && systemctl --user is-system-running >/dev/null 2>&1; then
    mkdir -p "$HOME/.config/systemd/user"
    install -m 0644 "$src/amcd.service" "$HOME/.config/systemd/user/amcd.service"
    systemctl --user daemon-reload
    systemctl --user enable amcd.service >/dev/null
    systemctl --user restart amcd.service   # picks up the new binaries
    echo "Installed and started (systemd user service, starts on login): systemctl --user status amcd"
  else
    echo "Installed. Start the daemon with: amcd"
  fi
  case ":$PATH:" in *":$HOME/.local/bin:"*) ;; *) echo "Note: add ~/.local/bin to your PATH to use the overhive command." ;; esac
}
