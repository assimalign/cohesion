# Assimalign.Cohesion.EventHub Design

## Design intent

The area root owns only the contracts that feature packages compose against. `IEventHubApplicationBuilder` is the contract-only builder seam, while `IEventHubApplication` supplies the host lifecycle expected by an executable resource.

## Hosting isolation

The root references only the shared Hosting foundation and its existing Connections dependency. The concrete builder, host, context, and options remain internal to `Assimalign.Cohesion.EventHub.Hosting`; feature libraries must not reference that runtime module.

## Filler lifecycle

The current implementation registers no hosted services and always uses the production host environment. It exists only to complete the SDK/framework path until event-hub behavior is implemented.

## AOT posture

The contracts require no reflection, dynamic code generation, runtime assembly scanning, or container-based activation and remain safe for trimming and NativeAOT.
