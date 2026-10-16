# Overhive — Architecture

Local, self-hosted control center for AI coding agents. No SaaS, no external
services. One daemon (`amcd`), one CLI (`agentctl`), one browser UI served by the
daemon. Everything binds to `127.0.0.1`.

## 1. What can we reliably know about an AI coding agent?

Every fact the system displays carries an **evidence level**:

| Evidence   | Meaning                                                                 |
|------------|-------------------------------------------------------------------------|
| `observed` | Seen directly by the OS, Git, or the filesystem.                        |
| `reported` | Stated by the agent itself (hook payload, transcript record, tool input). |
| `inferred` | Derived by correlation (time window + path + single active agent).      |

| Fact                               | Source                                    | Evidence  | Reliability |
|------------------------------------|-------------------------------------------|-----------|-------------|
| **A. OS-observable** | | | |
| Process alive / exited             | `/proc/<pid>` + `procStart` from registry | observed  | high |
| PID, PPID, cmdline, cwd, CPU, RSS  | `/proc/<pid>/{stat,cmdline,cwd,status}`   | observed  | high |
| Child commands (bash → dotnet test)| `/proc` parent chain                       | observed  | high |
| Terminal (pts) the agent runs in   | `/proc/<pid>/fd/0`                        | observed  | high |
| **B. Git-observable** | | | |
| Repos, worktrees, branch, HEAD     | `git worktree list --porcelain`           | observed  | high |
| Dirty files, +/- lines             | `git status --porcelain=v2`, `git diff --numstat` | observed | high |
| Commits, branch switches           | HEAD/ref changes + `git log old..new`     | observed  | high |
| Merge/rebase conflicts             | unmerged entries, `MERGE_HEAD`/`rebase-merge` | observed | high |
| **C. Filesystem-observable** | | | |
| A file changed (that it changed)   | git status refresh after triggers         | observed  | high |
| *Who* changed it                   | — (filesystem has no actor)               | —         | **not observable** |
| **D. Terminal-observable** | | | |
| Raw terminal output                | only for sessions launched via `agentctl run` (we own the PTY) | observed | high |
| **E. Agent-hook-observable** (Claude Code hooks) | | | |
| Session start/end, prompts         | `SessionStart`, `SessionEnd`, `UserPromptSubmit` | reported | high |
| Each tool call + input + result    | `PreToolUse`, `PostToolUse`               | reported  | high |
| Sub-agent start/stop               | `SubagentStart`, `SubagentStop`           | reported  | high |
| Waiting for permission / input     | `Notification`                            | reported  | high |
| **F. Provider-specific (Claude Code local files)** | | | |
| Live sessions, `busy`/`idle`       | `~/.claude/sessions/<pid>.json`           | reported  | high, undocumented format |
| Full history, assistant replies    | `~/.claude/projects/*/<session>.jsonl`    | reported  | high, undocumented format |
| Sub-agent tree                     | `<session>/subagents/agent-*.{jsonl,meta.json}` (`toolUseId` → parent `Agent` call) | reported | high |
| Task list / progress               | `TodoWrite` / task tool inputs            | reported  | depends on the agent using them |
| **G. Inferred** | | | |
| Worktree an agent works in         | absolute paths in tool inputs → longest-prefix worktree root | inferred | medium–high |
| File change made via Bash          | git delta during a Bash call, single active agent in worktree | inferred | medium |
| Agent's reply to a user message    | first assistant text after delivery       | inferred  | medium |
| Test pass/fail counts              | parse known runner summaries              | inferred  | medium |
| **Not observable** | | | |
| Agent reasoning / intent           | not exposed reliably → never displayed as fact | — | — |

Key discovery findings that shape the design:

* An agent's `cwd` does **not** identify its worktree. Sub-agents observed on this
  machine report `cwd=~/dev/acme-backend` but operate on
  `/tmp/.../scratchpad/wt-trackC/...` through absolute paths. Attribution must use
  tool-input paths.
