# Assimalign.Cohesion.LogSpace.Hosting Design

## Design intent

The hosting module implements the area root's contract-only application seam. Public construction is limited to `LogSpaceApplication.CreateBuilder(args)`; the builder, `Host<TContext>` implementation, context, and options are internal.

## Filler execution model

The built host exposes the ordered `HostedServices` materialized from explicit builder registrations and a production `HostEnvironment`; the collection remains empty when nothing is registered. Instance and factory registrations share one order, factories run exactly once per `Build()` after the context exists, and a null factory result fails the build. Services start in registration order and stop in reverse registration order through the shared host lifecycle without claiming that log-storage behavior exists.

`SegmentFlushService` and `IngestEndpointService` remain as dormant future service stubs. The filler builder does not register them automatically.

## Boundaries

The module references only the LogSpace area root and the shared Hosting foundation, preserving the resource hosting-isolation rule. It uses no reflection or dynamic activation and remains trimming- and NativeAOT-safe.

Command-line arguments are accepted at the canonical entry point. Integration with the ambient `ResourceRuntime` is deferred to design item 12; the explicit `ILogSpaceApplication.RunAsync` wrapper records that handoff.
