# Changelog

All notable changes to Overhive. Versions follow [Semantic Versioning](https://semver.org/); while the
version is 0.x, minor versions may change behaviour.

## [Unreleased]

### Added
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

[0.1.0]: https://github.com/Abhijeetsinghjat/overhive/releases/tag/v0.1.0
