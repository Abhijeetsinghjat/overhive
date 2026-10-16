# Security model

Overhive can read source code, terminals, processes and Git state and can
signal processes. It is designed for a single user on their own machine.

## Threat model

| Threat | Mitigation |
|--------|------------|
| Another machine on the network reaches the daemon | Binds `127.0.0.1` only; the daemon refuses to start with any other bind address. |
| A malicious web page in the user's browser calls the API (CSRF) | Every `/api`, `/ws`, `/hooks`, `/pty` request needs the token; cross-origin `Origin` headers are rejected; responses carry no CORS headers. |
| DNS rebinding (attacker domain resolving to 127.0.0.1) | `Host` header must be a loopback name/address, or an exact name the user listed in `AllowedHosts` (empty by default; wildcards refused), otherwise `421`. |
| Data leaving the machine | Nothing by default. The only outbound call is the optional ntfy push (`Notifications.NtfyUrl`, the user's own server): titles only, redacted, no redirects, no retries. Desktop notifications use `notify-send`/`osascript` via `ArgumentList`. |
| Another local user | Token file `0600` in a `0700` state dir; SQLite db `0600`. (Root and the same Unix user are trusted.) |
| Another local user squatting the port while the daemon is down, or reading the token from `ps` | Hooks talk to the daemon over a **Unix socket** (`amcd.sock`) in the 0700 state dir. Requests over the socket are authenticated by filesystem permissions, so no token appears on any command line, and no other user can create that path. The daemon runs with umask 077. |
| Secrets leaking into the UI / database | `Redactor` runs centrally in the ingest pipeline on every string before persistence: API keys (Anthropic, OpenAI, GitHub, Slack, AWS, Google, npm), bearer/authorization headers, `password=`/`token=`-style assignments, URL credentials, JWTs, private keys. Environment variables are shown as an allow-list of names; values of other variables are never read. Thinking/reasoning blocks from transcripts are never ingested. **Exception:** raw PTY output from `agentctl run` sessions (ring buffer, `terminals/*.log`, live terminal view) is stored and shown byte-for-byte without redaction, because rewriting a terminal stream would corrupt it. It stays in 0600 files in the 0700 state dir and is served only to token-holding clients. |
| Repository-configured git filters | `git status` runs `clean`/`process` filters that a repository's own `.git/config` + `.gitattributes` define, exactly as any shell prompt with git integration does. Only point `WorkspaceRoots` at directories whose repositories you trust (default `~/dev`). |
| Path traversal via diff/file endpoints | Paths must be relative, contain no NUL, not start with `-`; they are canonicalized, symlinks resolved, and must stay inside a known worktree root; `--` always precedes paths in git invocations. |
| Command injection | No shell is ever used: every subprocess uses `ProcessStartInfo.ArgumentList`. Signals go through `kill(2)` via P/Invoke. |
| Monitoring itself disturbing agents | All git calls use `GIT_OPTIONAL_LOCKS=0` (never take `index.lock`), `core.fsmonitor=false`. Hooks fail open (`curl -sf … || true`, 3 s timeout) so a down daemon never blocks an agent. |
| Destructive actions by mistake | Terminate/Kill need `confirm=true` (HTTP 428 otherwise); the UI requires typing the agent's short id; the CLI requires typed confirmation or `--yes`. Pause/resume are reversible. No action deletes files, resets Git state or force-pushes. |
| Claim verification probing the filesystem | The only filesystem access is an existence check of a path an agent claimed to create, change or delete, canonicalized and only inside a worktree that agent used. File contents are never read for it. |
| Undocumented agent internals | Claude Code's per-session messaging sockets and their `.key` token files are not read or used. |
| Automatic actions | Only the runaway guard acts on its own, only when `Guard.AutoPause` is turned on, and only to pause (reversible). It never terminates or kills: automation cannot pass the confirmation for destructive actions. Each automatic action is audited with source `system` and the reason. |
| Audit | Every control action and message is an event (`audit.control`, `agent.message_*`) in the append-only log. |
| Conflict warnings | "Warn agents" sends only a fixed, factual text (agent names, tool, branch, path, line numbers; passed through `Redactor`), never file contents. It needs `confirm=true` (428 otherwise) and the dashboard/CLI show each recipient's exact text first. Automatic warnings are off unless `AutoWarnConflicts` is set. The overlap check runs read-only `git diff`/`merge-base`. The optional committed-state check (`ConflictMergeTree`, off by default) runs `git merge-tree --write-tree`, which adds unreferenced objects to the repository (`git gc` removes them); it is opt-in because Overhive otherwise never writes to repositories. |

## Hooks

`agentctl hooks install` edits `~/.claude/settings.json` only after showing the change and
getting confirmation, writes a timestamped backup first, writes atomically, and only ever
touches entries marked `agent-mission-control`. `agentctl hooks uninstall` removes them.

The hook command is `curl --unix-socket <state dir>/amcd.sock … || true` (shell-quoted
path, fail-open). It carries no token, so nothing secret appears in `settings.json` or in
`ps`. `agentctl --token` (an override flag) does put the token in argv; the default is to
read it from the 0600 token file.

## Remote access

The daemon never listens on anything but `127.0.0.1`. To reach the dashboard or the phone page (`/m`)
from another device, use your own SSH tunnel or `tailscale serve` (TLS by Tailscale, tailnet only); see
[REMOTE_ACCESS.md](REMOTE_ACCESS.md). The page embeds the API token, so anyone who can load it has full
control: expose it only to your own devices. `tailscale serve` needs its host name in `AllowedHosts`; a
browser `Origin` from such a name is accepted only for requests addressed to that same name. Native remote
listening (with TLS and separate opt-in for control actions) remains unsupported.
