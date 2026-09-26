# `Assimalign.Cohesion.Logging.EventSourceForwardingOptions`

Selects which event sources `ILoggerFactory.ForwardEventSources` forwards.

## Constructors

```csharp
public EventSourceForwardingOptions()
```

Creates options whose `Sources` list holds `CohesionSourcePrefix`.

## Members

| Member | Description |
| --- | --- |
| `const string CohesionSourcePrefix` | `"Assimalign.Cohesion."` — the prefix every Cohesion event source name starts with, because each is named for its assembly. |
| `IList<string> Sources { get; }` | Name prefixes, matched case-insensitively against the start of the event source name. Defaults to `[CohesionSourcePrefix]`. |

There is deliberately no level setting: each source is enabled at the most verbose level the target
factory accepts for the source's category, so logger filter rules remain the one place verbosity is set.

## Usage

```csharp
// Add a runtime source alongside the Cohesion default.
var withTls = new EventSourceForwardingOptions { Sources = { "System.Net.Security" } };

// Forward only one Cohesion family.
var connectionsOnly = new EventSourceForwardingOptions();
connectionsOnly.Sources.Clear();
connectionsOnly.Sources.Add("Assimalign.Cohesion.Connections");
```
