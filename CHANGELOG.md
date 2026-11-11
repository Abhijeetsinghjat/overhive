# Changelog

All notable changes to Overhive. Versions follow [Semantic Versioning](https://semver.org/); while the
version is 0.x, minor versions may change behaviour.

## [Unreleased]

### Added
- Approve or deny Claude Code tool calls from the dashboard (opt-in, config `Approvals`, off by default).
  `"Mode": "risky"` holds the calls the risk rules rate warn or critical; `"all-edits"` also holds every
  Write/Edit/MultiEdit/NotebookEdit. A held call is an urgent **Needs you** card (also on the phone page and
  in the agent panel) with the command or file and a diff preview, the risk finding, **Allow**, **Deny**
  with a reason the agent sees, **Ask in terminal** (hands the call to Claude Code's own prompt at once)
  and, except for critical findings, **Allow this command for this session** (exact command, same
  directory) or **Allow edits to this file for this session**. The risk recorder's finding for the same
  call is merged into that card rather than raising a second item. `overhive approvals` lists them and
  `overhive approvals allow|deny|ask <id> [--reason] [--similar]` decides. Without a decision within `TimeoutSeconds`
  (default 120), when the daemon is down or on any error, Claude Code's normal permission flow applies:
  nothing is allowed silently. Decisions are control actions (token, Origin/Host checks, `EnableControl`,
  plus `Approvals.AllowRemote`), ids are random and single-use, and `approval.requested` /
  `approval.resolved` events (decision, local or remote, latency) appear in the timeline. `overhive hooks
  install` writes a PreToolUse hook with a matching longer timeout only while approvals are on (curl on
  Linux/macOS, `hook-forward --approval --max-time` on Windows); `hooks status` and `doctor` check it.
- Clean up a worktree after its PR merges. Linked worktrees whose branch is merged are detected through the
  user's own `gh` CLI when installed and authenticated (observed; matched by branch name and by the worktree's
  HEAD being part of the PR head, so a branch reused after the merge is not merged) or, without gh, from git
  alone without fetching ("likely merged", inferred). Default mode `suggest`: a **Merged — clean up** badge and
  button, a low-priority "Needs you" item, and a confirm dialog listing what will be removed and every safety
  check. Removal is `git worktree remove` (never `--force`) plus `git branch -d` (`-D` only for a squash merge
  whose tip gh confirmed as the PR head); the remote branch is deleted only with `DeleteRemoteBranch` and only
  when its tip is the PR head. Blocked for the main or a locked worktree, uncommitted or untracked files,
  unpushed commits, an active agent, an open Overhive terminal or any process with its current directory
  inside; all re-checked right before removal. Ignored files that are not regenerable (`.env`, keys, local
  config; `RegenerableIgnored` lists the safe ones such as `node_modules`, `bin`, `dist`) block auto mode and
  need an extra confirmation (`confirmIgnored`, `--include-ignored`) for a manual cleanup. `Mode: "auto"` removes after a 30 min grace period. New
  events `worktree.merged` and `worktree.cleaned` (timeline), `GET/POST /api/worktrees/{id}/cleanup`,
  `POST /api/worktrees/check-merged`, and `overhive worktrees clean [<path>] [--dry-run] [--yes]`. `Reducer.Version` 15.
- **Start a session in a new worktree**: **+ New session** offers *Run in: this folder | a new worktree* once
  the folder is inside a Git repository. Enter a branch name (validated with `git check-ref-format`, refused
  if it exists unless *Use an existing branch* is ticked), pick the base from a searchable list of local and
  remote branches (default: the remote default branch, e.g. `origin/main`, else the current branch) and check
  the full location before starting (default `<repo-parent>/<repo>.worktrees/<branch>`, configurable with
  `NewWorktree:Root` and a `{repo}` placeholder; long-path warning on Windows). Overhive runs
  `git worktree add --no-track -b <branch> <path> <base>` (argument list, no shell) and starts the tool there.
  Optional, off by default: *fetch the base first*, and a per-repository `NewWorktree:SetupCommand` (e.g.
  `npm ci`) run before the agent starts, because ignored files are not in a new worktree. A failure after
  the worktree exists leaves it in place and reports its path; nothing is deleted automatically.
  CLI: `overhive run --worktree <branch> [--base REF] [--path DIR] [--existing] [--fetch] [--setup] -- <tool>`.
  API: `GET /api/new-worktree/plan`, `POST /api/new-worktree`, and `worktree` in `POST /api/sessions`.
  Such worktrees are marked in state (`Worktree.Origin`, event `worktree.provisioned`: branch, base, session
  terminal, tool) and in the worktree panel, so cleanup after merge can be offered later. `Reducer.Version` 14
  (snapshots from older versions are discarded and the log replayed once).

## [0.3.0] - 2026-11-02

Still beta. First release with a native Windows build (early preview), plus a 3D dashboard, much lower
daemon memory over long uptimes, and a quieter risky-command log.

### Added
- Native Windows install (early preview), no admin rights needed: `scripts\install.ps1` builds from source,
  and releases gain a self-contained `overhive-<version>-win-x64.zip` with `install-release.ps1`. Both put
  `overhive`, `agentctl` and `amcd` on the user PATH and start the daemon at logon, hidden, through the
  per-user scheduled task `Overhive`; re-running updates, `-Uninstall` removes it (data kept unless
  `-RemoveData`). Prepared, not yet published: a Scoop manifest generator (`bucket/` in this repository) and
  winget manifests (`packaging/winget/`).
- **Hive**, a 3D view of all agents and the new default tab when the browser supports WebGL: every
  session is a hexagonal cell with its sub-agents tiled around it, grouped by repository worktree. Glow
  shows status (running pulses, waiting/blocked amber, failed red, finished dimmed), height shows token
  usage, red arcs connect agents in conflict, runaway-guard alerts ring the session, critical risks float
  a red marker. Orbit, zoom, hover for details, click to open the agent, double-click to focus, arrow keys
  to move between agents. Honours the filters, both themes and reduced motion. **2D** (or no WebGL) falls
  back to the Graph tab. three.js (MIT) is vendored and loaded only when the tab opens.
- A spatial, 3D look for the whole dashboard and the phone page: layered elevation lit from one direction,
  glass for floating panels, a faint hex-lattice floor behind the views, cards that tilt toward the pointer,
  and short 3D entry motion for tabs, the details panel, the drawer, dialogs, the "Needs you" panel and
  toasts. The **3D** button in the header turns the effects off (stored in the browser as
  `amc.effects3d`); they are also reduced automatically when the system asks for reduced motion.

### Changed
- The README demo is re-recorded around the 3D Hive (orbit, hover, open an agent), the "Needs you" queue,
  risky commands and claim checks. The recorder renders WebGL on the GPU and captures with Chrome's
  screencast (about 37 frames/s at 2x instead of 8).
- Better text contrast: dark text on amber primary buttons, a darker amber accent in the light theme and
  lighter "faint" text in the dark theme (WCAG AA).
- The risky-command log is much quieter on normal agent work (a month of real use: 386 findings, 11
  critical and 352 warn, replay to 221 with 2 critical and 23 warn; see docs/RISKS.md). Temporary and
  scratch directories (`/tmp`, `$TMPDIR`, `/var/folders`, `%TEMP%`, Claude scratchpads) are no longer
  "outside the worktree", and deleting a temp directory that holds a scratch worktree is info, not critical
  (the temp root itself, home and `/` stay critical). Literal shell variables assigned earlier in the same
  command (`S=/tmp/x; rm -rf $S/y`) are resolved. Force-pushes to a bare repository on disk are info (none
  in a temp dir); `--force-with-lease` to a feature branch is info; main/master stay critical. `git branch
  -D` is info except for main/master. `git reset --hard` is info when the worktree is clean or the repo is
  a temp scratch clone; git's own helper processes (`…/git-core/git reset --hard` run by `git worktree add`)
  are ignored. Single-file writes and deletes outside the worktree are info for ordinary files under home
  and warn for dotfiles, `.git/`, `~/bin`, `~/Library`, `AppData`, files directly in home and anything
  outside home. The Overhive API token is warn (local-only credential), and `ls`/`stat`/`chmod` on a warn
  credential is info. `rmdir` without `-r`/`/s` is not reported.
- New 3D logo: the three hive cells are now extruded, lit honeycomb cells with the watching eye on the
  raised centre cell. Adds a full favicon set (SVG, 16/32 px PNG, Apple touch icon, 192/512 px app icons),
  web app manifests for the dashboard and the phone page, a flat `logo-mono.svg` for tiny sizes, and a
  social preview image (`docs/media/social-preview.png`).

### Fixed
- A worktree with more than 500 dirty paths (a no-checkout scratch clone where every file shows as deleted)
  no longer keeps a state entry per file: only files an agent touched stay; one such clone with 60,000
  paths had doubled the state after the upgrade.
- Linux install: the installer skipped the systemd user service (no start at login, an old unit kept on
  update) when `systemctl --user is-system-running` reported `degraded`, which only means some unrelated
  unit failed and is common on Ubuntu.
- Overhive's own hook commands (`… # agent-mission-control`, the rewake waiter that appends to
  `rewake.log`, `agentctl hook-forward`) are no longer reported as the agent's risky actions. Only exact
  hook commands are skipped: `cat …/token` or `rm -rf ~ # agent-mission-control` are still classified.
