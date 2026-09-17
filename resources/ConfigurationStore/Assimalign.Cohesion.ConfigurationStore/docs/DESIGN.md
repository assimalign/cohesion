# Assimalign.Cohesion.ConfigurationStore Design

## Design intent

The area root owns only the contracts that executable composition and feature packages target.
`IConfigurationStoreApplicationBuilder` declares first-start namespaces;
`IConfigurationStoreApplication` supplies the executable lifecycle.

## Hosting isolation

The root contracts are hosting-free (O34): `IConfigurationStoreApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IConfigurationStoreApplicationContext` exposes `ContentRootPath`. `IConfigurationStoreApplicationBuilder` owns area declarations and `Build()`. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

The concrete application, builder, and context live in `Assimalign.Cohesion.ConfigurationStore.Hosting`; options and supporting services remain internal.

## Code-first namespaces

`AddNamespace(name, configure)` uses `IConfigurationNamespaceBuilder.Set` to capture string or null
values. These are declarative seeds, not an in-memory source of truth: Hosting writes them only when
the corresponding namespace has no durable document. The concrete Hosting builder supports explicit
`IHostService` instances and factories; their services start before the protocol listener and stop after it.

## AOT posture

The contracts require no reflection, dynamic code generation, runtime assembly scanning, or container-based activation and remain safe for trimming and NativeAOT.

## Hosting-free application contract (O34)

`ConfigurationStoreApplication.CreateBuilder(args)` returns the public concrete `ConfigurationStoreApplicationBuilder`; its `Build()` returns the public `ConfigurationStoreApplication : Host<ConfigurationStoreApplicationContext>`. The public `ConfigurationStoreApplicationContext` implements `IConfigurationStoreApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
