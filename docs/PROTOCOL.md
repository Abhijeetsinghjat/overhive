# Overhive open protocol (v1)

Any AI coding tool, wrapper script or hook can tell Overhive what an agent is doing, and pick up
messages for it. Overhive never needs the tool's credentials: the tool signs in with the user's own
account as usual.

Transport: HTTP on the daemon's private Unix socket (no token needed; only your user can open it) or on
`http://127.0.0.1:47821` with the `X-Amc-Token` header (token in `~/.local/state/agent-mission-control/token`).
From a shell, `agentctl emit` and `agentctl inbox` do this for you.

## Report: `POST /api/v1/report`

```json
{
  "tool": "codex",
  "kind": "tool_start",
  "agent": { "id": "session-123", "parent": null, "title": "Fix flaky tests", "cwd": "/home/me/repo", "pid": 4242, "model": "gpt-5" },
  "data": { "id": "call-7", "name": "shell", "command": "npm test" },
  "ts": "2026-10-13T07:15:00Z"
}
```

* `tool`: lowercase id (`codex`, `gemini`, `grok`, `cursor`, `copilot`, or your own). Tools with a native adapter (Claude Code) use that instead.
* `agent.id`: stable id for the session or sub-agent, `[A-Za-z0-9._-]{1,80}`. `parent` makes it a sub-agent of another agent of the same tool. The first report creates the agent; later reports may repeat `agent` fields.
* `ts`: optional; defaults to now.

| `kind` | `data` |
|--------|--------|
| `start` | `{ "reason"? }` (also sets the working directory and worktree from `agent.cwd`) |
| `status` | `{ "status": "running" \| "waiting" \| "blocked" \| "paused", "reason"? }` |
| `prompt` | `{ "text" }` (a user prompt to the agent) |
| `output` | `{ "text" }` (agent's text output) |
| `tool_start` | `{ "id", "name", "summary"?, "path"?, "command"? }` (`command` marks a shell command) |
| `tool_end` | `{ "id", "name"?, "command"?, "exitCode"?, "error"?, "output"? }` (`error` or non-zero `exitCode` = failed) |
| `file` | `{ "path" (absolute), "change": "created" \| "modified" \| "deleted", "toolId"? }` |
| `usage` | `{ "id" (model response id), "model"?, "input", "output", "cacheRead"?, "cacheWrite"? }` (repeat with growing values; only the increase is counted) |
| `end` | `{ "status"?: "completed" \| "failed", "reason"? }` |

Everything reported is shown with the evidence label **reported**. File changes are also confirmed by Git
(**observed**), and changes made by shell commands are attributed to the only agent active in that
worktree (**inferred**).

Responses: `200 {"ok":true,"agentId":"codex:session-123"}` or `400 {"error":"…"}`.

## Messages: `POST /api/v1/inbox`

```json
{ "tool": "codex", "agent": "session-123", "wait": 30 }
```

Returns `200 {"messages":[{"id","text","sentAt"}]}` or `204` when there is nothing (after waiting up to
`wait` seconds, max 50). While a tool polls its inbox, the dashboard can message that agent; each message
is marked delivered when collected. A hook that can inject text into the agent's context (Codex,
Gemini CLI, Cursor and Copilot hooks can) should call this and pass the text on.

## Examples

```bash
# a session starts
agentctl emit start --tool codex --agent "$SESSION_ID" --title "Fix flaky tests" --cwd "$PWD"
# a hook reports a shell command
agentctl emit tool_start --tool codex --agent "$SESSION_ID" --data '{"id":"c1","name":"shell","command":"npm test"}'
agentctl emit tool_end   --tool codex --agent "$SESSION_ID" --data '{"id":"c1","command":"npm test","exitCode":1}'
# token usage
agentctl emit usage --tool codex --agent "$SESSION_ID" --data '{"id":"resp_1","model":"gpt-5","input":1200,"output":300}'
# hand pending dashboard messages to the agent (prints them, one per line)
agentctl inbox --tool codex --agent "$SESSION_ID" --wait 0
agentctl emit end --tool codex --agent "$SESSION_ID"
```

## Without any integration

Tools that report nothing still appear: Overhive recognises their running processes (configured in
`config.json` → `Tools`), and shows the process, working directory, worktree, file changes, commits and,
for sessions started from the dashboard, a live terminal you can type into.
