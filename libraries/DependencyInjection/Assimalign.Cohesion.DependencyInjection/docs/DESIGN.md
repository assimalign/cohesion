# Assimalign.Cohesion.DependencyInjection Design

## Design Intent

The package mirrors the familiar shape of a modern DI container while remaining self-hosted inside the Cohesion ecosystem. Registration, provider construction, and resolution are intentionally separated into explicit types.

## Architecture

- ServiceDescriptor and IServiceCollection capture registration intent independently of runtime resolution.
- ServiceProviderBuilder composes a provider from descriptors and exposes extension points for registration helpers.
- Resolution internals, call sites, scopes, and activator utilities sit behind the public contracts so the outer API remains compact.

## Resolver compilation policy

`ServiceProviderOptions.EnableDynamicCode` defaults to `true`, preserving the existing resolver
choice: JIT runtimes may compile frequently resolved call sites, while NativeAOT uses the
interpreted runtime resolver. Setting it to `false` selects the runtime resolver before any
compiled engine is constructed. That resolver never schedules background expression or IL
compilation, including after repeated scoped, transient, or enumerable resolutions. The choice
is captured when each provider is built; later options mutation does not change that provider.

This is a general provider policy for consumers requiring no runtime code generation. Database
hosting always disables it. The option does not change registration semantics or remove
reflection from implementation-type constructor activation; consumers requiring reflection-free
construction register explicit closed factories or instances. Scope validation, singleton and
scope caching, and disposal retain their existing behavior.

`ServiceProviderDynamicCodeTests` exercises factory lifetimes and scope validation under the
strict policy. Its JIT test observes the actual resolver compilation diagnostics, confirms an
enabled control provider emits code after repeated resolution, and verifies that the disabled
provider emits neither IL nor expression diagnostics and retains the runtime engine with no
background compilation path. Phase 29 verification on 2026-09-19 also published and ran a
`net10.0` / `win-arm64` NativeAOT smoke using explicit singleton/scoped/transient factories and
an instance registration with dynamic code disabled. It verified 1,024 resolutions, scope
validation, and owned versus borrowed disposal. Existing constructor/open-generic trim/AOT
diagnostics remain outside this resolver-policy change; the strict factory path ran successfully.

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
