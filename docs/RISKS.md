# Risky-action flight recorder

AI coding agents have run `rm -rf ~/`, force-pushed over `main`, wiped directories and read
credential files. Each vendor's sandbox only covers its own tool; Overhive sees the commands of
every tool on the machine. The flight recorder checks each of them against a small set of
explainable rules and lists what it finds in the **Risks** tab, in `overhive risks` and at
`GET /api/risks`. It records; it does not block anything.

## What is checked

| Source | Evidence |
|--------|----------|
| Shell commands an agent reports (`command.started` from hooks, transcripts and the open protocol) | reported |
| Command lines of the agent's child processes that the process collector sees (`/proc`, macOS libproc) | observed |
| File events with absolute paths (writes, deletes, reads) and tools pointed at an absolute path | reported |

Both labels stay visible: one action can appear once as *reported* (the agent said it ran it) and once
as *observed* (Overhive saw the process). Short commands that finish between two process scans (2 s)
are only seen if the agent reports them.

The classifier (`src/Amc.Core/Risk/RiskClassifier.cs`) is pure: it tokenizes the command line
(quotes, `&&`/`||`/`;`/`|` chains, redirections, heredocs, `$(…)`), unwraps `sudo`, `env X=1`,
`nohup`, `timeout`, `xargs`, `bash -c "…"`, `eval '…'`, `pwsh -Command`, `cmd /c`, follows `cd`
inside a chain, and resolves `~`, `$HOME`, `%USERPROFILE%`, `$env:USERPROFILE`, `..` and globs
lexically. It never runs anything, never touches the filesystem and never opens a file.

**Shells.** The command's shell is inferred from its shape. A nested payload is lexed with the right
dialect and classified on its own: `powershell -Command "…"` / `pwsh -c …` and PowerShell positional
scripts (PowerShell quoting: `'…'` literal with `''` escapes, `"…"` with backtick escapes, `;` `|`
`&&` `||` separators), `cmd /c "…"` (cmd quoting, `&` `&&` `||` separators, `^` escapes), and Git
Bash (`bash -c "…"`, the shell Claude Code's Bash tool uses on Windows — its `/c/Users/…` paths map
through `PathText`). `powershell -EncodedCommand <base64>` is decoded from UTF-16LE and classified;
an inline `[Convert]::FromBase64String('…')` fed to `iex` is decoded too. Windows directory
variables (`%SystemRoot%`, `%ProgramFiles%`, `%APPDATA%`, `$env:LOCALAPPDATA` …) expand lexically.

**Worktree.** "Inside" means inside one of the agent's worktrees (or its session's). When the agent is
not in a Git worktree, its working directory stands in, unless that is `/`, the home directory or a
system directory. Without either, only the catastrophic cases (`/`, home, system directories) are
reported. Temporary directories (`/tmp`, `/var/tmp`, macOS `/var/folders`, `%TEMP%`, `$TMPDIR`) and
everything in them (Claude Code scratchpads `/tmp/claude-<uid>/…` included) are never "outside". An agent
whose worktree is itself a scratch checkout in a temp directory may delete the temp directory around it:
that is info, not "a parent of the worktree". The temp root itself (`rm -rf /tmp`), the home directory and
`/` stay critical.

**Variables.** `NAME=literal` assigned earlier in the same command line (also after `export`/`local`) is
substituted, so `S=/tmp/x; rm -rf $S/y` resolves to `/tmp/x/y` and `D=; rm -rf $D/` to `/`. A value from
`$(…)`, a glob or another unknown variable, a loop variable (`for D in …`), `read D` and prefix assignments
(`D=x rm -rf $D/`, where `$D` expands before the assignment applies) stay unknown.

**Long commands.** Risk classification sees the whole command, not the 16K copy every stored event string
is capped to: hook payloads keep the Bash command up to 1 MB (beyond that, the first and last 512K), and the
pipeline hands the redacted, uncapped text to the recorder (never persisted). A dangerous command padded
past 16K is still found. Process command lines from `/proc` are capped at 4 KB by the collector; the cut
last word is dropped rather than read as a path.

