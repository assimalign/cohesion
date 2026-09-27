# `Assimalign.Cohesion.Logging.EventSourceLoggerFactoryBuilderExtensions`

Extension members on `ILoggerFactoryBuilder` that register `System.Diagnostics.Tracing` forwarding, so
the built factory owns it. Declared with C# 14 `extension(ILoggerFactoryBuilder builder)` syntax.

## Members

```csharp
public ILoggerFactoryBuilder AddEventSourceForwarding(EventSourceForwardingOptions? options = null)
```

Registers a forwarder through `ILoggerFactoryBuilder.AddForwarder`:

- The prefixes in `options.Sources` (default: `Assimalign.Cohesion.`) are validated and copied when this
  method is called. Changing the options later has no effect.
- The factory creates the forwarder as the last step of `Build()`. Forwarding covers every event raised
  from then on, including events from sources created later.
- The factory disposes the forwarder before its providers. There is no handle for the caller to hold.

| Parameter | Description |
| --- | --- |
| `builder` | The receiver: the builder whose factory receives the entries. |
| `options` | The sources to forward, or `null` for every Cohesion source. |

Returns the builder, for chaining.

## Exceptions

| Exception | When |
| --- | --- |
| `ArgumentNullException` | `builder` is `null`. |
| `ArgumentException` | `options.Sources` is empty, or contains a `null`, empty, or whitespace prefix. |
| `InvalidOperationException` | The builder has already built a factory. |

## Behavior

The level rules, entry shape, and failure model are those of `ForwardEventSources`
(`EventSourceLoggerFactoryExtensions`).

The forwarder is listed in `ILoggerFactory.Forwarders` under the name `EventSource`. Forwarder names are
unique within a factory, so registering this verb twice makes `Build()` throw
`InvalidOperationException`. Put every prefix in one registration.

## Example

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Logging
    .AddProvider(new ConsoleLoggerProvider())
    .AddRule("Assimalign.Cohesion.Connections", LogLevel.Debug)
    .AddEventSourceForwarding();
```
