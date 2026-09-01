# Assimalign.Cohesion.SourceGeneration.ComponentModel Design

## Design intent

This project supplies reference-free integration light-up between Cohesion libraries. A contributing
assembly describes a public static factory through an ordinary assembly-level
`ComponentIntegrationAttribute`; a consuming compilation receives a forwarding extension member only
when it also references the named composition seam. The contributor therefore does not reference the
seam owner, and the generator contains no knowledge of particular libraries, containers, or lifetimes.

Like every project under `analyzers/`, it targets `netstandard2.0`, is loaded by Roslyn, and is not a
standalone package. Cohesion delivers it through the Core package for PackageReference consumers and
through the App targeting pack for SDK consumers.

## Mechanism

`ComponentIntegrationGenerator` is an `IIncrementalGenerator` that runs in the consuming compilation.
It uses `CompilationProvider` because assembly attributes on referenced binaries are metadata, not
syntax nodes, and cannot be discovered with `ForAttributeWithMetadataName`.

Collection proceeds in a fixed order:

1. Look up all types with metadata name
   `Assimalign.Cohesion.ComponentIntegrationAttribute`; no match means no work and no diagnostics.
2. Scan the current assembly and referenced assembly symbols. Referenced binaries are first filtered by
   whether they reference `Assimalign.Cohesion.Core`.
3. Decode matching assembly attributes defensively. Malformed declarations are diagnosed and skipped
   without stopping other contributors.
4. Resolve the target seam with `GetTypesByMetadataName`. An absent seam suppresses emission and reports
   one discoverability diagnostic for the contributing assembly.
5. Validate every public static ordinary overload of the named factory and convert it to a value-only
   projection model.
6. Sort projections deterministically and remove duplicate seam/verb/parameter signatures.

The incremental model contains strings, booleans, integers, equatable arrays, and captured diagnostic
values only. Roslyn symbols and locations never cross from collection into the cached output model.

## Attribute contract

The public attribute lives in `Assimalign.Cohesion.Core`. Its required constructor values are:

- `TargetTypeName`: the seam's fully qualified metadata name, deliberately represented as a string.
- `TargetMethodName`: the instance or extension sink invoked on the seam.
- `FactoryType`: the public static type holding the factory.
- `FactoryMethodName`: the public static method name; every supported overload is projected.

`Verb` optionally renames the projected method and defaults to the factory method name. `Contract`
optionally supplies the sink's explicit generic type argument. Adding another factory overload needs no
attribute change.

Supported parameters are ordinary by-value parameters. The generator preserves `params`, nullable type
annotations, and explicit defaults, and forwards every argument without adding behavior. Generic factory
methods, by-reference parameters, void returns, and non-public signature types are intentionally rejected.

## Emission

Output is one `CohesionComponents.<DeclaringAssembly>.g.cs` file per contributing assembly. Multiple
factory namespaces are grouped as separate block namespaces in that file. Block namespaces are the
sanctioned generated-code exception to the repository's file-scoped namespace rule.

The seam namespace is imported at file scope because the named sink may itself be an extension member and
extension lookup is namespace-scoped. All emitted type references are `global::`-qualified. The generated
container is internal, while its extension member is public within that container:

```csharp
extension(global::Target.ISeam builder)
{
    public global::Target.ISeam AddThing(global::System.String @name = "default")
    {
        builder.Add(global::Contributor.Components.Create(@name));
        return builder;
    }
}
```

The generated adapter is deliberately semantics-free: the factory owns construction and validation, the
target method owns registration behavior, and the adapter only composes the two calls. C# 14 is required to
declare the extension block; a down-level consuming compilation receives a diagnostic and no source.

## Disposal guard

For a factory returning `System.Func<...>`, the final type argument is the produced component. Otherwise
the return type itself is the product. A directly returned product implementing `IDisposable` or
`IAsyncDisposable` produces COHCMP0007 but is still emitted, because instance registration commonly
bypasses container disposal capture while a producer registration can be captured.

## Diagnostics

| ID | Severity | Meaning |
| --- | --- | --- |
| `COHCMP0001` | Warning | Declaration is malformed or contains unresolvable metadata. |
| `COHCMP0002` | Warning | Factory or signature is not externally visible. |
| `COHCMP0003` | Warning | Factory method uses an unsupported shape. |
| `COHCMP0004` | Warning | Projection duplicates an earlier seam/verb/parameter signature. |
| `COHCMP0005` | Info | Contributor is referenced while its named seam is absent. |
| `COHCMP0006` | Warning | Consumer language version is older than C# 14. |
| `COHCMP0007` | Warning | Disposable product is returned directly instead of through `Func<...>`. |

All locations are `Location.None` because declarations are normally read from assembly metadata.

## Limitations

- The target metadata name is an unchecked string. Renaming a seam disables its integrations until the
  declaration is updated; the repository integration-check project is the drift canary.
- Sink existence and overload compatibility are left to normal C# binding. A generated compile error is
  more accurate than attempting incomplete extension-member lookup inside the generator.
- A factory cannot name a type from an assembly it intentionally does not reference. Such integrations
  belong with the seam owner or in a dedicated adapter assembly.
- Generated containers are internal and cannot re-export verbs through another library.
- The adapter always returns the seam and has no ordering or middleware semantics; pipeline-shaped
  integrations and sub-builder returns are outside this mechanism.
- Analyzer delivery is transitive for packaged consumers, so mixed Cohesion package versions can select
  one of multiple analyzer copies. Coherent package versions are the supported configuration.
