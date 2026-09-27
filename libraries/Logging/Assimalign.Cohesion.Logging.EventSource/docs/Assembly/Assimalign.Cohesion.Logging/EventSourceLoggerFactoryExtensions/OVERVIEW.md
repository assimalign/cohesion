# `Assimalign.Cohesion.Logging.EventSourceLoggerFactoryExtensions`

Extension members on `ILoggerFactory` that forward `System.Diagnostics.Tracing` events into the
factory's loggers. Declared with C# 14 `extension(ILoggerFactory loggerFactory)` syntax.

## Members

```csharp
public ILoggerForwarder ForwardEventSources(EventSourceForwardingOptions? options = null)
```

Starts forwarding every event source whose name starts with one of `options.Sources` (default:
`Assimalign.Cohesion.`), including sources created later. Returns the forwarder; disposing it stops
forwarding and disables the sources it enabled. Disposing it twice is a no-op. The caller owns it and
must dispose it before the factory — when the factory is built from a builder, prefer
`AddEventSourceForwarding` (`EventSourceLoggerFactoryBuilderExtensions`), which makes the factory the
owner.

| Parameter | Description |
| --- | --- |
| `loggerFactory` | The receiver: the factory whose loggers receive the entries. |
| `options` | The sources to forward, or `null` for every Cohesion source. |

## Exceptions

| Exception | When |
| --- | --- |
| `ArgumentNullException` | `loggerFactory` is `null`. |
| `ArgumentException` | `options.Sources` is empty, or contains a `null`, empty, or whitespace prefix. |

## Behavior

- Each source is enabled at the most verbose level its category's logger accepts, and not at all when the
  logger accepts nothing.
- Each event becomes one entry: category = source name, message = formatted template (or event name),
  attributes = payload + `EventId`, `EventName`, `ActivityId` (when non-empty).
- Levels: Critical, Error, Warning → namesakes; Informational and LogAlways → `Information`; Verbose →
  `Debug`; event 0 (`EventSourceMessage`) → `Warning`.
- Events are written synchronously on the raising thread; an event raised while that thread is already
  forwarding is dropped; nothing a sink throws reaches the raising code; `EventCounters` payloads are
  skipped.

## Example

```csharp
using ILoggerFactory loggerFactory = new LoggerFactoryBuilder()
    .AddProvider(new ConsoleLoggerProvider())
    .AddRule("Assimalign.Cohesion.Connections", LogLevel.Debug)
    .Build();

using ILoggerForwarder forwarding = loggerFactory.ForwardEventSources();
```