* Recursive inotify on worktrees is infeasible (`max_user_watches=65536`; nine
  worktrees with `node_modules` exceed it). We watch only Git metadata and refresh
  status on triggers.
* Text cannot be injected into a terminal we did not create (TIOCSTI is disabled).
  Messaging therefore uses explicit transports (§8).

## 2. System architecture

```
 Claude Code sessions ──hooks (curl POST)──┐
 ~/.claude/sessions/*.json ── registry ────┤
 ~/.claude/projects/**/*.jsonl ── tailer ──┤       ┌──────────────────────── amcd ─────────────────────────┐
 /proc ── process collector ───────────────┼─────► │ Collectors ─► Ingest channel ─► Normalizer/dedupe ──┐ │
 .git metadata + git CLI ── git collector ─┤       │   (bounded, drop-counted)                            │ │
 agentctl run (PTY host) ── pty bridge ────┘       │                                                      ▼ │
                                                   │  SQLite event log (append-only, seq) ◄── batch writer  │
                                                   │            │                                          │
                                                   │            ▼                                          │
                                                   │  Reducer (pure, Amc.Core) ─► WorldState (in memory)  │
                                                   │            │                         ▲ snapshots      │
                                                   │            ▼                         │ in SQLite      │
                                                   │  Broadcaster ─► WebSocket clients (replay by seq)     │
                                                   │  REST API  ◄─── UI, agentctl                          │
                                                   │  Control/Message services ─► providers ─► audit events│
                                                   └───────────────────────────────────────────────────────┘
```

**Single source of truth:** the append-only event log. `WorldState` is a pure fold
of events (`Reducer.Apply(state, event)` in `Amc.Core`), so state after a restart
equals state before it. Snapshots of `WorldState` are stored every N events to keep
startup fast; startup = load latest snapshot + replay the tail. Snapshots carry `Reducer.Version`;
a snapshot from another version is discarded and the full log is replayed, so changes
to the fold semantics apply to all of history.

**Time-based rules** that no event triggers (a sub-agent silent for 30 min with no
completion recorded becomes `unknown`) are applied by `StalenessMonitor`, which
publishes ordinary, explicitly *inferred* status events. The reducer stays a pure fold.

**Volatile telemetry** (CPU %, RSS, PTY bytes) is *not* written to the event log.
It lives in a bounded in-memory overlay and is pushed to clients at ≤1 Hz.

## 3. Projects

```
src/Amc.Core     domain model, event types, reducer, ownership/conflict logic,
                 path → worktree resolution, test-output parsing, redaction.
                 No IO. Fully unit-testable.
src/Amc.Daemon   ASP.NET Core host: SQLite store, ingest pipeline, collectors,
                 Claude provider, REST + WebSocket, static UI (wwwroot/).
src/Amc.Cli      agentctl: thin HTTP client to the daemon + `run` (PTY host).
tests/Amc.Tests  unit / integration / e2e (xUnit; categories via traits).
```

## 4. Domain model (Amc.Core/Domain)

