# Agent integration: how agents are discovered and tracked

Overhive is tool-neutral. Every AI coding tool gets one of three integration levels (see the README):
**any tool** (process discovery + Git + optional Overhive-owned terminal), the **open protocol**
(`docs/PROTOCOL.md`), or a **native adapter**. Tools are defined in a catalog (Claude Code, Codex,
Gemini CLI, Grok, Aider, OpenCode, Cursor CLI, Copilot CLI) that users extend in `config.json`:

```json
{ "Tools": [ { "Id": "mytool", "Name": "My Tool", "Command": "mytool", "ProcessNames": ["mytool"],
               "ModelFlag": "--model", "Models": ["fast", "smart"], "PromptArg": "positional" } ] }
```

Each tool authenticates with the user's own subscription or key. Overhive handles no AI credentials.

## Native adapter: Claude Code


This section describes exactly where every piece of agent information comes from for
the first native adapter, **Claude Code** (verified against v2.1.292). Code: `src/Amc.Daemon/Providers/Claude/`.

## Sources, in order of authority

| # | Source | Mechanism | Gives | Evidence |
|---|--------|-----------|-------|----------|
| 1 | Session registry `~/.claude/sessions/<pid>.json` | inotify + 5 s reconcile | live sessions: pid, `procStart`, sessionId, cwd, version, `busy`/`idle` | reported |
| 2 | `/proc/<pid>` | 2 s scan of agent pids + descendants | liveness (pid **and** start time must match: PID-reuse safe), CPU, RSS, child commands | observed |
| 3 | Hooks (`agentctl hooks install`) | Claude Code POSTs each hook payload to `/hooks/claude` | prompts, every tool call + result, sub-agent start/stop, permission waits, turn end, session end | reported |
| 4 | Transcripts `~/.claude/projects/*/<session>.jsonl` and `<session>/subagents/agent-<id>.{jsonl,meta.json}` | inotify tail with persisted byte offsets; 24 h backfill | history, assistant text, sub-agent tree (`meta.toolUseId` → parent's Agent call), titles | reported |
| 5 | Git | inotify on git metadata dirs + debounced `git status` | repos, worktrees, branches, HEAD, dirty files, +/-, commits, merges/rebases/conflicts | observed |

Hooks and transcripts describe the same tool calls. They share dedupe keys
(`tool:<id>:start`, `file:<id>`, …), so whichever arrives first is stored and the
second is ignored. Without hooks, everything except mailbox messaging and permission-wait
detection still works, but it is driven by transcript writes.

The registry and transcript formats are **undocumented Claude Code internals**. The parsers
are tolerant; `agentctl doctor` and the Health tab report when they stop producing data.

## Identity

| Entity | Id |
|--------|----|
| Main agent (a Claude Code session) | `claude:<sessionId>` |
| Sub-agent | `claude:<sessionId>:<agentId>` (the agent id from the hook's `agent_id`, which matches the transcript's `agent-<id>.jsonl`) |
| Process | `pid:<pid>:<procStart>` (`procStart` = `/proc/<pid>/stat` field 22) |
| Worktree / repository | hash of worktree root / git common dir |

## Status rules

| Status | Set when | Evidence |
|--------|----------|----------|
| running | registry `busy`, a prompt, or a tool call in progress | reported |
| waiting | registry `idle`, or a `Stop` hook (turn finished) | reported |
| blocked | `Notification` hook of type `permission_prompt` | reported |
| paused | we sent SIGSTOP and `/proc` shows state `T` | observed |
| completed | `SessionEnd` hook, registry entry removed on clean exit, `SubagentStop`, or the parent's Agent tool result | reported |
| failed | the parent's Agent tool result is an error | reported |
| disconnected | the agent's process vanished (or its start time changed) with no end event, or its parent session died while it was active | observed |
| unknown | no live process and no end event for a historical session; or a sub-agent with no evidence for 30 min and no completion recorded | observed / inferred |

Evidence older than the current status never overrides it. This means backfilled
history can't flip a live idle session to "running", and late events can't resurrect
a finished agent. Newer activity can reopen an agent (for example, a resumed session).

## Worktree and file attribution

An agent's `cwd` does **not** say which worktree it works in: sub-agents commonly
operate on other worktrees through absolute paths. Attribution therefore uses:

1. `file_path` / `notebook_path` / `path` in tool inputs → longest-prefix worktree root
   (**reported**). Unknown repositories are discovered on demand.
2. Bash commands: absolute paths, `cd <dir>`, `git -C <dir>` parsed heuristically, else
   the record's cwd (**inferred**). Inferred locations never displace a reported current worktree.
3. Changes made by shell commands: after the command ends, the git delta in that worktree
   is attributed to the agent **only if it was the only agent active there** during the
   command (**inferred**). Otherwise the change stays unattributed.

## Messaging

| Transport | When available | Delivery |
|-----------|----------------|----------|
| `pty` | the agent was started with `agentctl run -- claude` | text is typed into the PTY that `agentctl run` owns, followed by Enter. Works while the agent is idle. |
| `hook-mailbox` | hooks installed, agent not finished | queued. At the agent's next `PostToolUse`/`UserPromptSubmit` hook it is returned as `additionalContext`; at `Stop`/`SubagentStop` it is returned as `decision: block` with the message as `reason`, so the agent continues and reads it. Never blocks when `stop_hook_active` is true. |

Message states: `queued → delivered → answered`, or `failed`. Delivery is
**observed**: the hook response carrying the message was sent. "Answered" links the
agent's *earliest* text output after delivery, decided by timestamp, not arrival
order, and is labelled **inferred**. Verified limitation: Claude Code does not persist
every assistant text block to its transcript (in a live test, the direct "ACK" reply
written between two tool calls was never persisted). When that happens, the linked
reply is the next text that *was* persisted, or the `Stop` hook's
`last_assistant_message`.

**Idle sessions** are reached through Claude Code's documented `asyncRewake` hooks. When a
session goes idle (`Stop`, `SessionStart`, the `idle_prompt` notification), a background
waiter hook long-polls `POST /hooks/claude/wait` over the private socket. When a message is
queued, the daemon answers at once; the hook prints the message to stderr and exits 2, and
Claude Code wakes the idle session with it. Measured locally, from send to wake-up:
about 0.25 s. As soon as the session is busy again (`UserPromptSubmit`/`PreToolUse`),
waiters are released and mid-turn delivery goes through `PostToolUse`. A waiter lives up to
an hour (hook timeout 3600 s). After that, delivery falls back to the next hook.

**Conflict warnings** use the same transports. "Warn agents" on a cross-worktree conflict
(or `AutoWarnConflicts`) sends each active agent attributed to the file a short message such as
"Heads-up from Overhive: Gemini (Gemini CLI, branch fix/y) is also editing src/auth.ts lines
52-70, overlapping your lines 40-80. Coordinate before changing those lines." Agents without a
transport are reported as `unsupported`; the others as `queued` or `delivered`.

**Not implemented:** Claude Code's internal session-to-session sockets
(`/run/user/<uid>/cc-socks/*.sock`). Their protocol is undocumented and authenticated by
per-session tokens; researching it was declined.

## Controls

| Action | Main agent | Sub-agent |
|--------|------------|-----------|
| pause / resume | SIGSTOP / SIGCONT to the agent's process tree, verified via `/proc` | not supported. Claude Code exposes no per-sub-agent process. |
| interrupt | ESC into the PTY (only for `agentctl run` sessions) | not supported |
| terminate / kill | SIGTERM / SIGKILL; requires confirmation | not supported |

Pausing an agent that is mid-request can make that API request time out. Every control
action is recorded as an `audit.control` event.

## Adding another provider

Implement `IAgentProvider` (`Amc.Core/Providers/IAgentProvider.cs`): capabilities,
messaging and control for agents whose ids start with your prefix. Then add collectors
that publish normalized events (`IEventSink`) using the shared event catalogue. Use
`IWorktreeIndex` for path attribution, and register everything in `Composition.cs`.
The reducer, storage, API, CLI and UI need no changes.
