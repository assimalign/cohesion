# Assimalign.Cohesion.Web.Results — Overview

The general HTTP result abstraction for the Cohesion Web pipeline: a handler *returns* a value
that knows how to write itself onto the exchange, instead of writing the response imperatively.

## What it provides

- **`IResult`** — the single-method contract
  (`Task ExecuteAsync(IHttpContext, CancellationToken)`) that endpoints, controllers, and
  source-generated bindings return.
- **Built-in results** — status-only, `204 No Content`, empty (write-nothing), text/content, JSON
  over caller-supplied source-generated `JsonTypeInfo<T>` (`Json<T>`, `Ok<T>`), byte/stream/
  physical-file payloads with `Content-Type` inference, redirects (301/302/307/308 by
  `permanent` × `preserveMethod`), RFC 9457 `ProblemDetails` (`application/problem+json`), and
  push streaming over the response-streaming feature.
- **`Results` / `TypedResults`** — plain-static factories; `Results` returns `IResult`,
  `TypedResults` returns the concrete carrier types for return-type inference and metadata.
  Concrete results have internal constructors — the factories are the only entry point.
- **Execution glue** — `context.ExecuteResultAsync(result)` and
  `response.WriteJsonAsync<T>(value, typeInfo)` / `response.WriteProblemDetailsAsync(problem)`
  extensions.
- **The ProblemDetails payload** — the RFC 9457 model plus the AOT-safe hand-rolled
  `Utf8JsonWriter` serializer (`ProblemDetailsWriter.Default`), reused by the exception-boundary
  work (#881).

## Usage

```csharp
app.MapGet("/widgets/{id:int}", context =>
    context.ExecuteResultAsync(
        Results.Ok(new Widget("gear", 42), AppJsonContext.Default.Widget)));

app.MapGet("/missing", context =>
    context.ExecuteResultAsync(
        Results.Problem(detail: "No such widget.", statusCode: HttpStatusCode.NotFound)));
```

JSON serialization always goes through a source-generated `JsonSerializerContext` the application
supplies — zero reflection, NativeAOT- and trimming-safe by construction.

## Dependencies

`Assimalign.Cohesion.Http` (protocol core) and `Assimalign.Cohesion.Http.Streaming` (the #769
response-streaming feature the streaming results adapt). The Server-Sent Events result lives in
the sibling `Assimalign.Cohesion.Web.Results.ServerSentEvents` adapter package.

## Scope boundaries

Content negotiation (`Accept`-aware `Ok<T>`) is deferred to #149; range/precondition-aware file
results to #777; the exception boundary, status-code pages, and 404 terminal to #881. See
[DESIGN.md](DESIGN.md) for the reasoning.
