# InProcessResourceDescriptorExtensions

`InProcessResourceDescriptorExtensions.InProcess(Assembly, string)` associates a built resource
descriptor with its statically linked executable assembly and absolute, resource-specific content
root. Rebinding the same descriptor to different values is rejected.

This method is SDK infrastructure and is hidden from IntelliSense. `Sdk.Gateway` emits no call to
it: the generated `Gateway.CreateBuilder(args)` binds by manifest identity through
[`InProcessResourceManifestExtensions`](../InProcessResourceManifestExtensions/OVERVIEW.md), with
`DynamicDependency` metadata for each compiler-discovered entry-point type, so the binding applies
whichever verb adds the resource. The gateway consults a descriptor binding first, so this method
serves builders that are not created through `Gateway.CreateBuilder(args)`. Hand-written callers
must supply an equivalent trimming root; the API is therefore annotated `RequiresUnreferencedCode`.

Until the owner decision of 2026-09-25, a generated `Add<Name>()` verb called this method on the
descriptor it returned. `Sdk.Gateway` no longer generates per-resource verbs.
