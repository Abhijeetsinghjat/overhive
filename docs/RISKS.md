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
reported. Temporary directories (`/tmp`, `/var/tmp`, macOS `/var/folders`, `%TEMP%`, `$TMPDIR`) are
never "outside".

## Rules

| Rule id | Severity | Fires on | Not flagged |
|---------|----------|----------|-------------|
| `recursive-delete` | **critical** for `/`, a drive root, the home directory or a parent of it, a parent of the worktree, system directories (`/usr`, `/etc`, `C:\Windows` …), `~/.ssh`, `~/.config`, `~/Documents` …; **warn** for the whole worktree, another worktree, anything else outside the worktree, `"$VAR/"` with an unknown variable (empty → `/`), `..` with unknown cwd, and pattern deletes (`~/*.log`, `find ~ -name … -delete`) | `rm -r/-R/--recursive`, `find … -delete`, `find … -exec rm`, `Remove-Item -Recurse`, `rd /s`, `del /s` | `rm -rf node_modules bin obj dist` and anything else inside the worktree, temp dirs, `rm -rf` without targets, `-WhatIf` |
| `delete-outside-worktree` | warn (critical in system dirs) | `rm file`, `mv` sources, deleted-file events outside the worktree | inside the worktree, temp dirs |
| `write-outside-worktree` | warn (critical in system dirs) | `>`/`>>` redirects, `tee`, `cp`/`mv`/`install`/`ln` destinations, `touch`, `sed -i`, `dd of=`, `Set-Content`/`Out-File`/`Copy-Item`, and file events, outside the worktree | other known worktrees, temp dirs, `/dev/null`, AI tools' own data dirs (`~/.claude`, `~/.codex`, `~/.gemini`, `~/.cursor` …) and caches (`~/.cache`, `~/.npm` …) |
| `git-force-push` | warn; **critical** when the destination is `main`/`master` (explicitly, via `HEAD`, or as the current branch when no refspec is given) | `--force`, `-f`, `--force-with-lease`, `+refspec`, `--mirror` | `--dry-run`/`-n` |
| `git-remote-delete` | warn; critical for `main`/`master` | `git push --delete`, `git push origin :branch` | |
| `git-reset-hard` | warn | `git reset --hard` | `--soft`, `--mixed` |
| `git-clean` | warn with `-d`/`-x`; info for `-f` alone | `git clean -f…` | `-n`, `--dry-run`, `-i` |
| `git-discard-changes` | warn if the worktree has uncommitted changes, info if unknown | `git checkout -- .`, `git checkout .`, `git checkout -f`, `git restore .` / `:/` | a clean worktree, single files, `git restore --staged .` |
| `git-branch-force-delete` | warn; critical for `main`/`master` | `git branch -D`, `--delete --force` | `git branch -d` (merged only) |
| `chmod-recursive` | world-writable `chmod -R 777` / `a+w` / `o+w`: warn inside the worktree, **critical** outside; other recursive chmod of system/home dirs: warn | `chmod -R` | `chmod -R 755`, non-recursive chmod |
| `chown-recursive` | warn outside the worktree, **critical** on system/home dirs | `chown -R`, `chgrp -R`, and **critical** for `icacls`/`takeown`/`cacls` on a system directory | inside the worktree |
| `pipe-to-shell` | warn; **critical** with `sudo` | `curl … \| sh`, `wget -qO- … \| bash`, `… \| python3`, `bash <(curl …)`, `sh -c "$(curl …)"`, `iex (irm …)`, `irm … \| iex`, `DownloadString` | `curl … \| jq`, `… \| python3 -m json.tool`, `bash -c '…'` |
| `credential-access` | **critical** for `~/.ssh` keys, `~/.aws/credentials` and SSO caches, `~/.config/gcloud`, `~/.azure`, `~/.netrc`, `~/.docker/config.json`, `~/.kube/config`, `~/.gnupg`, `~/.git-credentials`, `~/.config/gh/hosts.yml`, keychains/keyrings, `id_rsa`-style keys anywhere, macOS `security find-*-password`, Windows credential store (`AppData/…/Credentials`); **warn** for `cmdkey /add`/`/generic`/`/delete` (saves or removes stored credentials), `.env` files, `*.pem`/`*.key`/`*.p12`/`*.pfx` outside the worktree, `~/.npmrc`, `~/.pypirc`, and for critical paths used by `ssh -i`, `ls`, `chmod` …; **info** for the project's own `.env` / test certificates | any command argument, redirect or file event with such a **path** | `known_hosts`, `*.pub`, `.env.example`/`.sample`/`.template`, `echo`/`printf` text, URLs |
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
and 2000 overall; the per-agent counters (`agent.risks.info/warn/critical`) count every finding.

## Limitations

- Lexical only: symlinks, variables other than home/temp/`PWD`, aliases, shell functions and
  scripts (`./cleanup.sh`) are not followed; `find … | xargs rm -rf` targets come from stdin and
  are unknown.
- Base64 PowerShell is decoded only for `-EncodedCommand` (UTF-16LE) and for a literal passed to
  `[Convert]::FromBase64String('…')` run through `iex`; payloads assembled or decoded at run time
  (from a variable, a file, or string concatenation) are flagged as `encoded-command` but not read.
- Commands that live less than one process scan and are not reported by a hook are not seen.
- Severity is a judgement: `rm -rf` of a sibling project or a `.env` read may be intended.
