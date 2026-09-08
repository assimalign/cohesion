# Assimalign.Cohesion.Rezolvr.Hosting Design

## Design intent

The hosting module implements the area root's contract-only application seam. Public construction is limited to `RezolvrApplication.CreateBuilder(args)`; the builder, `Host<TContext>` implementation, context, and options are internal.

## Composition execution model

Each build creates a new context, invokes every registered service factory exactly once against that context, and exposes the materialized services as an ordered, read-only `HostedServices` snapshot. The shared host starts services in registration order and stops them in reverse; an unconfigured builder still produces an empty collection and a production `HostEnvironment`.

`ResolverEndpointService` remains as a dormant future service stub and is not registered by default.

## Boundaries

The module references only the Rezolvr area root and the shared Hosting foundation, preserving the resource hosting-isolation rule. It uses no reflection or dynamic activation and remains trimming- and NativeAOT-safe.

Command-line arguments are accepted at the canonical entry point. Integration with the ambient `ResourceRuntime` is deferred to design item 12; the explicit `IRezolvrApplication.RunAsync` wrapper records that handoff.
