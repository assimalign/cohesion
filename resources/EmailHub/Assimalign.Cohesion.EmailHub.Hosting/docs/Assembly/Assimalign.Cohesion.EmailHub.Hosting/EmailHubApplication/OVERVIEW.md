# EmailHubApplication

Namespace: `Assimalign.Cohesion.EmailHub.Hosting`
Assembly: `Assimalign.Cohesion.EmailHub.Hosting`

## Purpose

`EmailHubApplication` is the public factory for the email hub hosting module. The runtime builder, host, context, and options types are internal.

## Factory behavior

- `CreateBuilder(string[] args)` validates the argument array and returns an `IEmailHubApplicationBuilder`.
- The arguments are reserved for later runtime-context integration; the current filler does not interpret them.
- Building the returned builder registers no hosted services.

## Exceptions

`CreateBuilder` throws `ArgumentNullException` when `args` is `null`.

## Usage

```csharp
using Assimalign.Cohesion.EmailHub;
using Assimalign.Cohesion.EmailHub.Hosting;

IEmailHubApplicationBuilder builder = EmailHubApplication.CreateBuilder(args);
await using IEmailHubApplication application = builder.Build();
await application.RunAsync(cancellationToken);
```
