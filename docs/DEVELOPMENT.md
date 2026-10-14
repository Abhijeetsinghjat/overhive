# Development

## Requirements

.NET 10 SDK, git, curl (for the hook command), Linux (`/proc`, inotify, PTYs). No Node.js:
the UI is plain ES modules with no build step.

## Layout

```
src/Amc.Core        domain, events, Reducer (pure fold), ConflictDetector, parsers, Redactor,
                    IAgentProvider, ClaudeHooksConfig. No IO.
src/Amc.Daemon      amcd: Storage (SQLite), Pipeline (ingest → persist → reduce → broadcast,
                    StalenessMonitor), Api (REST, WebSocket, security), Collectors/Git,
                    Collectors/Process, Terminals, Providers/Claude, Messaging, wwwroot (UI)
src/Amc.Cli         agentctl: Commands/, Infrastructure/, Hooks/, Run/ (PTY host), Native/
tests/Amc.Tests     Core/, Pipeline/, Git/, Process/, Terminals/, Claude/, Cli/, E2E/, Ui/
scripts/            install.sh, launcher.sh
```

## Build, test, run

```bash
dotnet build
dotnet test                                   # all tests (unit, integration, E2E)
dotnet test --filter "Category=E2E"           # the end-to-end fake-agent lifecycle
AMC_DataDir=/tmp/amc AMC_Port=47900 dotnet run --project src/Amc.Daemon   # isolated dev instance
```

Configuration: `~/.config/agent-mission-control/config.json`, environment variables
prefixed `AMC_` (for example `AMC_Port`, `AMC_DataDir`, `AMC_ClaudeHome`,
`AMC_WorkspaceRoots__0`, `AMC_BackfillHours`), or command-line `--Key=value`. See
`AmcOptions`.

UI fixture (simulated data, clearly labelled in the UI):
`AMC_FIXTURE_DIR=/tmp/amc-fixture dotnet test --filter Category=DevFixture`, then run the
daemon with `AMC_DataDir=/tmp/amc-fixture`.

## Rules of the codebase

* **The event log is the only source of truth.** State changes only through
  `Reducer.Apply`. If you change folding semantics, bump `Reducer.Version` so persisted
  snapshots are discarded and the log is replayed.
* **Evidence on everything.** Set `Evidence` honestly on every event you publish.
* **No shells.** Use `ProcessStartInfo.ArgumentList`. Git runs with `GIT_OPTIONAL_LOCKS=0`.
* **Bounded everything:** queues, buffers, text fields, collections in state.
* Read `WorldState` only inside `StateStore.Read`, and serialize inside it too.
* Dedupe keys for facts that can arrive from more than one collector (EVENT_MODEL.md).
* Collectors register health in `HealthRegistry` with a stale interval.

## How this was built

Discovery, architecture and the core (domain, reducer, storage, pipeline, API) were
written first on `main`. Collectors were then built in parallel by sub-agents, each in
its own git worktree and branch (`agent/git`, `agent/claude`, `agent/process`,
`agent/cli`, `agent/ui` under `../agent-mission-control-worktrees/`), and merged after
review. The dashboard displays that development itself: each implementation agent, its
worktree and its files.

## Releasing

CI (`.github/workflows/ci.yml`) builds and tests every push to `main` and every pull request.
To publish a release, tag a commit and push the tag:

```bash
git tag v0.1.0 && git push origin v0.1.0
```

`.github/workflows/release.yml` then runs the tests, builds the self-contained `linux-x64` and
`linux-arm64` archives with `scripts/package.sh`, writes `SHA256SUMS`, and creates the GitHub release.
Everything runs on GitHub's free hosted runners; no secrets beyond the built-in `GITHUB_TOKEN` are needed.
