# Assimalign.Cohesion.ConfigurationStore Design

## Design intent

The area root owns only the contracts that executable composition and feature packages target.
`IConfigurationStoreApplicationBuilder` declares first-start namespaces and optional host services;
`IConfigurationStoreApplication` supplies the executable lifecycle.

## Hosting isolation

The root references only the shared Hosting foundation. The concrete builder, host, context, and options remain internal to `Assimalign.Cohesion.ConfigurationStore.Hosting`; feature libraries must not reference that runtime module.

## Code-first namespaces

`AddNamespace(name, configure)` uses `IConfigurationNamespaceBuilder.Set` to capture string or null
values. These are declarative seeds, not an in-memory source of truth: Hosting writes them only when
the corresponding namespace has no durable document. Explicit `IHostService` instances and factories
remain supported and start before the protocol listener, then stop after it.

## AOT posture

The contracts require no reflection, dynamic code generation, runtime assembly scanning, or container-based activation and remain safe for trimming and NativeAOT.
