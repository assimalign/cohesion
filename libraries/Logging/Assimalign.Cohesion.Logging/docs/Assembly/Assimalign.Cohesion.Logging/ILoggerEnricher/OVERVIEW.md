# `Assimalign.Cohesion.Logging.ILoggerEnricher`

Adds attributes to every log entry before fan-out. Examples: machine identity, current
trace / span ids, environment name, runtime version.

## Members

```csharp
string Name { get; }
void Enrich(ILoggerEntry entry, IDictionary<string, object?> attributes);
```

- `Name` identifies the enricher in `ILoggerFactory.Enrichers`. Names are unique within a builder
  (case-insensitive; `null` counts as empty): `AddEnricher` throws `InvalidOperationException` for a
  second enricher with the same name.

- `entry` is the entry being enriched (immutable; provided for context).
- `attributes` is a mutable view on the attribute bag. New keys are added to the entry's
  final attribute payload.

## Rules

- Enrichers run in registration order.
- Enrichers MUST NOT overwrite keys supplied by the entry author; assignments through the
  mutable view to an existing key are silently dropped. This guarantees caller intent wins.
- Implementations must be thread-safe. Enrichment runs on the caller's thread; long-running
  or blocking work belongs elsewhere.
- Exceptions thrown by an enricher are swallowed; the entry still ships.

## Example

```csharp
internal sealed class ActivityEnricher : ILoggerEnricher
{
    public string Name => "Activity";

    public void Enrich(ILoggerEntry entry, IDictionary<string, object?> attributes)
    {
        if (System.Diagnostics.Activity.Current is { } activity)
        {
            attributes["trace.id"] = activity.TraceId.ToString();
            attributes["span.id"] = activity.SpanId.ToString();
        }
    }
}
```