| Concept            | Identity                              | Notes |
|--------------------|---------------------------------------|-------|
| `Agent`            | `claude:<sessionId>` (main) / `claude:<sessionId>:<agentId>` (sub) | One node in the hierarchy. `ParentId`, `Kind` (main/sub), `AgentType`, `Title`, `Status`, `StatusReason`, timestamps, `Current*` fields, `Provider`. Arbitrary depth. |
| `AgentSession`     | provider session id                   | Process-lifetime of a main agent; owns `ProcessId`, `TranscriptPath`, `Transports`. |
| `AgentProcess`     | `pid:<pid>:<procStart>`               | PID reuse-safe identity. Agent ↔ process link is observed (registry). |
| `AgentRelationship`| parent → child edge                   | Captured from Agent tool call ids (reported). |
| `AgentStatus`      | enum                                  | created, starting, running, waiting, blocked, paused, completed, failed, cancelled, disconnected, unknown |
| `AgentTask`        | `<agentId>#<todoKey>`                 | From TodoWrite/task tools; sub-agent description is the sub-agent's root task. |
| `ToolCall`         | provider tool_use id                  | name, input summary, status, duration, error. |
| `CommandExecution` | tool_use id of a Bash call            | command, cwd, exit code, bounded stdout/stderr. |
| `TestRun`          | tool_use id                           | runner, passed/failed/skipped counts (inferred from output). |
| `Repository`       | `repo:<sha1(common git dir)>`         | Shared by all its worktrees. |
| `Worktree`         | `wt:<sha1(worktree root)>`            | root, branch, HEAD, detached, dirty counts, lifecycle, owners. |
| `Branch`/`Commit`  | name / sha                            | Commits observed; author agent inferred. |
| `FileChange`       | `(worktreeId, relPath)`               | status (A/M/D/R/U), +/-, first/last change, contributing agents with evidence. |
| `GitOperation`     | event                                 | checkout, commit, merge, rebase, conflict. |
| `TerminalSession`  | PTY id                                | Only for `agentctl run` sessions. Ring buffer on disk. |
| `Message`          | ulid                                  | direction, transport, status (queued → delivered → answered / failed / expired). |
| `Conflict`         | `(kind, key)`                         | same-file / same-worktree / cross-worktree same-path / git-unmerged. |

## 5. Agent lifecycle (state machine)

```
created ─► starting ─► running ◄──► waiting (idle at prompt)
                         │  ▲          │
                         │  └─────── blocked (permission/notification)
                         ├─► paused (SIGSTOP by us) ─► running (SIGCONT)
                         ├─► completed (SessionEnd / SubagentStop / handback)
                         ├─► failed (tool error ends agent / non-zero exit)
                         ├─► cancelled (user stop via us)
                         └─► disconnected (process vanished without end event)
unknown: historical agent with no live evidence (e.g. backfilled transcript whose process is gone and no end event).
```

Rules (enforced in `AgentStateMachine`, unit-tested):
* `running` requires live evidence: process alive **and** (registry busy **or** a tool event in the last N seconds).
* Process gone + no end event ⇒ `disconnected` with reason "Process exited unexpectedly". Its running sub-agents become `disconnected` too.
* Terminal states (completed/failed/cancelled) only reopen on new activity from the same session (e.g. `--resume`).

## 6. Event model

See `EVENT_MODEL.md` for the full catalogue. Envelope:

```json
{ "seq": 1042, "id": "01J...", "ts": "2026-10-07T11:21:03.120Z",
  "type": "file.modified", "source": "hook", "evidence": "reported",
  "agentId": "claude:c441…:a319…", "sessionId": "c441…",
  "repositoryId": "repo:…", "worktreeId": "wt:…", "filePath": "src/X.cs",
  "dedupeKey": "tool:toolu_01…:post", "data": { … } }
```

* `seq` — global monotonic, assigned by SQLite; the replay cursor for clients.
* `dedupeKey` — UNIQUE. Hooks and transcript tailing both report the same tool call;
  whichever arrives first wins, the other is ignored.
* Payloads are **redacted before persistence** (`Redactor`) and bounded (16 KiB per
  text field).

## 7. Collectors

| Collector        | Mechanism (event-driven first)                                          |
|------------------|-------------------------------------------------------------------------|
| Claude registry  | inotify on `~/.claude/sessions` (non-recursive) + 5 s reconcile |
| Transcript tailer| inotify on `~/.claude/projects` + byte offsets persisted; backfill window configurable (default 24 h) |
| Hook receiver    | `POST /hooks/claude` (token-authenticated); also the mailbox delivery point |
| Process          | `/proc` scan every 2 s, limited to agent pids + descendants             |
| Git / worktree   | One inotify instance (P/Invoke; `FileSystemWatcher` costs one instance per watcher and the per-user limit is 128) with non-recursive watches on each git dir (`HEAD`, `index`, `refs`, `worktrees/`); status refresh debounced 750 ms per worktree on triggers (tool event touching it, git dir change); safety rescan 30 s (active) / 5 min (idle). All git calls run with `GIT_OPTIONAL_LOCKS=0` so we never take `index.lock` from under an agent. |
| PTY bridge       | `agentctl run` streams PTY output to the daemon over a local WebSocket; ring buffer on disk |