## Never reported

- **Overhive's own hooks.** AI tools run our hooks as child processes of the agent: the curl one-liner
  tagged `# agent-mission-control`, the rewake waiter that appends to `rewake.log` in Overhive's state
  directory, and `agentctl hook-forward … --tag agent-mission-control` on Windows. A command is skipped only
  when it is exactly one of those templates (bare or as `sh -c '…'`) with any socket path; a command that
  merely mentions the marker or the state directory (`cat …/token`, `rm -rf ~ # agent-mission-control`, a
  hook line with anything appended) is classified as usual.
- **git's own helpers.** `…/git-core/git reset --hard` started by `git worktree add`, `git stash` and the
  like: the command the agent typed is classified, its internals are not a second action.
- **PowerShell syntax in POSIX text.** `iex (irm …)` or `FromBase64String` inside a heredoc or a string of
  a bash command is data; it is checked when it runs through `pwsh`/`powershell` or on Windows.

## Rules

| Rule id | Severity | Fires on | Not flagged |
|---------|----------|----------|-------------|
| `recursive-delete` | **critical** for `/`, a drive root, the home directory or a parent of it, a parent of the worktree (outside temp), system directories (`/usr`, `/etc`, `C:\Windows` …), the temp root itself, `~/.ssh`, `~/.config`, `~/Documents` …; **warn** for the whole worktree, another worktree, anything else outside the worktree, `"$VAR/"` with an unknown variable (empty → `/`), `..` with unknown cwd, and pattern deletes (`~/*.log`, `find ~ -name … -delete`); **info** for a scratch worktree in a temp dir or the temp directory that contains it | `rm -r/-R/--recursive`, `find … -delete`, `find … -exec rm`, `Remove-Item -Recurse`, `rd /s`, `del /s` | `rm -rf node_modules bin obj dist` and anything else inside the worktree, temp dirs, `rm -rf` without targets, `-WhatIf` |
| `delete-outside-worktree` | **info** for ordinary files under home and one file in an AI tool's data dir; warn for another worktree, files directly in home, dotfiles, `.git/`, outside home; critical in system dirs | `rm file`, `mv` sources, deleted-file events outside the worktree | inside the worktree, temp dirs, `rmdir` (empty dirs only) |
| `write-outside-worktree` | **info** for ordinary files under home (a sibling project, notes, logs); **warn** for files directly in home, home dotfiles and dot-dirs (`~/.bashrc`, `~/.config`, `~/.local/bin`), a repository's `.git/` (hooks), `~/bin`, `~/Library`, `AppData`, PowerShell profiles, anything outside home; **critical** in system dirs | `>`/`>>` redirects, `tee`, `cp`/`mv`/`install`/`ln` destinations, `touch`, `sed -i`, `dd of=`, `Set-Content`/`Out-File`/`Copy-Item`, and file events, outside the worktree | other known worktrees, temp dirs, `/dev/null`, AI tools' own data dirs (`~/.claude`, `~/.codex`, `~/.gemini`, `~/.cursor` …) and caches (`~/.cache`, `~/.npm` …) |
| `git-force-push` | **critical** when the destination is `main`/`master` (explicitly, via `HEAD`, or as the current branch when no refspec is given), lease or not; warn for `-f`/`+refspec`/`--mirror` to another branch; **info** for `--force-with-lease` to another branch and for a bare repository on disk given as a path or `file://` URL | `--force`, `-f`, `--force-with-lease`, `+refspec`, `--mirror` | `--dry-run`/`-n`, a local repository in a temp dir (test fixtures) |
| `git-remote-delete` | warn; critical for `main`/`master` | `git push --delete`, `git push origin :branch` | |
| `git-reset-hard` | warn; **info** when the agent's worktree is known to be clean or the repository (`-C` / cwd) is a scratch clone in a temp dir | `git reset --hard` | `--soft`, `--mixed`, git's own helper processes |
| `git-clean` | warn with `-d`/`-x`; info for `-f` alone and in a temp scratch clone | `git clean -f…` | `-n`, `--dry-run`, `-i` |
| `git-discard-changes` | warn if the worktree has uncommitted changes, info if unknown or in a temp scratch clone | `git checkout -- .`, `git checkout .`, `git checkout -f`, `git restore .` / `:/` | a clean worktree, single files, `git restore --staged .` |
| `git-branch-force-delete` | **info**; critical for `main`/`master`. A local ref only: its commits stay in the reflog and on any remote it was pushed to, and whether it was merged is not knowable without running git | `git branch -D`, `--delete --force` | `git branch -d` (merged only) |
| `chmod-recursive` | world-writable `chmod -R 777` / `a+w` / `o+w`: warn inside the worktree, **critical** outside; other recursive chmod of system/home dirs: warn | `chmod -R` | `chmod -R 755`, non-recursive chmod |
| `chown-recursive` | warn outside the worktree, **critical** on system/home dirs | `chown -R`, `chgrp -R`, and **critical** for `icacls`/`takeown`/`cacls` on a system directory | inside the worktree |
| `pipe-to-shell` | warn; **critical** with `sudo` | `curl … \| sh`, `wget -qO- … \| bash`, `… \| python3`, `bash <(curl …)`, `sh -c "$(curl …)"`, `iex (irm …)`, `irm … \| iex`, `DownloadString` | `curl … \| jq`, `… \| python3 -m json.tool`, `bash -c '…'` |
| `credential-access` | **critical** for `~/.ssh` keys, `~/.aws/credentials` and SSO caches, `~/.config/gcloud`, `~/.azure`, `~/.netrc`, `~/.docker/config.json`, `~/.kube/config`, `~/.gnupg`, `~/.git-credentials`, `~/.config/gh/hosts.yml`, keychains/keyrings, `id_rsa`-style keys anywhere, macOS `security find-*-password`, Windows credential store (`AppData/…/Credentials`); **warn** for `cmdkey /add`/`/generic`/`/delete` (saves or removes stored credentials), `.env` files, `*.pem`/`*.key`/`*.p12`/`*.pfx` outside the worktree, `~/.npmrc`, `~/.pypirc`, the Overhive API token (it only opens the local Overhive API; reading it is how scripts call that API), and for critical paths used by `ssh -i`, `ls`, `chmod` …; **info** for the project's own `.env` / test certificates and for warn-level paths used by `ls`, `stat`, `chmod` … | any command argument, redirect or file event with such a **path** | `known_hosts`, `*.pub`, `.env.example`/`.sample`/`.template`, `echo`/`printf` text, URLs |
| `publish-deploy` | warn | `npm/pnpm/yarn/bun publish`, `cargo publish`, `twine upload`, `gem push`, `dotnet nuget push`, `docker/podman push`, `terraform/tofu apply/destroy`, `pulumi up/destroy`, `cdk deploy/destroy`, `kubectl delete`, `helm uninstall`, `firebase/fly/wrangler deploy`, `vercel --prod` … | `--dry-run`, `terraform plan`, builds |
| `database-drop` | warn | `DROP DATABASE/SCHEMA/TABLE` given to a database client (argument, heredoc or pipe), `dropdb`, `mysqladmin drop`, `rails db:drop/db:reset`, `prisma migrate reset`, `dotnet ef database drop`, MongoDB `dropDatabase()` | `grep 'DROP TABLE'`, SQL written to a file |
| `encoded-command` | warn when it cannot be decoded; **info** when it decodes (the decoded script is then classified by every rule) | `powershell -EncodedCommand <base64>` (decoded from UTF-16LE), `iex ([Convert]::FromBase64String('…'))` and other Base64 payloads run through `iex`/`Invoke-Expression`/`[scriptblock]::Create` | plain `-Command`, Base64 that is not run as code |
| `disk-wipe` | **critical** for `format`, `Format-Volume`, `Clear-Disk`, `cipher /w`; warn for `diskpart`, `sdelete` | formatting or wiping a volume, overwriting free space | `format` mentioned in quoted text, `format /?` |
| `execution-policy` | warn | `Set-ExecutionPolicy Unrestricted` / `Bypass` | `Restricted`/`AllSigned`/`RemoteSigned` |
| `registry-edit` | warn | `reg add/delete/import/copy/load/restore` and `Set-/New-/Remove-ItemProperty`, `New-/Remove-/Set-Item` under `HKLM`/`HKEY_LOCAL_MACHINE`/`HKU` | `HKCU` (current user) edits, `reg query` |
| `elevation` | warn | `Start-Process -Verb RunAs` (relaunch elevated) | running without `-Verb RunAs` |
| `scheduled-task` | warn | `schtasks /create`, `Register-ScheduledTask`, `Set-ScheduledTask` (persistence) | `schtasks /query`, `/run` |
| `account-change` | warn | `net user/localgroup /add` & `/delete`, `New-/Remove-LocalUser`, `New-LocalGroup`, `Add-LocalGroupMember` | `net user` (listing), `Get-LocalUser` |
| `system-power` | warn | `shutdown /s` / `/r` …, `Stop-Computer`, `Restart-Computer` | `shutdown /a` (abort), `shutdown` with no action |
| `security-disable` | warn | `Add-MpPreference -Exclusion…`, `Set-MpPreference -DisableRealtimeMonitoring $true`, `netsh … firewall` off/disable | re-enabling (`$false`), `Get-MpPreference` |

