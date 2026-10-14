# Assimalign.Cohesion.Web.ProblemDetails — Overview

The RFC 9457 *Problem Details for HTTP APIs* payload for the Cohesion Web pipeline: a machine-
readable error body (`application/problem+json`) with an AOT-safe, zero-reflection writer.

## What it provides

- **`ProblemDetails`** — the RFC 9457 model: `Type`, `Title`, `Status`, `Detail`, `Instance`,
  plus a constrained `Extensions` bag; `ProblemDetails.FromStatus(statusCode, detail)` for the
  common status-derived shape.
- **`ProblemDetailsWriter.Default`** — the single problem+json serializer in the framework
  (`IProblemDetailsWriter`), hand-rolled on `Utf8JsonWriter` so it is NativeAOT- and
  trimming-safe and safe to call from last-chance error handling.
- **`response.WriteProblemDetailsAsync(problem)`** — the imperative write path for middleware
  and error handlers.

## Usage

```csharp
app.Use(async (context, next) =>
{
    if (!authorized)
    {
        await context.Response.WriteProblemDetailsAsync(
            ProblemDetails.FromStatus(HttpStatusCode.Forbidden, "Insufficient scope."));
        return;
    }

    await next.Invoke(context);
});
```

Types surface in the `Assimalign.Cohesion.Web` namespace (documented deviation — see
[DESIGN.md](DESIGN.md)).

`HttpStatusCode` accepts codes from 100 through 599, including unregistered codes.
`ProblemDetails.FromStatus(new HttpStatusCode(499))` uses title `Error`; an unregistered 5xx
code uses `Internal Server Error`. Registered codes use their reason phrase. The response helper
preserves the numeric status, and an explicit `ProblemDetails.Title` is written unchanged.

## Scope boundaries

This is the payload only. Error *policy* — when a fault becomes a response and in what shape —
is the application's, composed through the `OnError` hook + content-serialization registry
design that issue #864 owns; its default handler renders this payload. Dependencies:
`Assimalign.Cohesion.Http` only.
