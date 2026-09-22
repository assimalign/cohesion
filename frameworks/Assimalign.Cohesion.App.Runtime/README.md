# Assimalign.Cohesion.App runtime pack

This project produces the RID-specific runtime pack for the Cohesion hosting
kernel. It is a packaging shell: the policy lives in
`frameworks/Assimalign.Cohesion.App.props`, and the collection mechanics live in
`frameworks/Assimalign.Cohesion.App.targets`.

The base `Assimalign.Cohesion.Sdk` does not reference this framework implicitly.
Resource-area SDKs reference App together with `App.<Area>`; a direct base-SDK
executable adds its own explicit `FrameworkReference`.

## Derived kernel

`@(CohesionAppKernelRoot)` is the only curated input. It names Hosting and its
Health, Resources, and Telemetry siblings; the CommandLine,
EnvironmentVariables, FileSystem, and Json configuration providers;
Logging.Console; OpenTelemetry; DependencyInjection; FileSystem.Physical; and
Connections. Connections belongs in the kernel because generated resource
accessors and every area hosting module depend on it.
The runtime project references those roots, and the pack target derives their
transitive `Assimalign.Cohesion.*` project-reference closure before writing
`RuntimeList.xml` and `FrameworkList.xml`. The App umbrella assembly is added to
that derived set.

The framework tests independently walk the same `libraries/**` and
`resources/**` project index. They enforce both the App kernel closure and each
area framework's coverage, including whether a dependency is public or a
runtime-only implementation detail.

Libraries outside the kernel are ordinary NuGet packages. Adding one requires a
consumer package reference; it must not be appended directly to App's assembly
list.
