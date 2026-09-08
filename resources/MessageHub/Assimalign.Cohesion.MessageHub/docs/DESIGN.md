# Assimalign.Cohesion.MessageHub Design

## Design intent

The area root owns only the contracts that feature packages compose against. `IMessageHubApplicationBuilder` is the contract-only builder seam, while `IMessageHubApplication` supplies the host lifecycle expected by an executable resource.

## Hosting isolation

The root references only the shared Hosting foundation. The concrete builder, host, context, and options remain internal to `Assimalign.Cohesion.MessageHub.Hosting`; feature libraries must not reference that runtime module.

## Composition lifecycle

The builder accepts existing `IHostService` instances and factories that receive the newly created area `IHostContext`. Each factory is invoked once per `Build()`, and the resulting services are retained in registration order so the shared host starts them in that order and stops them in reverse. The collection is empty when callers register nothing, and the host environment remains production.

No message-broker service is registered by default. The seam remains composition-only until message-broker behavior is implemented.

## AOT posture

The contracts require no reflection, dynamic code generation, runtime assembly scanning, or container-based activation and remain safe for trimming and NativeAOT.
