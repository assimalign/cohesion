# Assimalign.Cohesion.SourceGeneration.Web Design

## Design Intent

This project is a build-time Roslyn source generator that gives `Assimalign.Cohesion.Web.Api` its
typed-delegate endpoint binding. Because the framework targets NativeAOT, binding must be generated —
no reflection over handler signatures and no `Expression.Compile`. The generator is the AOT-sanctioned
path (ASP.NET reached the same conclusion with `RequestDelegateGenerator`).

Like every project under `analyzers/`, it targets `netstandard2.0` with `IsAotCompatible=false` and
`EnablePreviewFeatures=false` — the one sanctioned exception to the repo-wide TFM/AOT defaults, because
the compiler loads generators as netstandard2.0 components. It ships no NuGet package of its own; the
consuming library bundles the DLL under `analyzers/dotnet/cs/` (see delivery below).

## What It Intercepts

`EndpointBindingGenerator` is an `IIncrementalGenerator`. Its syntax predicate cheaply matches
`Map`/`MapGet`/`MapPost`/`MapPut`/`MapPatch`/`MapDelete` invocations; the semantic transform then keeps
only the calls that resolve to a typed overload — one whose handler parameter is `System.Delegate`
(the `WebApplicationMiddleware` overloads are registered verbatim and ignored). For each such call site
the transform records an equatable `EndpointBinding` model: the interceptable location, the concrete
receiver type, whether an explicit `HttpMethod` parameter is present, the handler's
exact delegate type, its return shape, and a classified `ParameterBinding` per handler parameter. Any
call site the transform cannot fully model returns `null` and is left to the throwing placeholder
overload.

## Parameter Classification

Per parameter, in order: direct injections (`IHttpContext`, `CancellationToken`, `IHttpFeature`
implementations) win first; then an explicit `[From*]` attribute; then convention (route-token name
match → route, scalar → query, complex → body). Scalar-ness is decided by `System.IParsable<T>`,
enum-ness, `string`, and `Nullable<>` of those. The model stores fully-qualified type strings so the
emit phase needs no symbols and incremental caching stays value-based (via `EquatableArray<T>`).

## Emission

The generator emits one file, `EndpointBinding.Interceptors.g.cs`, containing a file-local
`InterceptsLocationAttribute` shim (in `System.Runtime.CompilerServices`) and a `file static class` of
interceptors in `Assimalign.Cohesion.Web.Api.Generated`, produced through `RegisterImplementationSourceOutput`.
Each interceptor:

- Casts the `Delegate` to the inferred `Func<...>`/`Action<...>` and invokes it directly (AOT-safe).
- Emits inline binding per parameter: route (`TryGetRouteValues` + boxed fast-path/`TryParse`
  fallback), query/form (`TryGetValue` + `TryParse`), header (`GetValue` + `TryParse`), body
  (`IHttpContentSerializationFeature` reader probe → 415, `ReadContentAsync<T>` with `JsonException`
  → 400 / `HttpContentSerializationException` → 415), and direct injections.
- Registers the thunk through the raw `Map` overload — which binds to `WebApplicationMiddleware`, not
  the typed overload, so generated registration is never itself intercepted.

Conversions use `IParsable<T>.TryParse` / `Enum.TryParse<T>` with `InvariantCulture`, so no runtime
binder helper is required and the emitted code carries no reflection.

## Delivery

The generator is consumed exactly like the base `SourceGeneration` generator:

- In-repo and test projects add `<CohesionAnalyzerReference Include="Assimalign.Cohesion.SourceGeneration.Web" />`,
  which activates the analyzer at compile time and bundles the DLL into the consuming library's own
  `.nupkg`.
- Sdk.Web consumers get it through the `<CohesionFrameworkAnalyzer Include="Assimalign.Cohesion.SourceGeneration.Web" />`
  entry in the `App.Web` group of `frameworks/Assimalign.Cohesion.App.props`, bundled at
  `analyzers/dotnet/cs/` inside `App.Web.Ref`.

Consumers must allow-list the generated namespace with
`<InterceptorsNamespaces>$(InterceptorsNamespaces);Assimalign.Cohesion.Web.Api.Generated</InterceptorsNamespaces>`
(the interceptor feature's opt-in), which the Web.Api test project demonstrates.

## Testing

`tests/` drives the generator with a hand-rolled `CSharpGeneratorDriver` and asserts on the emitted
source. Runtime behavior — real requests through every binding source, the 400/415 outcomes, and
injection — is proven end-to-end in `Assimalign.Cohesion.Web.Api/tests` against the in-memory
`WebApplicationTestFactory`.

## Non-Goals

Result-typed handler returns, filter chains, OpenApi emission, and compile-time diagnostics for
unmodelable call sites are out of scope for v1 (see `Web.Api/docs/DESIGN.md`).
