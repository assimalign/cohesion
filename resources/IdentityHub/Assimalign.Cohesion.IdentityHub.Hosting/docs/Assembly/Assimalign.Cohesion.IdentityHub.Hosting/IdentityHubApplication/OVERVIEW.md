# IdentityHubApplication

Namespace: `Assimalign.Cohesion.IdentityHub.Hosting`
Assembly: `Assimalign.Cohesion.IdentityHub.Hosting`

## Purpose

`IdentityHubApplication` is the public factory for the identity hub hosting module. The runtime builder, host, context, and options types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `IIdentityHubApplicationBuilder`.
- The arguments are reserved for later runtime-context integration; the current filler does not interpret them.
- Building the returned builder registers no hosted services.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`.

## Usage

```csharp
using Assimalign.Cohesion.IdentityHub;
using Assimalign.Cohesion.IdentityHub.Hosting;

IIdentityHubApplicationBuilder builder = IdentityHubApplication.CreateBuilder(args);
await using IIdentityHubApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```
