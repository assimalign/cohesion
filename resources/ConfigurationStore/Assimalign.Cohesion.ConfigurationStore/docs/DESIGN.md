# Assimalign.Cohesion.ConfigurationStore Design

## Design intent

The area root owns only the contracts that feature packages compose against. `IConfigurationStoreApplicationBuilder` is the contract-only builder seam, while `IConfigurationStoreApplication` supplies the host lifecycle expected by an executable resource.

## Hosting isolation

The root references only the shared Hosting foundation. The concrete builder, host, context, and options remain internal to `Assimalign.Cohesion.ConfigurationStore.Hosting`; feature libraries must not reference that runtime module.

## Filler lifecycle

The current implementation registers no area services by default and always uses the production host environment. The builder accepts `IHostService` instances and `Func<IHostContext, IHostService>` factories; each factory is materialized once per `Build()` against that application's context. Services start in registration order and stop in reverse registration order. The filler exists only to complete the SDK/framework path until configuration-store behavior is implemented.

## AOT posture

The contracts require no reflection, dynamic code generation, runtime assembly scanning, or container-based activation and remain safe for trimming and NativeAOT.
