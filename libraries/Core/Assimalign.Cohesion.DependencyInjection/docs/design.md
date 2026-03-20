# Assimalign.Cohesion.DependencyInjection Design

## Design Intent

The package mirrors the familiar shape of a modern DI container while remaining self-hosted inside the Cohesion ecosystem. Registration, provider construction, and resolution are intentionally separated into explicit types.

## Architecture

- ServiceDescriptor and IServiceCollection capture registration intent independently of runtime resolution.
- ServiceProviderBuilder composes a provider from descriptors and exposes extension points for registration helpers.
- Resolution internals, call sites, scopes, and activator utilities sit behind the public contracts so the outer API remains compact.

## Layout Example

```text
Assimalign.Cohesion.DependencyInjection/
  src/
    Assimalign.Cohesion.DependencyInjection.csproj
    Abstractions/
    Extensions/
    Internal/
    Properties/
    Scopes/
    Utilities/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Register and resolve a singleton

```csharp
var builder = new ServiceProviderBuilder();

builder.Add(ServiceDescriptor.Singleton(typeof(IMyService), typeof(MyService)));

IServiceProvider provider = builder.Build();
object service = provider.GetRequiredService(typeof(IMyService));
```

## Example 2: Compose registrations with lifetime helpers

```csharp
var builder = new ServiceProviderBuilder();

builder.AddTransient(typeof(IMyHandler), typeof(MyHandler));
builder.AddScoped(typeof(IMyRepository), typeof(MyRepository));
builder.AddSingleton(typeof(IMyClock), typeof(SystemClock));

IServiceProvider provider = builder.Build();
IServiceScope scope = provider.CreateScope();
```
