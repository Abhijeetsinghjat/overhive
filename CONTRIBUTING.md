# Contributing to Overhive

Thanks for helping. Overhive is free and open source (Apache-2.0).

## Ground rules

- **No paid dependencies or services.** Only open-source components under permissive licences (MIT,
  Apache-2.0, BSD, public domain) that work offline. List new ones in [docs/DEPENDENCIES.md](docs/DEPENDENCIES.md).
- **Never handle AI credentials.** Tools sign in with their users' own accounts; Overhive only observes.
- **Evidence over guesses.** Every fact shown carries `observed`, `reported` or `inferred`; don't display
  "working" or attribute a change without evidence.
- **Vendor-neutral.** Support for one tool must not make the product specific to it.

## Build and test

```bash
dotnet build
dotnet test                         # unit, integration and end-to-end tests
AMC_DataDir=/tmp/ovh AMC_Port=47900 dotnet run --project src/Amc.Daemon   # isolated dev instance
```

See [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) for the layout and rules of the codebase.

## Adding a tool

1. Add it to the catalog in `src/Amc.Core/Domain/AgentTools.cs` (or users can add it in `config.json`).
2. If it has documented hooks, add a profile in `src/Amc.Core/Providers/ToolHooksConfig.cs` and map its
   event and field names in `src/Amc.Daemon/Tools/ToolHookReceiver.cs`, with tests using payloads from
   its official documentation.
3. Document the setup step in the README table.

## Pull requests

Keep changes focused, include tests, and describe how you verified the change. By contributing you agree
that your contribution is licensed under the Apache License 2.0.