- A dangerous command padded past 16K characters no longer escapes the risk rules: hook payloads keep
  the Bash command up to 1 MB (head and tail beyond), and the recorder classifies the full redacted command
  instead of the 16K copy that is stored. Text that only looks like a command (PowerShell `iex`/Base64 in a
  POSIX heredoc, a capped command's partial first line, a process command line cut by the collector) is
  no longer read as one, and commands after `then`/`do`/`else` are classified.
- The daemon's state no longer grows without bound: removed worktrees and their files, idle clean files,
  finished agent sessions (14 days), old commits, test runs and risk findings (14 days), usage marks and
  claim-check details (7 days) are dropped from memory in event time (`StateRetention`; the timeline keeps
  the full log). A worktree with more than 500 dirty paths reports summary counts only for new paths, and
  event-log compaction also drops git status of removed worktrees and old "clean" statuses. On one real
  installation the state went from 33 MB / 43,333 files to 5 MB / 1,287 files, and the database from
  575 MB to 253 MB after the first retention run. The first start after upgrading replays the event log.
- The Hive tab was blank and the **3D** button did nothing on `main`: a merge had dropped the Hive and 3D
  effects start-up from the dashboard's entry module. Restored, with a test that every view's start-up
  function is called.
- Dashboard dialogs and narrow windows: the New session working-directory picker no longer opens a native
  list that spills outside the dialog; it is a searchable list anchored inside the dialog that scrolls,
  shortens long paths in the middle (full path on hover) and works with the arrow keys, Enter and Escape.
  Dialogs keep Tab focus inside and return focus to what opened them; the header no longer runs off
  the right edge (it cut off the 3D and help buttons even at 1920 px): it stays one row from 1280 px up by
  dropping button words first, and wraps below that; search results, the "Needs you"
  panel and the agent-tree slide-over stay on screen and below the header and banners; Escape closes only
  the topmost panel; the current tab scrolls into view; the help lists every tab shortcut and `n`.
- Lower and steadier daemon memory. State snapshots are written at most every 5 minutes (and at
  least every 2,000 events) instead of every 2,000 events, are gzip-compressed (33 MB of JSON becomes 4.4
  MB), serialized into pooled 64 KB chunks and streamed into SQLite, and only the newest two are kept. The
  daemon uses the Workstation GC with `ConserveMemory=5` instead of the Server GC, the systemd unit and
  launcher set `MALLOC_ARENA_MAX=2` (re-run the installer to update an installed unit), the WAL is truncated
  to 8 MB after checkpoints, and `/api/state` no longer builds a full-size string per request. Transcript
  tailing reads with a pooled 64 KB buffer and caps a single line at 8 MB (longer lines still start and end
  their tool calls). `/api/health` no longer counts every event on each poll. Older JSON snapshots are still
  read; an older daemon started on the upgraded database replays the event log instead.
- Retention runs on wall-clock time, checked every minute, so a laptop that slept no longer postpones it
  or makes the Health tab report it as stale.
- Daemon memory no longer grows with uptime from live buffers: remembered Claude tool calls keep only the
  fields the end needs (not Write content or Edit strings) and are dropped 5 minutes after they end (cap
  5,000, was 50,000 full copies); queued hook calls are compacted and bounded to 32 MB; each dashboard
  WebSocket queue is bounded to 8 MB, process/attention updates replace each other instead of queuing,
  patches are coalesced over 250 ms, and a socket that stops answering pings or sends for 30 s is closed;
  terminal viewers are bounded to 4 MB queued; an hourly sweep forgets per-session state of ended sessions
  and empty mailbox queues; the idle-session wake-up timer is cancelled when a message arrives.

## [0.2.0] - 2026-10-17

Still beta. First release with macOS builds.

### Added
- macOS support (Apple Silicon and Intel): process detection for agents Overhive did not start, live
  terminals, repository watching, pause/resume, and a launchd agent that starts the daemon at login.
  Release archives `osx-arm64` and `osx-x64`, and a Homebrew formula (`brew install overhive`,
  `brew services start overhive`). The full test suite runs on macOS in CI and every release archive is
  install-tested on a Mac before it is published.
- The "Needs you" queue now includes runaway-guard alerts (critical: urgent), risky actions (critical:
  urgent, each on its own; warnings combined per agent and rule) and claims contradicted by the evidence
  (normal), so they notify like everything else when notifications are on.
- Risky-action flight recorder: every agent's commands (reported by hooks/protocol and observed in its
  process tree) and file events are checked for recursive deletes outside the worktree, force-pushes,
  `git reset --hard`/`clean -fdx`, `chmod -R 777`, pipe-to-shell installs, credential file paths, writes
  outside the worktree, publishes/deploys and database drops. Findings carry severity, an explanation and
  the evidence label; see the **Risks** tab, the red badge on agents, `overhive risks` and `GET /api/risks`
  ([rules](docs/RISKS.md)). Credential files are matched by path only and never opened.
