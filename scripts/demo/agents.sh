#!/usr/bin/env bash
# Scripted demo agents (Codex, Gemini CLI, Copilot) that report through Overhive's open protocol
# and make real changes in the demo repository.  Usage: agents.sh setup | live | inbox
D="${OVERHIVE_DEMO_DIR:-/tmp/overhive-demo}"; SOCK="$D/state/overhive/amcd.sock"; R="$D/repos"
CO="$R/acme-shop-checkout"; FL="$R/acme-shop-flaky"; MAIN="$R/acme-shop"
post() { curl -sf --unix-socket "$SOCK" -H 'Content-Type: application/json' --data-binary "$2" "http://localhost$1" >/dev/null || echo "post failed: $2" >&2; }
emit() { post /api/v1/report "{\"tool\":\"$1\",\"agent\":$2,\"kind\":\"$3\",\"data\":${4:-{\}}}"; }
CX='{"id":"checkout","title":"Add checkout API","cwd":"'$CO'","model":"gpt-5-codex"}'
CXS='{"id":"checkout-tests","parent":"checkout","title":"Write checkout tests","cwd":"'$CO'","model":"gpt-5-codex"}'
GM='{"id":"flaky","title":"Fix flaky order tests","cwd":"'$FL'","model":"gemini-2.5-pro"}'
CP='{"id":"docs","title":"Document the API","cwd":"'$MAIN'","model":"gpt-5"}'
gitc() { git -C "$1" -c user.name="Demo Dev" -c user.email=demo@example.com "${@:2}" >/dev/null; }

phase_setup() {
  emit codex "$CX" start '{"reason":"session started"}'
  emit codex "$CX" prompt '{"text":"Add a POST /checkout endpoint that validates the cart and creates an order"}'
  emit gemini "$GM" start; emit gemini "$GM" prompt '{"text":"orders.test.ts fails about 1 in 5 runs. Find out why and fix it."}'
  emit copilot "$CP" start; emit copilot "$CP" prompt '{"text":"Write API docs for the orders module in the README"}'
  emit codex "$CX" usage '{"id":"r1","model":"gpt-5-codex","input":18400,"output":2100,"cacheRead":52000}'
  emit gemini "$GM" usage '{"id":"g1","model":"gemini-2.5-pro","input":24100,"output":1600}'
  emit copilot "$CP" usage '{"id":"p1","model":"gpt-5","input":9300,"output":800}'
  emit codex "$CX" tool_start '{"id":"c1","name":"read","summary":"src/api/orders.ts","path":"'$CO'/src/api/orders.ts"}'
  emit codex "$CX" tool_end '{"id":"c1"}'
  emit codex "$CX" tool_start '{"id":"c2","name":"apply_patch","summary":"Add checkout handler","path":"'$CO'/src/api/checkout.ts"}'
  cat > "$CO/src/api/checkout.ts" <<'TS'
import { createOrder, Order } from "./orders";

export interface CheckoutRequest { items: string[] }

export function checkout(req: CheckoutRequest, prices: Record<string, number>): Order {
  if (req.items.length === 0) throw new Error("cart is empty");
  const unknown = req.items.filter(i => !(i in prices));
  if (unknown.length) throw new Error(`unknown items: ${unknown.join(", ")}`);
  return createOrder(req.items, prices);
}
TS
  emit codex "$CX" file '{"path":"'$CO'/src/api/checkout.ts","change":"created","toolId":"c2"}'
  emit codex "$CX" tool_end '{"id":"c2"}'
  emit codex "$CX" tool_start '{"id":"c3","name":"apply_patch","summary":"Round totals to cents","path":"'$CO'/src/api/orders.ts"}'
  sed -i 's/  return { id: crypto.randomUUID(), items, total };/  return { id: crypto.randomUUID(), items, total: Math.round(total * 100) \/ 100 };/' "$CO/src/api/orders.ts"
  emit codex "$CX" file '{"path":"'$CO'/src/api/orders.ts","change":"modified","toolId":"c3"}'
  emit codex "$CX" tool_end '{"id":"c3"}'
  emit codex "$CX" output '{"text":"Added checkout() with validation for empty carts and unknown items. Next: tests."}'
  emit codex "$CXS" start '{"reason":"spawned by checkout"}'
  emit codex "$CXS" tool_start '{"id":"s1","name":"apply_patch","summary":"Add checkout tests","path":"'$CO'/tests/checkout.test.ts"}'
  printf 'import { checkout } from "../src/api/checkout";\n\ntest("rejects empty cart", () => {\n  expect(() => checkout({ items: [] }, {})).toThrow("cart is empty");\n});\n\ntest("creates an order", () => {\n  expect(checkout({ items: ["a", "b"] }, { a: 2, b: 3.5 }).total).toBe(5.5);\n});\n' > "$CO/tests/checkout.test.ts"
  emit codex "$CXS" file '{"path":"'$CO'/tests/checkout.test.ts","change":"created","toolId":"s1"}'
  emit codex "$CXS" tool_end '{"id":"s1"}'
  emit codex "$CXS" usage '{"id":"rs1","model":"gpt-5-codex","input":6200,"output":900}'
  emit gemini "$GM" tool_start '{"id":"g2","name":"shell","command":"npm test -- orders"}'
  emit gemini "$GM" tool_end '{"id":"g2","command":"npm test -- orders","exitCode":1,"output":"FAIL tests/orders.test.ts: expected 0.30000000000000004 to be 0.3"}'
  emit gemini "$GM" tool_start '{"id":"g3","name":"edit","summary":"Use integer cents","path":"'$FL'/src/api/orders.ts"}'
  sed -i 's/(sum, i) => sum + (prices\[i\] ?? 0), 0)/(sum, i) => sum + Math.round((prices[i] ?? 0) * 100), 0) \/ 100/' "$FL/src/api/orders.ts"
  emit gemini "$GM" file '{"path":"'$FL'/src/api/orders.ts","change":"modified","toolId":"g3"}'
  emit gemini "$GM" tool_end '{"id":"g3"}'
  emit copilot "$CP" tool_start '{"id":"p2","name":"edit","summary":"README: orders API","path":"'$MAIN'/README.md"}'
  printf '# Acme Shop\n\n## Orders API\n\n`createOrder(items, prices)` returns an order with a rounded total.\n' > "$MAIN/README.md"
  emit copilot "$CP" file '{"path":"'$MAIN'/README.md","change":"modified","toolId":"p2"}'
  emit copilot "$CP" tool_end '{"id":"p2"}'
  emit copilot "$CP" status '{"status":"waiting","reason":"Waiting for your review"}'
}

