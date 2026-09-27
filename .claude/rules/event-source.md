---
paths:
  - "**/Internal/EventSource/**"
  - "**/*EventSource*.cs"
  - "libraries/Logging/Assimalign.Cohesion.Logging.EventSource/**"
---

# EventSource Convention

How a Cohesion library reports on itself at run time. `System.Diagnostics.Tracing.EventSource` is the
one mechanism: lifecycle events, failures, and counters go through an internal event source, and a
library never takes a logging dependency to describe its own behavior. Developers never see the type.
Out-of-process tools (`dotnet-trace`, `dotnet-counters`, `dotnet-monitor`, PerfView) enable it by name;
in process, `Assimalign.Cohesion.Logging.EventSource` forwards it into `ILogger` sinks.

Consumer-facing reference, including the list of instrumented assemblies: `docs/EVENT_SOURCES.md`.
First implementer: the `Connections.*` drivers (`Tcp`, `Quic`, `NamedPipes`, `Udp`).

## Shape of an event source

```csharp
namespace Assimalign.Cohesion.Connections.Tcp.Internal;

[EventSource(Name = "Assimalign.Cohesion.Connections.Tcp")]
internal sealed class TcpConnectionEventSource : EventSource
{
    public static readonly TcpConnectionEventSource Log = new();

    private long _currentConnections;

    private TcpConnectionEventSource()
    {
    }

    [NonEvent]
    public void ConnectionOpened(ConnectionId connectionId, EndPoint? remoteEndPoint)
    {
        Interlocked.Increment(ref _currentConnections);      // counters: always maintained

        if (IsEnabled(EventLevel.Informational, EventKeywords.None))
        {
            ConnectionOpened(connectionId.ToString(), remoteEndPoint?.ToString() ?? string.Empty);
        }
    }

    [Event(3, Level = EventLevel.Informational, Message = "Connection {0} opened from {1}")]
    private void ConnectionOpened(string connectionId, string remoteEndPoint)
        => WriteEvent(3, connectionId, remoteEndPoint);
}
```

## Rules

1. **Internal, sealed, one per assembly.** An assembly that raises events owns exactly one
   `internal sealed class <Subject>EventSource : EventSource` in `src/Internal/EventSource/`, namespace
   `{RootNamespace}.Internal`. No intermediate base class. An assembly that raises nothing has none.
2. **No public diagnostics surface.** No public type forwards to, wraps, or exposes an event source: no
   `XxxDiagnostics` forwarder classes, no public trace-code enums, no public counter snapshots. A
   public forwarder lets any caller write fabricated events into a process-global provider and freezes
   the event shapes as public API. A family whose members all raise events gives **each member its own
   source** rather than routing through one shared source in the family root (the retired
   `ConnectionDiagnostics` forwarder is the cautionary precedent).
3. **Never linked, never granted.** An event source is never `shared/` source (general-rules.md,
   *Shared source*: linked copies are distinct providers claiming one process-global name) and is never
   reached through `InternalsVisibleTo` from another shipped assembly. Test assemblies may reach it.
4. **The name is the assembly name.** `[EventSource(Name = "<AssemblyName>")]`, character for character
   (`Assimalign.Cohesion.Connections.Tcp`). Never set `Guid` (it is derived from the name), never put
   "EventSource" in the name, never pass the name to the base constructor instead. Consequences the rest
   of the repo relies on:
   - The `Assimalign.Cohesion.` prefix identifies every Cohesion source; it is the forwarder's default.
   - The name is the logger category of forwarded entries, so category rules apply unchanged:
     `AddRule("Assimalign.Cohesion.Connections", LogLevel.Debug)` covers every driver.
   - Tools take the assembly name: `dotnet-trace collect --providers Assimalign.Cohesion.Connections.Tcp`.
5. **One singleton.** `public static readonly <Subject>EventSource Log = new();` with a `private`
   parameterless constructor (default, manifest-based settings). Nothing else constructs one.