Repository discovery: configured roots (default `~/dev`, depth 3) at startup, plus
any absolute path seen in agent activity → `git rev-parse --git-common-dir` → the
repo and **all** its worktrees. No manual registration.

## 8. Agent integration & messaging

`IAgentProvider` (Amc.Core) — one per agent implementation:

```csharp
interface IAgentProvider {
    string Id { get; }
    AgentCapabilities Capabilities(Agent agent);           // what control/messaging is possible
    Task<ControlResult> ControlAsync(Agent agent, ControlAction action, CancellationToken ct);
    Task<DeliveryResult> SendAsync(Agent agent, OutgoingMessage msg, CancellationToken ct);
}
```

Claude Code provider transports, in order of preference:

1. **PTY** (sessions started with `agentctl run -- claude …`): text is typed into the
   PTY we own. Works when idle. Delivery = written + echoed.
2. **Hook mailbox** (any hooked session): message is queued; the next `PostToolUse`
   returns it as `additionalContext`, or a `Stop`/`SubagentStop` hook returns
   `decision: block` with the message as `reason` so the agent continues and reads
   it. Delivery = hook response sent. Limitation: an agent idle at its prompt fires
   no hooks; the UI says so.
3. **Peer socket** (Claude Code's internal session messaging): not implemented. The
   protocol is undocumented and authenticated by per-session tokens; see AGENT_INTEGRATION.md.

Controls: pause/resume = SIGSTOP/SIGCONT on the agent's process group (observed
effect); interrupt = ESC into PTY (PTY sessions only); terminate = SIGTERM
(confirmation required). Per-sub-agent stop is not supported by Claude Code; the UI
disables it with the reason.

## 9. Attribution, ownership & conflicts

* Edit/Write/MultiEdit/NotebookEdit ⇒ `file.*` for that agent, evidence `reported`;
  confirmed by the next git refresh (evidence upgraded to `observed` for the
  change itself, actor stays `reported`).
* Bash in a worktree ⇒ after completion, the git delta in that worktree is
  attributed `inferred` to the agent only if it was the sole agent active in that
  worktree during the call; otherwise `unattributed`.
* Conflict detector (pure, Amc.Core) raises:
  * `same-file` — ≥2 non-terminal agents changed the same file in the same worktree,
  * `cross-worktree` — same repo-relative path dirty in ≥2 worktrees of one repo
    (likely merge conflict),
  * `same-worktree` — ≥2 active agents writing in one worktree,
  * `git-unmerged` — unmerged index entries / in-progress merge or rebase.
* Hunk-level overlap (`ConflictAnalyzer`, daemon) for each `cross-worktree` conflict: for every
  pair of worktrees (first 4 worktrees only) it runs `git merge-base` on their HEADs, then in each
  worktree `git diff -U0 -M <merge-base> -- <path>` (committed **and** uncommitted changes;
  untracked files count as wholly new). Changed base-line ranges separated by fewer than 3
  unchanged lines are *overlapping* (severity high, merging will likely conflict); otherwise
  *different regions* (severity low, likely merges cleanly). When both heads committed a change
  to the file, `git merge-tree --write-tree` confirms the committed state (it writes unreferenced
  objects that `git gc` removes). Bounds: only already-flagged conflicts, only when their inputs
  (HEADs, file status) changed, after 2 s of quiet, ≤8 per 3 s tick, files ≤1 MB, diff ≤512 KB,
  10 s git timeout. Results are `conflict.analyzed` events, evidence `observed`.
