# Troubleshooting

Start with `agentctl doctor`, then `agentctl status`. The Health tab shows the same
data, plus the last success time of each collector.

| Symptom | Cause / fix |
|---------|-------------|
| `You must install .NET to run this application` | The apphost can't find a user-local .NET. Use the `amcd`/`agentctl` launchers from `scripts/install.sh` (they set `DOTNET_ROOT`), or export `DOTNET_ROOT=$HOME/.dotnet`. |
| `agentctl`: daemon unreachable (exit 3) | `systemctl --user status amcd`, then `journalctl --user -u amcd -n 50`. Port in use? Set `AMC_Port` in `~/.config/agent-mission-control/config.json`. |
| `amcd is already running` | Another instance (usually the systemd service) owns the socket. A second instance refuses to start instead of taking over. |
| Service doesn't start at login | `systemctl --user is-enabled amcd` should be `enabled`; re-run `scripts/install.sh`. |
| No agents appear | `claude-registry` health must be ok. Claude Code must be v2.x with `~/.claude/sessions/`. Check `AMC_ClaudeHome`. |
| Agents appear but nothing happens until a transcript is written | Hooks not installed: `agentctl hooks install`. Running sessions pick up hook changes automatically. |
| `claude-hooks` shows `disabled` | Hooks not installed in the settings file shown. |
| Hook events stopped | Hooks fail open on purpose. Check that the daemon is running, then check `~/.local/state/agent-mission-control/hook-payloads.jsonl` (the raw payloads, redacted). |
| Idle session not woken although `claude-rewake` exists | Look at `~/.local/state/agent-mission-control/rewake.log`: every waiter logs `start` and its `curl=` result. No `start` line when the session goes idle means that Claude Code session was started before the waiter hooks were installed and has not loaded them: restart it (`/exit`, then `claude --resume <session id>`). Verified: sessions started after `agentctl hooks install` are woken within about a second. |
| Message stays `queued` | Idle sessions are woken by the asyncRewake waiter hook armed at `Stop`/`SessionStart`/`idle_prompt` (`claude-rewake` health shows armed waiters). If none is armed (session idle for over an hour, or hooks installed before this feature: re-run `agentctl hooks install`), delivery happens at the agent's next hook. `agentctl run -- claude` sessions are always reachable through the PTY. |
| Agent shows `unknown` | No live process and no recorded end (historical session), or a sub-agent with no evidence for 30 min. The status reason says which. |
| Agent shows `disconnected` | Its process disappeared without an end event (crash or kill). Observed via `/proc`. |
| Files show no agent | The change happened through a shell command while several agents were active in that worktree, or outside any agent. It is deliberately left unattributed instead of guessed. |
| A worktree's edits appear late | Plain file edits don't touch git metadata. Status refreshes on agent activity in that worktree (debounced), on index/HEAD changes, and on a rescan every 30 s (active) or 5 min (idle). |
| `git` health shows `(polling)` | inotify isn't available. Rescans still work. Check `fs.inotify.max_user_instances`. |
| Terminal looks broken after `agentctl run` was killed with SIGKILL | Run `stty sane`. Any other way of exiting restores the terminal. |
| Remove the hooks | `agentctl hooks uninstall` removes only Overhive's entries. Backups are next to `settings.json` (`*.amc-backup-*`). |
| Reset all state | Stop `amcd` and delete `~/.local/state/agent-mission-control/amc.db*`. History older than the backfill window (24 h) is not re-imported. |

## Retention

The event log is pruned every 6 hours (`retention` in the Health tab):

| Kind | Kept | Config |
|------|------|--------|
| Superseded "latest value" events (file git status, worktree status) | 7 days; the newest per file/worktree is always kept | `DetailRetentionDays` |
| Detail: tool/command calls, process start/exit, file reads, agent text output, tests, notifications | 7 days | `DetailRetentionDays` |
| History: agent lifecycle, prompts, token usage, commits, messages, tasks, audit | 180 days | `HistoryRetentionDays` |

A fresh state snapshot is written before each run and only events covered by it are deleted, so restarts are
exact. Freed space is returned to disk (incremental vacuum). Caveat: after an upgrade that changes how state
is derived (`Reducer.Version`), state is rebuilt from the *retained* events, so counters derived from pruned
detail (for example tool-call counts older than the detail window) restart from what is still in the log.

## Windows via WSL2

Until native Windows support lands, Overhive can run inside WSL2 (the Linux on Windows that many AI coding
CLIs already use). This setup is **not yet verified**; if you try it, please report how it went.

1. Run your AI tools inside WSL (Ubuntu or another distribution), and install Overhive there with the Linux
   release archive, as in the README.
2. For the start-on-login service, enable systemd in WSL: add `[boot]` and `systemd=true` to `/etc/wsl.conf`,
   then run `wsl --shutdown` in Windows and reopen WSL. Without systemd, start the daemon by hand with
   `amcd &`.
3. Open **http://127.0.0.1:47821** in your Windows browser; WSL2 forwards localhost to Windows by default.

Limits:
- Overhive sees only agents running inside WSL, not tools running natively on Windows.
- Keep repositories in the Linux file system (for example `~/dev`), not under `/mnt/c`: file-change
  notifications do not work on Windows drives, so changes there are picked up late, and Git is much slower.

## macOS

- **Start and logs.** The installer adds a launchd agent (`~/Library/LaunchAgents/dev.overhive.amcd.plist`) that
  starts the daemon at login. Logs: `~/Library/Logs/Overhive/amcd.log`. Restart it with
  `launchctl kickstart -k gui/$(id -u)/dev.overhive.amcd`. With Homebrew, use `brew services restart overhive`;
  logs are in `$(brew --prefix)/var/log/overhive.log`.
- **"cannot be opened because the developer cannot be verified".** Release archives are not notarised (that
  needs a paid Apple account). Homebrew installs are not affected. For an archive downloaded with a browser,
  remove the quarantine flag once: `xattr -dr com.apple.quarantine overhive-<version>-osx-arm64`.
- **`overhive` not found.** The archive installer links into `~/.local/bin`; add it to your PATH, for example
  `echo 'export PATH="$HOME/.local/bin:$PATH"' >> ~/.zshrc`.
- **Claude Code sessions.** On macOS, Overhive may not be able to compare Claude Code's recorded process start
  time with its own, and then accepts the live `claude`/`node` process with that pid. Please report it if a
  finished session keeps showing as running.

