# Assimalign.Cohesion.Hosting.ApplicationCluster Design

## Design Intent

The package is intentionally interface-heavy. It establishes the vocabulary for cluster resources and identities before locking in a concrete runtime implementation.

## Architecture

- IApplicationCluster and IApplicationClusterBuilder describe the outer orchestration surface.
- ResourceId and ResourceName give cluster members stable identities and names.
- The lack of implementation types suggests this package is a contract layer for future cluster runtimes.

## Layout Example

```text
Assimalign.Cohesion.Hosting.ApplicationCluster/
  src/
    Assimalign.Cohesion.Hosting.ApplicationCluster.csproj
    Abstractions/
    ValueObjects/
  tests/
  docs/
    OVERVIEW.md
    DESIGN.md
```

## Example 1: Implement the cluster contract

```csharp
internal sealed class Cluster : IApplicationCluster
{
    public IEnumerable<IApplicationClusterResource> Resources { get; } = [];

    public Task RunAsync(CancellationToken cancellationToken = default)
    {
        return Task.CompletedTask;
    }
}
```

## Example 2: Use the builder contract

```csharp
IApplicationClusterBuilder builder = CreateClusterBuilder();

builder.AddApplication(appResource);
IApplicationCluster cluster = await builder.BuildAsync(cancellationToken);
```
