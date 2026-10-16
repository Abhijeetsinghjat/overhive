# Attention queue and notifications

When many agents run at once, the hard part is knowing which one is waiting for you. Overhive keeps one
**"Needs you" queue** across every tool and can tell you about new items. Everything is local; nothing is
sent anywhere unless you configure your own ntfy server.

## The queue

| Item | Priority | Evidence |
|------|----------|----------|
| Agent blocked: permission prompt or notification | urgent | from the agent's status (usually reported) |
| Session failed (sub-agent failed: normal), last 24 h | urgent | from the status |
| Git: unmerged files / merge or rebase in progress | urgent | observed |
| Session finished its turn and waits for your next prompt (not while its sub-agents work) | normal | from the status |
| Session exited unexpectedly, last 24 h | normal | usually observed |
| Conflicts between agents (same file, same worktree, same path in several worktrees) | normal | observed / inferred |
| Session finished, last hour | low | from the status |

Ordered by priority, then the oldest first. The header badge (**⚑**) counts urgent items; click it or press
**`a`** for the whole queue. The Overview panel ("Needs you") shows the same list. Each item has its main
action (open the agent, open its terminal to answer a prompt, message it, or open the conflict), plus:

* **Snooze** (15 min – 1 day): hidden, then it comes back and notifies again.
* **Dismiss**: hidden until the situation happens again (a new prompt, a new failure).

Dismiss and snooze are kept by the daemon (in `attention-marks.json` in the data directory), so they apply
to the dashboard, the phone page, `overhive attention` and notifications at once.

Overhive cannot approve a permission prompt for you: the tools do not offer a safe way to do that from
outside. For sessions started with **+ New session** the item opens the live terminal, where you answer.

From the command line: `overhive attention` (`--all` includes snoozed/dismissed, `--json` for scripts).
API: `GET /api/attention[?all=true]`, `POST /api/attention/dismiss|snooze|restore` with `{"id", "minutes"?}`.

## Notifications (all opt-in)

New items notify once (by their stable id). A burst is combined into one notification, at most one per
minute and 20 per hour per channel. Items already there when the daemon starts do not notify. Notifications
carry titles only (agent name, kind), never details, and are redacted.

**Browser.** In the queue panel click **Enable notifications** (the browser asks for permission only then).
Notifications show for new urgent items while the dashboard tab is in the background. **Mute** pauses them;
**Also when waiting** adds sessions that wait for their next prompt.

**Desktop** (from the daemon, when no dashboard is open). Linux uses `notify-send` (package `libnotify-bin`
on Debian/Ubuntu), macOS uses `osascript`. In `~/.config/agent-mission-control/config.json`:

```json
{ "Notifications": { "Desktop": true } }
```

**Phone push through your own ntfy server.** If you run [ntfy](https://ntfy.sh) yourself, Overhive can
POST a short message to your topic. This is the **only outbound network call** Overhive can make, and only
when you set it. Use HTTPS, a hard-to-guess topic, and an access token:

```json
{ "Notifications": { "NtfyUrl": "https://ntfy.example.org/overhive-k3j9x2", "NtfyTokenFile": "/home/me/.config/agent-mission-control/ntfy-token" } }
```

One POST per notification, 5 s timeout, no retries, no redirects; after 3 failures in a row it pauses 10
minutes. ntfy notifies even while a dashboard is open (you may be away from the desk).

| Option (`Notifications.*`) | Default | Meaning |
|--------|---------|---------|
| `Desktop` | `false` | Desktop notifications while no dashboard is connected |
| `NtfyUrl` | empty (off) | Full topic URL on your own ntfy server; no credentials in the URL |
| `NtfyTokenFile` | empty | File with an ntfy access token (sent as `Authorization: Bearer`); keep it `0600` |
| `MinPriority` | `urgent` | `normal` also notifies when a session waits for its next prompt |
| `CoalesceSeconds` | `5` | Items within this window go out as one notification |
| `MinIntervalSeconds` | `60` | Minimum time between notifications per channel |
| `MaxPerHour` | `20` | Cap per channel |

The Health tab shows a `notifications` component (channels, last send, failures).

## For contributors: adding items

Items come from `IAttentionProvider`s (`src/Amc.Core/Attention`). To add a source, implement
`Collect(WorldState, AttentionContext)` and register it in the daemon with one line:

```csharp
services.AddSingleton<IAttentionProvider, MyProvider>();
```

Give each item a stable `Id` per occurrence (e.g. `"<kind>:<entity>:<since-ms>"`), a `Kind`, `Priority`,
`Evidence`, `Since` and an `Action`. Ordering, de-duplication, redaction, capping, dismiss/snooze, the UI and
notifications apply automatically. A provider that throws is skipped and logged.
