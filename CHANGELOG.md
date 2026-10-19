# Changelog

All notable changes to Overhive. Versions follow [Semantic Versioning](https://semver.org/); while the
version is 0.x, minor versions may change behaviour.

## [Unreleased]

### Added
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
- Better text contrast: dark text on amber primary buttons, a darker amber accent in the light theme and
  lighter "faint" text in the dark theme (WCAG AA).
- New 3D logo: the three hive cells are now extruded, lit honeycomb cells with the watching eye on the
  raised centre cell. Adds a full favicon set (SVG, 16/32 px PNG, Apple touch icon, 192/512 px app icons),
  web app manifests for the dashboard and the phone page, a flat `logo-mono.svg` for tiny sizes, and a
  social preview image (`docs/media/social-preview.png`).

### Fixed
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

[0.2.0]: https://github.com/Abhijeetsinghjat/overhive/releases/tag/v0.2.0
[0.1.0]: https://github.com/Abhijeetsinghjat/overhive/releases/tag/v0.1.0