- Line-level conflict check: when the same file is changed in several worktrees, Overhive compares the
  changed lines of each worktree (committed and uncommitted) against the branches' merge base and shows
  "overlapping lines — will likely conflict" or "different regions — likely merges cleanly", with the line
  ranges per agent and branch (evidence: observed). `git merge-tree` confirms committed changes.
- **Warn agents** on a conflict (dashboard, `overhive conflicts warn <n>`, `POST /api/conflicts/warn`):
  each involved agent gets a short, factual heads-up through its usual message transport; the result is
  reported per agent (delivered, queued, unsupported, failed) and audited. Requires confirmation.
- `AutoWarnConflicts` option (off by default): warn automatically once per conflict and agent, again only
  when the overlapping lines change.
- `overhive conflicts` lists conflicts with their compared line ranges; `GET /api/conflicts/detail?key=`
  shows the analysis and the exact warning text per agent.
- Claimed vs observed: when an agent says "all tests pass", "the build succeeds", "I committed the changes"
  or "created `src/x.ts`", Overhive checks it against the commands it ran, Git and the files, and shows
  each claim as confirmed, contradicted or unverified with the evidence behind it (agent panel →
  Verification, `GET /api/agents/{id}/verification`). Contradicted claims show as ✗ N on the agent's card
  and tree row. A review hint (inferred) flags sessions where tests went from failing to passing after the
  tests themselves were changed.