## Privacy

- Credential files are recognised **by path only**. Overhive never opens, reads or hashes them.
- Commands are redacted (`Redactor`) before they are stored, like every other event field; findings
  keep the redacted command and the matched part.
- Everything is local: no network, no shell, no new dependency.

## Storage and dedupe

Each finding is a `risk.detected` event (history retention, 180 days by default). One finding is
kept per agent + rule + evidence + resolved target path (or matched command when there is no
path), so a repeated command is recorded once. The state keeps the newest 100 findings per agent
and 2000 overall, each for 14 days (`StateRetention.HistoryDays`); the per-agent counters
(`agent.risks.info/warn/critical`) count every finding. Older findings stay in the event log.

## Limitations

- Lexical only: symlinks, variables other than home/temp/`PWD`, aliases, shell functions and
  scripts (`./cleanup.sh`) are not followed; `find … | xargs rm -rf` targets come from stdin and
  are unknown.
- Base64 PowerShell is decoded only for `-EncodedCommand` (UTF-16LE) and for a literal passed to
  `[Convert]::FromBase64String('…')` run through `iex`; payloads assembled or decoded at run time
  (from a variable, a file, or string concatenation) are flagged as `encoded-command` but not read.
- Commands that live less than one process scan and are not reported by a hook are not seen.
- Severity is a judgement: `rm -rf` of a sibling project or a `.env` read may be intended.
- Events that were stored capped before full-command classification (or arrive capped from elsewhere)
  are classified without their partial first line, but the rest of a cut heredoc can still read as
  commands.

## Measured noise

Replaying a month of real findings (386 from 265 distinct commands, reconstructed context) through the
classifier before and after the noise fixes: critical 11 → 2, warn 352 → 23, info 23 → 196. What is still
flagged: `.env` and cloud-credential reads outside the worktree, the Overhive token read, `git reset
--hard` with uncommitted changes, `git checkout -- .` in a dirty worktree, `terraform apply`, `sudo tee
/proc/sys/…`, a deletion inside another worktree, an accidental `>` redirect into the home directory, and
heredoc text from commands that had been stored capped (live, the full command is classified now).
