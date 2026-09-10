# IConfigurationStoreApplicationBuilder

Namespace: `Assimalign.Cohesion.ConfigurationStore`
Assembly: `Assimalign.Cohesion.ConfigurationStore`

## Purpose

`IConfigurationStoreApplicationBuilder` is the public composition seam for a configuration store application. It extends `IHostBuilder` while refining `Build()` to return `IConfigurationStoreApplication`.

## Surface and behavior

- `AddService(IHostService service)` registers an existing lifecycle service.
- `AddService(Func<IHostContext, IHostService> factory)` creates a lifecycle service once per `Build()` from that application's context.
- `AddNamespace(string name, Action<IConfigurationNamespaceBuilder> configure)` declares first-start values for one durable namespace.
- `Build()` creates a configured configuration store application.

The Hosting implementation appends its protocol listener after explicit services, so dependencies
start before the listener accepts requests and drain after it. Namespace declarations never replace
an existing durable namespace document. The concrete builder remains internal.

## Exceptions

`AddNamespace` rejects a blank or duplicate name and a null callback. `AddService` rejects a null
service or factory. `Build()` rejects reuse and a factory that returns null.

## Usage

```csharp
using Assimalign.Cohesion.ConfigurationStore;
using Assimalign.Cohesion.ConfigurationStore.Hosting;

IConfigurationStoreApplicationBuilder builder = ConfigurationStoreApplication.CreateBuilder(args);
builder.AddNamespace("app", ns => ns.Set("Mode", "production"));
await using IConfigurationStoreApplication application = builder.Build();
```
