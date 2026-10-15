# Runaway guard

An agent that keeps spawning sub-agents, or sub-agents of sub-agents, can use up days of token
allowance in minutes. Overhive already sees the sub-agent tree and the tokens each agent reports, so it
watches every session (a main agent with all its sub-agents) and warns when one runs away. It can also
pause the session for you, if you turn that on.

## Rules

Checked every 15 seconds for each session that is active or had activity in the last hour.

| Rule | Default warn | Default critical | Counts |
|------|-------------:|-----------------:|--------|
| Sub-agents at once | 12 | 30 | Sub-agents that are starting, running, waiting or blocked |
| Spawn rate | 30 | 80 | Sub-agents created in the last 10 minutes |
| Tree depth | 4 | 6 | Deepest level of active or recently created sub-agents (a direct sub-agent is level 1) |
| Token burn rate | 400k/min | 1M/min | Average over the last 5 minutes |
| Tokens in the last hour | 12M | 30M | Whole session |
| Session token budget | off | off | Whole session, all time; warns at 80 % of the budget |

Tokens are input + output + cache writes, as reported by the tool. Cache reads are not counted: they are
cheap re-reads of context and would drown the signal. Agent counts come from the sub-agent tree the tool
reports (evidence: reported); token counts are reported by the tool.

Each crossing raises one alert. It is raised again only if it changes level (warn → critical). It clears
when the value falls below 90 % of the threshold, so a value hovering at the threshold does not flap.
Alerts are events (`guard.alert`, `guard.cleared`, `guard.dismissed`) in the timeline and survive restarts.

## Where you see it

- A red **Runaway guard** banner under the header, and a **⚠ runaway** badge in the header.
- The agent panel of any agent in that session: the alerts, the live values, **Pause tree** and **Dismiss**.
- The session's card in the Graph tab: depth, active and new sub-agents, tokens per minute.
- `overhive guard` (or `--json`), and `GET /api/guard`.

**Dismiss** hides an alert from the banner until it clears, or until it becomes critical.
**Pause tree** pauses the session process (SIGSTOP on its process group, which includes sub-agents running
inside it) and any sub-agent that has its own process. Resume it from the agent panel or with
`overhive resume <agent>`.

## Automatic pause (off by default)

With `AutoPause` on, a **new critical** alert pauses the session tree through the same control path as the
Pause button. The audit log records it as automatic, with the rule and value that triggered it
(`audit.control`, source `system`). It happens once per alert: if you resume the session while the alert is
still critical, the guard does not pause it again. The guard never terminates or kills an agent, and does
nothing when `EnableControl` is false. A tool whose process Overhive cannot signal is not paused; the skipped
attempt is audited.

## Configuration

In `~/.config/agent-mission-control/config.json` (restart `amcd` after changing it):

```json
{
  "Guard": {
    "AutoPause": true,
    "SessionTokenBudget": 5000000,
    "ActiveSubAgentsWarn": 12, "ActiveSubAgentsCritical": 30,
    "SpawnedWarn": 30, "SpawnedCritical": 80, "SpawnWindowMinutes": 10,
    "DepthWarn": 4, "DepthCritical": 6,
    "TokensPerMinuteWarn": 400000, "TokensPerMinuteCritical": 1000000, "TokenRateWindowMinutes": 5,
    "TokensPerHourWarn": 12000000, "TokensPerHourCritical": 30000000,
    "BudgetWarnPercent": 80, "IntervalSeconds": 15, "Enabled": true
  }
}
```

or environment variables such as `AMC_Guard__AutoPause=true`. A threshold of `0` turns that level off.
`"Enabled": false` turns the guard off and clears its alerts.
