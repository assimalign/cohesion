# ISecretStoreApplicationBuilder

Namespace: `Assimalign.Cohesion.SecretStore`
Assembly: `Assimalign.Cohesion.SecretStore`

## Purpose

`ISecretStoreApplicationBuilder` is the public composition seam for a SecretStore application. It extends `IHostBuilder` while refining `Build()` to return `ISecretStoreApplication`.

## Surface and behavior

- `Build()` creates a configured SecretStore application.

The current filler builder has no feature registrations and produces an application with an empty hosted-service collection. The concrete builder remains internal to `Assimalign.Cohesion.SecretStore.Hosting`.

## Exceptions

The filler `Build()` operation has no documented exceptions.

## Usage

```csharp
using Assimalign.Cohesion.SecretStore;
using Assimalign.Cohesion.SecretStore.Hosting;

ISecretStoreApplicationBuilder builder = SecretStoreApplication.CreateBuilder(args);
await using ISecretStoreApplication application = builder.Build();
```
