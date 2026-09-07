# Hosting

`Assimalign.Cohesion.Hosting` provides Cohesion's dependency-light host lifecycle contracts and
implementations. It also defines the transport-neutral `IHealthContributor` seam, the ambient
`ResourceContext`/`ResourceRuntime`, and the Core-only `IResourceControlPlane` registration and
aggregation surface used by enabled resource executables. Its only Cohesion assembly dependency is
Core (plus the Windows ProtectedData BCL facade); it does not own dependency injection,
configuration, logging, or HTTP delivery.