* "Warn agents" (`ConflictWarner`) sends every active agent attributed to the file a factual
  heads-up through its normal message transport (other agent, tool, branch, both line ranges);
  it needs `confirm=true` and is audited (`conflict.warned` + `audit.control`). With
  `AutoWarnConflicts` (default off) overlapping conflicts are warned automatically, once per
  conflict and agent; again only after 10 min and only if the overlapping lines moved by more
  than 10 lines.

## 10. Persistence (SQLite, WAL)

```
events(seq INTEGER PK, id, ts, type, source, evidence, agent_id, session_id,
       repository_id, worktree_id, file_path, dedupe_key UNIQUE, data JSON)
  idx: (agent_id, seq), (worktree_id, seq), (file_path, seq), (type, seq), (ts)
snapshots(seq PK, created_at, state JSON)      -- latest few kept
cursors(key PK, value)                         -- transcript byte offsets etc.
```
Writes are batched (≤200 events or 50 ms per transaction) on one writer task.

## 11. API

REST (JSON, all under `/api`, token required):

```
GET  /api/health                     collector health, queue depth, drops, db, clients
GET  /api/state                      full WorldState snapshot + seq
GET  /api/events?after=&limit=&agent=&repo=&worktree=&file=&type=&from=&to=
GET  /api/agents | /api/agents/{id}
GET  /api/worktrees | /api/repositories
GET  /api/worktrees/{id}/diff?path=   (path validated inside worktree)
GET  /api/worktrees/{id}/file?path=
GET  /api/agents/{id}/diff           diff of files attributed to agent
GET  /api/processes                  live process overlay
POST /api/agents/{id}/messages       {text}
POST /api/agents/{id}/control        {action, confirm}
GET  /api/terminals/{id}/buffer
POST /hooks/claude                   Claude Code hook receiver (mailbox replies)
```

WebSocket `/ws?since=<seq>`:
```
server → {t:"hello", seq, serverTime}
server → {t:"event", e:{…}}             (replayed since `since`, then live)
server → {t:"patch", kind, id, value}   entity upserts from the reducer
server → {t:"live", processes:[…]}      ≤1 Hz volatile overlay
server → {t:"resync"}                    client too slow / gap → refetch /api/state
client → {t:"sub", topics:[…]} | {t:"ping"}
```
Per-client bounded send queue; overflow ⇒ `resync` and the drop is counted.

## 12. Security model

* Binds `127.0.0.1` only. Remote listen requires explicit flag + token.
* Token: 256-bit random in `~/.local/state/agent-mission-control/token` (0600).
  CLI and hooks read it; the UI receives it embedded in the served page.
* `Host` header must be loopback (DNS-rebinding defence); `Origin` checked on
  mutating requests and WebSocket upgrades.
* No shells: every subprocess uses `ArgumentList`. Paths from clients are resolved
  with realpath and must stay inside a known worktree root.
* Redaction at ingest (tokens, keys, passwords, `Authorization:` headers, private
  keys); environment shown as allow-listed names only.
* Dangerous actions (terminate, kill, remove worktree) require `confirm: true`
  plus an explicit UI confirmation dialog; every control action is an `audit.*` event.

## 13. UI architecture

No build step (no Node on target): ES modules in `wwwroot/`. A small store holds
entities keyed by (kind, id), fed by `/api/state` + WebSocket patches; views
subscribe to slices and re-render via keyed DOM diffing, batched per animation
frame. Long lists (timeline, events, terminal) are windowed. Dark theme default.

Views: Agent tree (left) · center tabs: Graph, Worktrees & Files, Activity,
Timeline, Processes, Health · Detail panel (right): agent / worktree / file /
repo · Bottom drawer: diff viewer, terminal, tool output, message thread.
`xterm.js` is vendored (MIT) for PTY sessions.

## 14. Performance budget

* Startup with 24 h backfill < 3 s on this machine.
* Hook round-trip < 20 ms p99 (hooks are synchronous for mailbox delivery).
* No full-repo scans on events; git status only on debounced triggers.
* Terminal ring buffer 2 MiB/session; tool output fields capped 16 KiB.
* UI: batch DOM updates per frame; virtualize lists > 200 rows.
