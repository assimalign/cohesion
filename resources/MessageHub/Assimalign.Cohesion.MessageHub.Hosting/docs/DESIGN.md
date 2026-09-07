# Assimalign.Cohesion.MessageHub.Hosting Design

## Design intent

The hosting module implements the area root's contract-only application seam. Public construction is limited to `MessageHubApplication.CreateBuilder(args)`; the builder, `Host<TContext>` implementation, context, and options are internal.

## Filler execution model

The built host deliberately exposes an empty `HostedServices` collection and a production `HostEnvironment`. It can start, observe cancellation, and stop through the shared host lifecycle without claiming that message-broker behavior exists.

`JournalFlushService` and `BrokerEndpointService` remain as dormant future service stubs. The filler builder does not register them.

## Boundaries

The module references only the MessageHub area root and the shared Hosting foundation, preserving the resource hosting-isolation rule. It uses no reflection or dynamic activation and remains trimming- and NativeAOT-safe.

Command-line arguments are accepted at the canonical entry point. Integration with the ambient `ResourceRuntime` is deferred to design item 12; the explicit `IMessageHubApplication.RunAsync` wrapper records that handoff.
