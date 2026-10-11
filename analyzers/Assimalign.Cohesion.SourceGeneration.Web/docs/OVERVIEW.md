# Assimalign.Cohesion.SourceGeneration.Web Overview

This build-time Roslyn generator supplies typed endpoint binding for
`Assimalign.Cohesion.Web.Api`. It rewrites typed `Map*` calls into interceptors that bind request
parameters, invoke the handler, write its response, and attach endpoint metadata without runtime
reflection. Unsupported handlers produce `COHWEB` compile-time diagnostics.

Sdk.Web applications receive the generator through App.Web. In-repository consumers activate it
with `<CohesionAnalyzerReference Include="Assimalign.Cohesion.SourceGeneration.Web" />` and add
`Assimalign.Cohesion.Web.Api.Generated` to `InterceptorsNamespaces`.

Query parameters support scalar values and sequences of scalars: arrays, `List<T>`, `IEnumerable<T>`,
`IReadOnlyList<T>`, `IReadOnlyCollection<T>`, `IList<T>` and `ICollection<T>`. For example,
`app.MapGet("/search", (string[] tag) => string.Join('|', tag))` reads every `tag` occurrence in order.
`?tag=one&tag=&tag=two` supplies `["one", "", "two"]`; a scalar `string tag` reads `"one"`.
A missing collection is empty, and an invalid element rejects the request with 400.

Use `[FromQuery(Name = "tag")]` to choose a different query key. Use `[FromBody]` explicitly for a
collection that should be deserialized from the request body. Before #1335 scalar query reads kept
the last occurrence and inferred collections bound from the body; these are behavior changes from
10.0.0-preview.1.

The generator targets netstandard2.0 as a compiler component. Its emitted code retains the
framework's NativeAOT guarantees. See [DESIGN.md](DESIGN.md) for classification, emission,
diagnostics, delivery, and tests, and the
[Web.Api overview](../../../resources/Web/Assimalign.Cohesion.Web.Api/docs/OVERVIEW.md) for the
complete endpoint surface.
