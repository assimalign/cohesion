# `Assimalign.Cohesion.Logging.ILoggerForwarder`

A component that writes entries *into* a logger factory from a source outside the logging pipeline, such
as runtime event sources. A provider is a sink the factory writes to; a forwarder is the reverse.

```csharp
public interface ILoggerForwarder : IDisposable
{
    string Name { get; }
}
```

`Name` identifies the forwarder in `ILoggerFactory.Forwarders`. Names are unique within a factory
(case-insensitive; `null` counts as empty). Names are only known once forwarders exist, so the factory
checks them as it creates them: a duplicate fails construction like any other failed registration.

## Lifetime

- **Registration.** Register a forwarder with `ILoggerFactoryBuilder.AddForwarder(Func<ILoggerFactory,
  ILoggerForwarder>)`, or add it directly to `LoggerFactoryOptions.Forwarders`.
- **Creation.** The factory calls each registration once, in registration order, as the last step of its
  construction, passing itself. The forwarder can create loggers at once, so entries it writes during
  application startup reach the providers.
- **Disposal.**
  - Disposing the factory disposes its forwarders in reverse order, then its providers.
  - A forwarder that throws while being disposed does not stop the rest of teardown.
- **Failure during construction.** If a registration throws, returns `null`, or produces a duplicate
  name, construction fails. The forwarders already created (including the duplicate) and the providers
  are disposed, and the exception propagates.
- **Visibility.** `ILoggerFactory.Forwarders` lists the forwarders the factory owns. A forwarder a caller
  creates against the factory, rather than registers with it, is not listed.

Forwarding is active for the forwarder's whole lifetime; there is no start or stop.

## Implementing

- Write through `factory.Create(category)`, so the factory's filter rules, enrichers, and providers apply.
- Be thread-safe, and never let an exception reach the code whose activity is being forwarded.
- Keep `Dispose` idempotent.

## Implementations

- `Assimalign.Cohesion.Logging.EventSource` forwards `System.Diagnostics.Tracing` event sources
  (`AddEventSourceForwarding`, `ForwardEventSources`). Its name is `EventSource`.
