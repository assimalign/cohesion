# Assimalign.Cohesion.Web.ErrorHandling — Overview

The `OnError` hook for the Cohesion Web pipeline: the fault seam through which an application
owns its error responses. Feature libraries throw their area-scoped exceptions; the hook
decides what the client sees, with an overridable default that renders the RFC 9457
`Web.ProblemDetails` payload.

## What it provides

- **The hook contract** — `IHttpErrorHandler` / the `HttpErrorHandler` delegate: inspect a
  fault, own the response for it (return `true`) or pass (`false`).
- **Builder-time registration** — `builder.AddErrorHandling().OnError(...)`; handlers are
  consulted in registration order.
- **The exchange feature** — `IHttpErrorHandlingFeature`, seeded onto every exchange; a
  pipeline exception boundary invokes `HandleAsync(context, exception)` to turn a caught fault
  into the application's response.
- **The terminal default** — when no registration owns a fault, the response is
  `500` + `application/problem+json` (`about:blank`, status phrase, no exception detail).

## Usage

```csharp
// Composition (builder time). With no OnError registrations, every fault renders as
// problem+json 500 — the hook is useful with zero configuration.
builder.AddErrorHandling().OnError(async (context, exception, cancellationToken) =>
{
    if (exception is not StorageUnavailableException)
    {
        return false; // pass to the next registration (or the default)
    }

    context.Response.StatusCode = 503;
    await context.Response.WriteProblemDetailsAsync(
        ProblemDetails.FromStatus(503, detail: "Storage is briefly unavailable; retry."),
        cancellationToken);
    return true;
});
```

A boundary invokes the hook (this is what the #881 exception-boundary middleware does; until it
lands, an inline `try/catch` around `next(context)` serves):

```csharp
IHttpErrorHandlingFeature hook = context.Features.Get<IHttpErrorHandlingFeature>()!;
await hook.HandleAsync(context, exception, context.RequestCancelled);
```

## Scope boundaries

- **Faults only.** Expected protocol outcomes — an authentication challenge's `401`, a router's
  `404`, an unsupported media type's `415` — are each feature's normal response path and must
  never arrive here as exceptions.
- **The boundary middleware itself** (exception catching, status-code pages, the 404 terminal)
  is #881's scope; this package is the seam it invokes.
- **The payload** is `Web.ProblemDetails`' scope; this package renders it.

Design rationale lives in [DESIGN.md](DESIGN.md).
