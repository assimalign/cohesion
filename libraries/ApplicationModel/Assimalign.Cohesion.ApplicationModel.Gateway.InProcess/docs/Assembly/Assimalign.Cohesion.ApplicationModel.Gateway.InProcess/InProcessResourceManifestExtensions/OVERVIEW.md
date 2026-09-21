# InProcessResourceManifestExtensions

`InProcessResourceManifestExtensions.InProcess(Assembly, string)` associates a generated resource
manifest with its statically linked executable assembly and absolute, resource-specific content
root. The binding is keyed by the manifest's application and resource names, not by the manifest
instance: a planned resource snapshots the manifest it was built from, so identity is the only key
that survives `AddResource`. The gateway consults this registry whenever a resource has no
descriptor binding, which is how a resource added through the area verb
(`builder.AddWeb(Manifests.DocsWeb)`) or a third-party application model's verb over the same
manifest is colocated. Rebinding the same names to different values is rejected.

This method is SDK infrastructure and is hidden from IntelliSense. `Sdk.Gateway` emits one call per
enabled, composable project resource inside the generated `Gateway.CreateBuilder(args)`, with
`DynamicDependency` metadata for each compiler-discovered entry-point type. Hand-written callers
must supply an equivalent trimming root; the API is therefore annotated `RequiresUnreferencedCode`.
