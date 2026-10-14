# Assimalign.Cohesion.SourceGeneration.ObjectMapping

This Roslyn incremental generator provides compile-time mapping configuration for
`Assimalign.Cohesion.ObjectMapping`. It owns the generator for `libraries/ObjectMapping`
under the analyzer placement rule (owner decision 59, 2026-10-13).

`MapperProfileGenerator` recognizes eligible partial `MapperProfile<TTarget, TSource>`
classes and inline `MapperBuilder.AddProfile<TTarget, TSource>` lambdas. It emits delegate
mapping configuration so these paths avoid reflection and expression compilation at runtime.
Unsupported configuration keeps the library's existing fallback behavior.

The project targets `netstandard2.0` with `IsAotCompatible=false`, inherited from the
analyzers area's build configuration. Those settings describe the compiler component;
its generated mapping code remains NativeAOT-compatible.

In-repository consumers reference it with:

```xml
<CohesionAnalyzerReference Include="Assimalign.Cohesion.SourceGeneration.ObjectMapping" />
```

The ObjectMapping library package bundles the generator under `analyzers/dotnet/cs`.
Inline profiles also require the existing interceptor namespace opt-in:

```xml
<InterceptorsNamespaces>$(InterceptorsNamespaces);Assimalign.Cohesion.ObjectMapping.Generated</InterceptorsNamespaces>
```

See [DESIGN.md](DESIGN.md) for generation boundaries and verification, and the
[ObjectMapping design](../../../libraries/ObjectMapping/Assimalign.Cohesion.ObjectMapping/docs/DESIGN.md)
for the runtime mapping contract.
