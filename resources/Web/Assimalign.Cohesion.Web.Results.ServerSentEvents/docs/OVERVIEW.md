# Assimalign.Cohesion.Web.Results.ServerSentEvents — Overview

The Server-Sent Events result adapter for the Cohesion Web pipeline. Referencing this package
extends the `Results` / `TypedResults` factories with `ServerSentEvents(...)`, returning an
`IResult` that streams a sequence of events as a `text/event-stream` response body.

## Usage

```csharp
app.MapGet("/events", context =>
    context.ExecuteResultAsync(Results.ServerSentEvents(TicksAsync(context.RequestCancelled))));

static async IAsyncEnumerable<ServerSentEvent> TicksAsync(
    [EnumeratorCancellation] CancellationToken cancellationToken = default)
{
    for (int tick = 1; ; tick++)
    {
        yield return new ServerSentEvent($"tick {tick}") { EventType = "tick", Id = $"{tick}" };
        await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
    }
}
```

The result sets `Content-Type: text/event-stream` and `Cache-Control: no-cache` before the first
write, flushes each event so the client observes it immediately, never sets `Content-Length`, and
fails loudly (`NotSupportedException`) when response streaming (#769) is not enabled on the
exchange.

## Dependencies

`Assimalign.Cohesion.Web.Results` (the `IResult` contract and factories),
`Assimalign.Cohesion.Http.ServerSentEvents` (the `ServerSentEvent` model and wire formatter), and
`Assimalign.Cohesion.Http` / `Assimalign.Cohesion.Http.Streaming` (the exchange and streaming
feature). Kept separate from the core results package so its dependency tree stays lean — see
[DESIGN.md](DESIGN.md).
