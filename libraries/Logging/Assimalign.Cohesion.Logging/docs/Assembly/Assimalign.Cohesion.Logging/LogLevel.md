# `Assimalign.Cohesion.Logging.LogLevel`

Severity tag attached to every log entry.

| Value | Ordinal | Description |
| --- | --- | --- |
| `Trace` | 0 | Deep diagnostic detail; may carry sensitive data; disabled by default. |
| `Debug` | 1 | Interactive-investigation messages; no long-term value. |
| `Information` | 2 | General application flow. Default minimum level. |
| `Warning` | 3 | Unexpected event, application continues. |
| `Error` | 4 | Failure in the current activity. |
| `Critical` | 5 | Unrecoverable failure; immediate attention. |
| `Event` | 6 | Audit / domain event not tied to severity. Treated as enabled when the factory minimum is `Information` or below. |
| `None` | 7 | Filter sentinel; `IsEnabled(None)` always returns `false`. |

The composite logger compares against the factory's `MinimumLevel` (and any category-prefix
filter override) using ordinal comparison. `Event` is intentionally higher than `Critical`
ordinally so it can be selectively filtered; it is not considered more severe than
`Critical`.
