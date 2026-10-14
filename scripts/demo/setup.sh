#!/usr/bin/env bash
# Starts an isolated Overhive (own port, data, home and config) with a sample repository, then seeds
# scripted agents. Nothing from your real sessions or repositories is visible to it.
#   scripts/demo/setup.sh          fresh demo state at http://127.0.0.1:47900
#   scripts/demo/setup.sh stop     stop it and delete the demo directory
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
D="${OVERHIVE_DEMO_DIR:-/tmp/overhive-demo}"
PORT="${OVERHIVE_DEMO_PORT:-47900}"
AMCD="${AMCD:-$HOME/.local/share/agent-mission-control/amcd/amcd}"

stop() { local p; p=$(ss -ltnp 2>/dev/null | grep ":$PORT " | grep -oP 'pid=\K[0-9]+' | head -1 || true); [ -n "$p" ] && kill "$p" && sleep 1 || true; }
if [ "${1:-}" = stop ]; then stop; rm -rf "$D"; exit 0; fi

stop
rm -rf "$D"; mkdir -p "$D/repos" "$D/home/.claude/sessions" "$D/home/.claude/projects" "$D/state" "$D/config"
export GIT_AUTHOR_DATE="2026-10-01T09:00:00Z" GIT_COMMITTER_DATE="2026-10-01T09:00:00Z"
g() { git -c user.name="Demo Dev" -c user.email=demo@example.com "$@"; }
r="$D/repos/acme-shop"; g init -q -b main "$r"
mkdir -p "$r/src/api" "$r/src/web" "$r/tests"
cat > "$r/src/api/orders.ts" <<'TS'
export interface Order { id: string; items: string[]; total: number }

export function createOrder(items: string[], prices: Record<string, number>): Order {
  const total = items.reduce((sum, i) => sum + (prices[i] ?? 0), 0);
  return { id: crypto.randomUUID(), items, total };
}
TS
printf 'export function Cart() {\n  return <div className="cart" />;\n}\n' > "$r/src/web/cart.tsx"
printf 'import { createOrder } from "../src/api/orders";\n\ntest("total", () => {\n  expect(createOrder(["a"], { a: 2 }).total).toBe(2);\n});\n' > "$r/tests/orders.test.ts"
printf '# Acme Shop\n' > "$r/README.md"
g -C "$r" add -A; g -C "$r" commit -qm "Initial storefront"
g -C "$r" remote add origin https://github.com/acme/acme-shop.git
g -C "$r" worktree add -q -b feat/checkout ../acme-shop-checkout
g -C "$r" worktree add -q -b fix/flaky-tests ../acme-shop-flaky
unset GIT_AUTHOR_DATE GIT_COMMITTER_DATE

dotnet_root=""; command -v dotnet >/dev/null && dotnet_root="$(dirname "$(readlink -f "$(command -v dotnet)")")"
( export HOME="$D/home" XDG_STATE_HOME="$D/state" XDG_CONFIG_HOME="$D/config"
  [ -n "$dotnet_root" ] && export DOTNET_ROOT="$dotnet_root"
  export AMC_Port="$PORT" AMC_DataDir="$D/state/overhive" AMC_ClaudeHome="$D/home/.claude" AMC_WorkspaceRoots__0="$D/repos" AMC_BackfillHours=0
  nohup "$AMCD" > "$D/amcd.log" 2>&1 & )
for _ in $(seq 1 40); do curl -s -o /dev/null "http://127.0.0.1:$PORT/" && break; sleep 0.25; done
OVERHIVE_DEMO_DIR="$D" "$here/agents.sh" setup
echo "Demo running at http://127.0.0.1:$PORT"
