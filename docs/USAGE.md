# Usage and estimated API cost

The **Usage** tab and `overhive usage` show how many tokens your agents use, per session (with its sub-agents),
per agent, per model and over time, plus what those tokens would cost at the provider's published **API list
prices**.

The estimate answers "what would this work cost if I paid per token?". It is **not** what you pay on a
subscription plan (Claude Pro/Max, ChatGPT Plus/Pro, Gemini, Copilot): those charge a flat fee. Overhive never
connects to a provider's billing, and it never reads or stores your AI account credentials.

## Where the numbers come from

| What | Source | Evidence label |
|---|---|---|
| Token counts | What the tool reports on every model response: Claude Code transcripts, or the [open protocol](PROTOCOL.md) `usage` report for any other tool | **reported** |
| Estimated cost | Overhive's calculation: each response's tokens × the model's list price | **inferred** |
| Usage over time | The event log Overhive already keeps (`HistoryRetentionDays`, 180 days by default). No extra database or service. | |

Each response is priced on its own, so the details that change a price are applied per response:

- **Cache writes** at the 5-minute or 1-hour rate, depending on what the response recorded (Claude Code uses
  1-hour caching; the transcript says how many tokens were written with each lifetime).
- **Long prompts**: Claude Haiku 5.5 charges more when a prompt (input + cache writes + cache reads) is over
  100,000 tokens.
- **Fast mode** (2× on the models that offer it) and **US-only inference** (1.1× on Claude 4.6 and later).

Usage recorded **before this version** did not keep the cache lifetime. Those cache writes are priced at the
5-minute rate, and the dashboard and CLI say the figure is a **lower bound** (if they were 1-hour writes, the
real figure is higher). Usage recorded from now on has the lifetime and is priced exactly.

A model without a price is **missing, not zero**: its tokens are left out of the cost, the row says
"not priced", and the total is marked with a **+**.

## Built-in prices

Anthropic's first-party API list prices (USD per million tokens) are built in, taken from
<https://platform.claude.com/docs/en/about-claude/pricing> on **2026-11-11**. Dated, Bedrock and Vertex
spellings of a model id (`claude-haiku-4-5-20251001`, `us.anthropic.claude-opus-4-8-v1:0`,
`claude-opus-4-5@20251101`) use the same entry. `GET /api/pricing` lists the table in use.

Other vendors' prices are not shipped: they change often and Overhive has no reliable source to keep them
current. Add the models you use yourself (next section).

Partner platforms (Amazon Bedrock, Google Vertex AI) and batch requests have their own prices; the estimate
always uses the first-party API price.

## Your own prices

Add or override models in `~/.config/agent-mission-control/config.json`, then restart `amcd`. Prices are per
million tokens. A key ending in `*` matches every model id with that prefix (the longest match wins). An entry
replaces the built-in one with the same key completely.

```json
{
  "Pricing": {
    "Models": {
      "gpt-5-codex":     { "Input": 1.25, "Output": 10, "CacheRead": 0.125 },
      "gemini-2.5-pro*": { "Input": 1.25, "Output": 10, "CacheRead": 0.31 }
    }
  }
}
```

| Field | Meaning |
|---|---|
| `Input`, `Output` | Price per million input / output tokens (required) |
| `CacheWrite`, `CacheWrite1h` | Cache write with the 5-minute / 1-hour lifetime (default: `Input`; `CacheWrite1h` defaults to `CacheWrite`) |
| `CacheRead` | Cache read (default: `Input`) |
| `LongContextAbove`, `LongContext` | Prompts above this many tokens use the prices in `LongContext` (an object with the same fields) |
| `FastMultiplier`, `UsOnlyMultiplier` | Multipliers for responses reported with `"speed": "fast"` / `"geo": "us"` |

Other settings: `"Pricing": { "Enabled": false }` hides every cost (tokens are still shown). `"Currency"` is
only a label; change it together with your own prices for every model you use.

## Dashboard

- **Usage** tab: **Est. API cost** tile, an **Est. cost** column per session (with sub-agents) and per model.
- **Over time**: stacked bars per hour (last 48 hours) or per day (7, 30 or 90 days, or everything kept),
  one color per model (the five most expensive models; the rest are "Other"). Switch between **Est. cost** and
  **Tokens**, and between **Chart** and **Table**. Hover or focus a bar for every model's value. Days and hours
  are in your browser's time zone.

## CLI

```bash
overhive usage                       # per session, with EST. COST
overhive usage --agents              # also each sub-agent
overhive usage --history             # per day, last 30 days: cost, tokens, responses, top model
overhive usage --history --by hour   # per hour, last 48 hours
overhive usage --history --range 90d --json
```

## API

```
GET /api/usage/report?range=48h|7d|30d|90d|all&bucket=hour|day&tz=<IANA zone>
GET /api/pricing
```

The report holds totals, one bucket per hour or day (per model), per-model and per-agent totals, the models
without a price, and `cacheWriteUnknownTtl` (cache-write tokens priced at the 5-minute rate because their
lifetime was not recorded). Reports are computed from the event log on request and cached for 30 seconds.

## Reporting cost details from other tools

The open protocol's `usage` report accepts three optional fields that refine the estimate:
`"cacheWrite1h"` (the part of `cacheWrite` written with a 1-hour lifetime), `"speed": "fast"` and
`"geo": "us"`. See [PROTOCOL.md](PROTOCOL.md).
