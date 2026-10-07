# Assimalign.Cohesion.Web.WebSockets — Overview

The WebSocket policy for the Web pipeline. `UseWebSockets` guards and shapes every WebSocket an
endpoint accepts through `context.WebSockets` (`Assimalign.Cohesion.Http.WebSockets`).

## Scope

- **Cross-site WebSocket hijacking defense.** A handshake whose `Origin` is present and is neither
  the request's own origin (from the effective, proxy-resolved scheme and host) nor one of
  `AllowedOrigins` is refused with `403`. A handshake without `Origin` passes.
- **RFC 6455 refusals.** A malformed handshake gets `400`; one for another version gets `426` with
  `Sec-WebSocket-Version: 13`.
- **Accept defaults.** Every accept downstream takes the keep-alive interval and timeout, and the
  compression switch, unless it sets its own. Compression stays off unless enabled.
- **Drain close.** When the default server begins its drain, every open socket is closed with
  `1001 Going Away`, inside the stop's budget.

## Usage

```csharp
using System.Net.WebSockets;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.WebSockets;

WebApplication app = builder.Build();

// Behind a proxy, register UseForwardedHeaders(...) first: the origin check reads the effective
// scheme and host it resolves.
app.UseWebSockets(options =>
{
    options.AllowedOrigins.Add("https://app.example");   // besides the request's own origin
    options.KeepAliveInterval = TimeSpan.FromSeconds(20);
});

app.MapGet("/chat", async (IHttpContext context) =>
{
    if (!context.WebSockets.IsWebSocketRequest)
    {
        context.Response.StatusCode = HttpStatusCode.BadRequest;
        return;
    }

    using WebSocket socket = await context.WebSockets.AcceptWebSocketAsync(cancellationToken: context.RequestCancelled);
    // ...receive and send until the close handshake; complete it with CloseAsync.
}).DisableRequestTimeout();
```

The endpoint's own `CloseAsync` or `CloseOutputAsync` keeps working after a drain close: the policy
coordinates the two, so a receive loop written the usual way ends cleanly.

## Options

| Option | Default | Meaning |
| --- | --- | --- |
| `AllowedOrigins` | empty | Serialized origins, besides the request's own, whose pages may open a socket; validated when `UseWebSockets` runs |
| `AllowAnyOrigin` | `false` | Turns the hijacking defense off, for sockets that carry no ambient credentials |
| `KeepAliveInterval` | 30 seconds | How often an idle socket sends a keep-alive frame |
| `KeepAliveTimeout` | infinite | How long to wait for a pong before aborting; infinite sends unsolicited pongs |
| `DangerousEnableCompression` | `false` | Whether accepts negotiate permessage-deflate when the client offers it |

## Dependencies

- `Assimalign.Cohesion.Web` — the pipeline seams and the server drain feature (`IWebServerDrainFeature`).
- `Assimalign.Cohesion.Http.WebSockets` — the handshake, the negotiation and the accept.
- `Assimalign.Cohesion.Http.Forwarded` — `EffectiveScheme` and `EffectiveHost` for the same-origin check.
- `Assimalign.Cohesion.Http` — the protocol core.

It references nothing in the hosting family; the drain signal reaches it through the Web root's
feature contract. See [DESIGN.md](./DESIGN.md).
