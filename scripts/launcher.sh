#!/usr/bin/env bash
# Launcher for amcd / agentctl published builds. Locates the .NET runtime (user installs
# in ~/.dotnet are not found by the apphost unless DOTNET_ROOT is set).
set -euo pipefail
self="$(basename "$0")"
base="${AMC_INSTALL_DIR:-$HOME/.local/share/agent-mission-control}"
if [[ -z "${DOTNET_ROOT:-}" ]]; then
  for d in "$HOME/.dotnet" /usr/share/dotnet /usr/lib/dotnet /usr/local/share/dotnet; do
    [[ -x "$d/dotnet" ]] && { export DOTNET_ROOT="$d"; break; }
  done
fi
case "$self" in
  amcd)     exec "$base/amcd/amcd" "$@" ;;
  agentctl|overhive) exec "$base/cli/agentctl" "$@" ;;
  *) echo "launcher: unknown name '$self' (link me as amcd, agentctl or overhive)" >&2; exit 2 ;;
esac