phase_live() {
  sleep 3
  emit codex "$CXS" tool_start '{"id":"s2","name":"shell","command":"npm test -- checkout"}'
  emit codex "$CXS" usage '{"id":"rs1","model":"gpt-5-codex","input":9800,"output":1500}'
  sleep 3
  emit codex "$CXS" tool_end '{"id":"s2","command":"npm test -- checkout","exitCode":0,"output":"PASS tests/checkout.test.ts (2 tests)"}'
  emit codex "$CXS" end '{"status":"completed"}'
  sleep 2
  emit gemini "$GM" tool_start '{"id":"g4","name":"shell","command":"npm test -- orders --runs 20"}'
  emit gemini "$GM" usage '{"id":"g1","model":"gemini-2.5-pro","input":31800,"output":2400}'
  sleep 3
  emit gemini "$GM" tool_end '{"id":"g4","command":"npm test -- orders --runs 20","exitCode":0,"output":"PASS 20/20 runs"}'
  emit gemini "$GM" output '{"text":"The flake was floating-point rounding in totals. Totals are now summed in integer cents; 20/20 runs pass."}'
  sleep 2
  emit codex "$CX" tool_start '{"id":"c4","name":"shell","command":"git commit -m \"Add checkout API\""}'
  gitc "$CO" add -A; gitc "$CO" commit -m "Add checkout API with cart validation"
  emit codex "$CX" tool_end '{"id":"c4","command":"git commit -m \"Add checkout API\"","exitCode":0}'
  emit codex "$CX" usage '{"id":"r1","model":"gpt-5-codex","input":26900,"output":3400,"cacheRead":61000}'
  emit codex "$CX" status '{"status":"waiting","reason":"Done. Waiting for instructions"}'
}

# Polls the protocol inbox like a tool hook would, and answers the first dashboard message.
phase_inbox() {
  for _ in $(seq 1 30); do
    msg=$(curl -s --unix-socket "$SOCK" -H 'Content-Type: application/json' --data-binary '{"tool":"codex","agent":"checkout","wait":5}' http://localhost/api/v1/inbox)
    if [[ "$msg" == *'"text"'* ]]; then
      emit codex "$CX" status '{"status":"running","reason":"Message from dashboard"}'
      sleep 2
      emit codex "$CX" output '{"text":"Sure: I will rebase on fix/flaky-tests first so orders.ts uses the integer-cents fix, then re-run the tests."}'
      emit codex "$CX" tool_start '{"id":"c5","name":"shell","command":"git rebase fix/flaky-tests"}'
      return
    fi
  done
}
"phase_$1"