- Runaway guard: warns when a session fans out into too many sub-agents (at once, per 10 minutes, or too
  deep) or burns tokens too fast (per minute, per hour, optional per-session budget). Banner, header badge,
  agent-panel section with **Pause tree** / **Dismiss**, counters on the graph, `overhive guard` and
  `GET /api/guard`. Optional `Guard.AutoPause` (off by default) pauses a session on a critical alert, audited
  as automatic; it never kills. See [docs/RUNAWAY_GUARD.md](docs/RUNAWAY_GUARD.md).
- "Needs you" attention queue across all tools: blocked and waiting agents, failures, unexpected exits and
  conflicts, ordered by priority and age, each with its evidence label and main action. Header badge (key `a`),
  Overview panel, `overhive attention`, `GET /api/attention`; snooze and dismiss kept by the daemon.
- Opt-in notifications for new urgent items, rate-limited and combined: browser notifications from the
  dashboard, desktop notifications from the daemon (`Notifications.Desktop`, notify-send / osascript), and push
  through your own ntfy server (`Notifications.NtfyUrl`), the only outbound call and only when configured.
- Phone page `/m` and a guide to reaching it through your own SSH tunnel or `tailscale serve`; optional
  `AllowedHosts` (exact names, empty by default) for the `tailscale serve` host name.
- Extension point `IAttentionProvider` so other features can add items with one registration line.

## [0.1.0] - 2026-10-15

First public release: a free, open-source, local control center for AI coding agents. Linux (x64, arm64).

### Agents, from any tool
- Every agent and sub-agent on the machine, whatever tool runs it, with its status and the evidence behind it
  (observed, reported or inferred).
- Works without setup for any tool, by recognising its running process: Claude Code, Codex CLI, Gemini CLI,
  Grok, Aider, OpenCode, Cursor CLI, GitHub Copilot CLI, plus your own tools in `config.json`.
- Hook integrations, installed with `overhive hooks install --tool <tool>`: Claude Code, Codex, Gemini CLI,
  Cursor and Copilot (with messaging), Grok (monitoring). Only Claude Code has been verified against a live
  install so far.
- Open protocol (`POST /api/v1/report`, `/api/v1/inbox`, `overhive emit`, `overhive inbox`) so any tool or
  script can report and receive messages.

### Dashboard
- Agent tree and graph with models, tokens and sub-agents; worktrees and files with diffs per agent; activity,
  timeline, commits (linked to GitHub, GitLab or Bitbucket), processes, token usage and health.
- Conflict detection: the same file in several worktrees, or several agents in one worktree.
- Message, pause, resume or stop an agent; start a new session of any installed tool in a live terminal.

### Security and privacy
- Local only: listens on 127.0.0.1 and a private Unix socket; API token in a 0600 file; Host and Origin
  checks; strict CSP; secret redaction before storage; audit log. No telemetry, no accounts.
- Never asks for, reads or stores AI credentials; each tool uses your own subscription.

### Install
- Self-contained release archives (no .NET needed) with `SHA256SUMS` and GitHub build provenance;
  systemd user service that starts on login.

[0.3.0]: https://github.com/Abhijeetsinghjat/overhive/releases/tag/v0.3.0
[0.2.0]: https://github.com/Abhijeetsinghjat/overhive/releases/tag/v0.2.0
[0.1.0]: https://github.com/Abhijeetsinghjat/overhive/releases/tag/v0.1.0