6. **Event methods.**
   - Explicit ids: `[Event(N, Level = ..., Message = "...")]`, starting at 1. A shipped id is never
     renumbered or reused; a retired event's id stays retired.
   - The method's parameters *are* the payload, and its body is exactly `WriteEvent(N, <the same
     arguments, same order>)`. EventSource does not reject a mismatch at compile time; it reports it at
     run time as event 0, `EventSourceMessage` — which the forwarder surfaces as a warning.
   - Payload types are `string`, `bool`, integral types, `double`, `Guid`, and `DateTime` — what
     `EventSourcePrimitive` converts, so `WriteEvent` binds to a trim-safe overload. Enums are written as
     their `ToString()` names. Domain types (`ConnectionId`, `EndPoint`, `Exception`) never reach an
     `[Event]` method: a public `[NonEvent]` overload converts them.
   - `[Event]` methods are `private` whenever a `[NonEvent]` overload exists, so callers always go
     through the guarded, typed path.
   - Payload names are camelCase C# parameter names; they become attribute keys on forwarded entries.
   - `Message` templates use `{0}`-style placeholders that match the payload; the forwarder formats
     them into the log message.
7. **Levels mean something.** `Critical` — the process cannot continue; `Error` — an operation failed;
   `Warning` — degraded but recovered; `Informational` — lifecycle (bound, opened, closed); `Verbose` —
   high-frequency detail (back-pressure, per-stream, per-operation). Never `LogAlways`.
8. **`Start`/`Stop` only for a same-flow unit of work.** EventSource treats a `…Start`/`…Stop` pair as
   an activity and tracks it on the async flow that raised `…Start`. Use the suffixes for work that
   begins and ends on one flow (a request, a handshake), with the stop id one past the start id. A
   lifetime that begins on one flow and ends on another — a listener, a connection, a stream — uses
   `Bound`/`Opened`/`Closed` names instead; `…Start` there would open an activity on the accept loop that
   never stops and nest every later connection under it.
9. **Cost nothing when nobody listens.** Every write sits behind `IsEnabled(level, keywords)` —
   usually in the `[NonEvent]` overload — and every argument that allocates (`ToString()`, formatting)
   is computed inside that check. Events without keywords check with `EventKeywords.None`. Keywords are
   optional; when used they are explicit bits in a nested `Keywords` class, below the reserved top 16.
10. **Counters are exact or absent.** `PollingCounter`/`IncrementingPollingCounter` instances are created
    lazily in `OnEventCommand` on the first `EventCommand.Enable`, never in the constructor. Their backing
    fields are updated with `Interlocked` on every transition *whether or not anyone listens*, so a tool
    that attaches late reads true values. Counter names are kebab-case (`current-connections`) with a
    `DisplayName`. A gauge (`current-*`) needs its owner to report the opening and the closing transition
    exactly once each — guard the close with an `Interlocked.Exchange` flag when two paths (`Abort`,
    `DisposeAsync`) can end the same object. Expose the backing values as `internal` read-only
    properties for tests. A counter that cannot be driven accurately is not shipped.
11. **Payload hygiene.** No secrets, credentials, tokens, connection strings, or message bodies.
    Identifiers and endpoints are fine. An exception is written as its type's full name and its
    `Message`, never `ToString()`: payloads stay bounded, and stack traces belong to the caller that
    handles the exception.
12. **NativeAOT.** A NativeAOT application receives no events unless it sets
    `<EventSourceSupport>true</EventSourceSupport>`. Library behavior never depends on whether events are
    delivered, and the library builds with no trim or AOT analyzer warnings.

## Tests (required for every event source)

Each instrumented library's test project carries a `<Subject>EventSourceTests` class that:

1. asserts `EventSource.GetName(typeof(<Subject>EventSource))` equals the assembly name;
2. asserts `EventSource.GenerateManifest(typeof(<Subject>EventSource), null, EventManifestOptions.Strict)`
   succeeds — this catches duplicate ids, an id that differs from the one passed to `WriteEvent`, and
   unsupported payload types (the test method suppresses IL2026/IL2111 with a test-only justification);
3. drives a real operation under an `EventListener`, asserts each lifecycle event arrives exactly once
   with its declared payload names, asserts no event 0 (`EventSourceMessage`) was raised, and asserts
   any gauge returns to its starting value;
4. enables counters with a short `EventCounterIntervalSec` and asserts each declared counter publishes.

Event sources and their counters are process-wide, so these tests run in a
`[CollectionDefinition(..., DisableParallelization = true)]` collection.

## Documenting an event source

- The library's `docs/DESIGN.md` has a **Diagnostics** section: the source name, a table of events
  (id, name, level, payload), and the counters.
- `docs/EVENT_SOURCES.md` lists the source; add the row in the same change that adds the source.

## Not yet conforming

Pre-convention sources in `DependencyInjection` (`Assimalign-Cohesion-DependencyInjection`), `Resilience`
(three sources: a concatenated name, an empty name, a name ending in `EventSource`), and
`Http.Connections` (an empty placeholder) are tracked for migration in `docs/EVENT_SOURCES.md`. Do not copy
their patterns; bring one into line when you next touch it.
