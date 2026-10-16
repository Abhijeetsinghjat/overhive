# Event model

The append-only event log is the single source of truth. Every UI view, CLI output and
API response is derived from it (`Reducer.Apply` in `Amc.Core/State/Reducer.cs`).

## Envelope

| Field          | Type      | Notes |
|----------------|-----------|-------|
| `seq`          | int64     | Global, contiguous, assigned by SQLite on insert. Replay cursor for clients. |
| `id`           | ULID      | Time-sortable unique id. |
| `ts`           | ISO-8601  | When the fact happened (from the source when available, e.g. transcript timestamp). |
| `type`         | string    | See catalogue below. Constants in `EventTypes`. |
| `source`       | string    | `hook`, `transcript`, `registry`, `process`, `git`, `pty`, `user`, `system`, `simulator`. |
| `evidence`     | enum      | `observed` / `reported` / `inferred` (see ARCHITECTURE.md §1). |
| `agentId`      | string?   | `claude:<session>` or `claude:<session>:<subagent>`. |
| `sessionId`    | string?   | Provider session. |
| `repositoryId` | string?   | `repo:<hash of common git dir>`. |
| `worktreeId`   | string?   | `wt:<hash of worktree root>`. |
| `filePath`     | string?   | Relative to the worktree root when `worktreeId` is set. |
| `dedupeKey`    | string?   | UNIQUE. Same fact from two collectors (hook + transcript) is stored once. |
| `data`         | object    | Typed payload (records in `Amc.Core/Events/EventTypes.cs`). Redacted and capped (16 KiB per string) at ingest. |

## Catalogue

| Type | Payload | Typical source | Evidence |
|------|---------|----------------|----------|
| `agent.created` | `AgentCreatedData` | registry, hook `SessionStart` | reported |
| `agent.spawned` | `AgentCreatedData` (ParentId) | transcript `subagents/*.meta.json`, hook `SubagentStart` | reported |
| `agent.started` / `agent.status_changed` | `AgentStatusData` | registry busy/idle, hooks `Stop`/`Notification`, control | reported / observed |
| `agent.completed` / `agent.failed` | `AgentStatusData` | `SessionEnd`, `SubagentStop`, Agent tool result, registry removal | reported |
| `agent.updated` | `AgentUpdatedData` | title, cwd, branch, pid, terminal link | reported / observed |
| `agent.prompt` | `AgentTextData` | user prompt to the agent | reported |
| `agent.output` | `AgentTextData` | assistant text | reported |
| `agent.notification` | `AgentTextData` | permission / idle notifications | reported |
| `agent.message_sent` | `MessageData` | Overhive user | observed |
| `agent.message_delivered` / `agent.message_failed` | `MessageStatusData` | transport result | observed |
| `agent.message_received` | `MessageReplyData` | first agent output after delivery | **inferred** |
| `process.started` / `process.exited` | `ProcessData` / `ProcessExitData` | `/proc` | observed |
| `tool.started` / `tool.completed` / `tool.failed` | `ToolCallData` | hooks, transcript | reported |
| `command.started` / `command.completed` / `command.failed` | `CommandData` | Bash tool calls | reported |
| `file.created` / `file.modified` / `file.deleted` / `file.renamed` | `FileChangeData` | Edit/Write tools (actor known) | reported |
| `file.read` | `FileChangeData` | Read tool | reported |
| `git.file_status` | `GitFileStatusData` | `git status` refresh; optional actor | observed (actor inferred) |
| `git.branch_changed` / `git.checkout` | `GitRefData` | HEAD change | observed |
| `git.commit_created` | `GitCommitData` | new commits on HEAD | observed (author agent inferred) |
| `git.merge` / `git.rebase` / `git.conflict` | `GitRefData` / `GitConflictData` | git dir state | observed |
| `repository.discovered` | `RepositoryData` | workspace scan, path resolution | observed |
| `worktree.created` / `worktree.changed` / `worktree.deleted` | `WorktreeData` | `git worktree list`, status | observed |
| `test.started` / `test.passed` / `test.failed` | `TestRunData` | test command + parsed output | inferred |
| `task.updated` | `TaskListData` | TodoWrite / task tools (full list snapshot) | reported |
| `terminal.started` / `terminal.exited` | `TerminalData` | `agentctl run` | observed |
| `audit.control` | `AuditData` | every control action (source `system` when automatic, e.g. the runaway guard's pause) | observed |
| `guard.alert` / `guard.cleared` | `GuardAlertData` / `GuardClearedData` | runaway guard (level changes only; see RUNAWAY_GUARD.md) | reported (agent counts carry their status evidence) |
| `guard.dismissed` | `GuardDismissedData` | Overhive user | observed |
| `risk.detected` | `RiskData` (rule, severity, explanation, matched span, target, redacted command) | risk recorder on commands, child processes and file events ([RISKS.md](RISKS.md)) | of the source: reported / observed |

## Dedupe keys (shared by hook and transcript ingestion)

```
tool:<tool_use_id>:start      tool:<tool_use_id>:end      file:<tool_use_id>
prompt:<session>:<prompt_id>  spawn:<agent_id>            end:<agent_id>
test:<tool_use_id>:start      test:<tool_use_id>:end      commit:<worktree>:<sha>
proc:<pid>:<procStart>:exit   msg:<message_id>:delivered
risk:<agent>:<rule>:<evidence>:<hash of target or matched command>
```

## Derived state (not events)

Conflicts (`same-file`, `cross-worktree`, `same-worktree`, `git-unmerged`) are
recomputed from state after every batch (`ConflictDetector`) so they always reflect the
current truth and disappear when resolved.

The attention queue ("needs you") is derived the same way, by `AttentionDeriver` from state through
`IAttentionProvider`s, after every batch; it is never stored as events (see NOTIFICATIONS.md).

## Volatile data (not events)

CPU %, RSS and PTY output bytes are pushed over the WebSocket as `{"t":"live"}` and
terminal streams; they are never written to the event log.
