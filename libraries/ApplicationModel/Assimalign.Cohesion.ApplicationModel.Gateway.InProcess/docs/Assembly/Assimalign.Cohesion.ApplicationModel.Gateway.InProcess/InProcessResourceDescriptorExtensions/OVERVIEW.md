# InProcessResourceDescriptorExtensions

`InProcessResourceDescriptorExtensions.InProcess(Assembly, string)` associates a built resource
descriptor with its statically linked executable assembly and absolute, resource-specific content
root. Rebinding the same descriptor to different values is rejected.

This method is SDK infrastructure and is hidden from IntelliSense. `Sdk.Gateway` emits calls only
for the enabled, composable project-reference closure and emits `DynamicDependency` metadata for the
compiler-discovered entry-point type. Hand-written callers must supply an equivalent trimming root;
the API is therefore annotated `RequiresUnreferencedCode`.
