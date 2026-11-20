# Web middleware order

Middleware runs in registration order. Each Web package documents its own position, but read one
at a time, five of them claim the front of the pipeline. This page merges those constraints into
one order and says why each middleware goes where it does. The Web NativeAOT guard
([`Program.cs`](../../../resources/Web/Assimalign.Cohesion.Web.Hosting/samples/Assimalign.Cohesion.Web.AotGuard/Program.cs))
registers its middleware in this order.

When a package's ordering constraint changes, update this page in the same commit.

## The order

An application registers the middleware it uses in this order and leaves out the rest. In the
diagram an arrow reads "runs before"; the table gives the reason for each position. The dotted arrow
is the forward proxy, which runs in a branch of its own, or on a port of its own, and never returns
to the rest of the order (row 2).

```mermaid
flowchart TD
    subgraph Outer["Wrappers that write no response of their own"]
        Logging["UseHttpLogging"] --> Headers["UseSecurityHeaders"]
    end
    Logging -.->|"own port or branch"| ForwardProxy["UseForwardProxy, terminal"]
    subgraph Identity["Client identity and transport policy"]
        Forwarded["UseForwardedHeaders"] --> Hosts["UseHostFiltering"] --> Https["UseHttpsRedirection"] --> Hsts["UseHsts"]
    end
    subgraph Shaping["Exception boundary and request shaping"]
        Errors["UseErrorHandling"] --> ReverseProxy["UseReverseProxy"] --> Pages["UseStatusCodePages"] --> Rewrite["UseRewrite"] --> Cookies["UseCookiePolicy"] --> Compress["UseResponseCompression"] --> Decompress["UseRequestDecompression"] --> Static["UseStaticFiles"] --> Authn["UseAuthentication"] --> Sessions["UseSessions"]
    end
    subgraph Policy["Endpoint policy: reads the endpoint UseRouting published"]
        Routing["UseRouting"] --> Cors["UseCors"] --> Authz["UseAuthorization"] --> Timeouts["UseRequestTimeouts"] --> Limits["UseRateLimiting"] --> Forms["UseForms"] --> Csrf["UseAntiforgery"] --> Cache["UseOutputCache"] --> Sockets["UseWebSockets"] --> Endpoint["Endpoint, run by the pipeline terminal"]
    end
    Headers --> Forwarded
    Hsts --> Errors
    Sessions --> Routing
```

| # | Verb | Package | Why it goes here |
| --- | --- | --- | --- |
| 1 | `UseHttpLogging` | Web.Diagnostics | First, so the exchanges every later middleware rejects are logged too. It writes its entry after the pipeline unwinds, when `UseForwardedHeaders` has already attached the forwarded identity, so running ahead of that middleware loses nothing. |
| 2 | `UseForwardProxy` | Web.Proxy | Only in a branch of its own, such as `UseWhen` on the proxy's port, or in an application that serves nothing else: it is terminal and serves every request that reaches it as a proxy request. Right after `UseHttpLogging`, so proxied exchanges are logged, a tunnel's entry when it closes. Ahead of everything else, because nothing else applies to a proxy request: security headers would be added to other sites' responses, host filtering would refuse every target its allowlist does not name, and HTTPS redirection would redirect a plaintext proxy request. Forwarded headers never decide where it connects. |
| 3 | `UseSecurityHeaders` | Web.SecurityHeaders | Early enough to cover rejections and redirects from later middleware. Once registered, its starting callback also covers an error page from a boundary on either side. It reads no client identity, so it may run ahead of `UseForwardedHeaders`. |
| 4 | `UseForwardedHeaders` | Web.ForwardedHeaders | Ahead of every middleware that reads the effective scheme, host or client address on the way in. Leave it out when no proxy sits in front of the application. |
| 5 | `UseHostFiltering` | Web.HostFiltering | Validates the effective host, so it follows `UseForwardedHeaders`. It runs ahead of everything that uses the host: the redirect `Location`, absolute URLs, cache keys. |
| 6 | `UseHttpsRedirection` | Web.HttpsPolicy | Redirects a plaintext request before anything does work for it, building the `Location` from the validated host. Behind a TLS-terminating proxy, see [Recognizing a redirect loop](../../../resources/Web/Assimalign.Cohesion.Web.HttpsPolicy/docs/DESIGN.md#recognizing-a-redirect-loop) if every route returns `307` or `308`. |
| 7 | `UseHsts` | Web.HttpsPolicy | Ahead of the exception boundary, so the header survives a reset error response. |
| 8 | `UseErrorHandling` | Web.ErrorHandling | The exception boundary: a fault anywhere after it becomes a problem-details response. |
| 9 | `UseReverseProxy` | Web.Proxy | Inside the exception boundary. After `UseForwardedHeaders` and `UseHostFiltering`, because its routes match the validated effective host, and after `UseHttpsRedirection`, so a plaintext request is redirected before it is forwarded. Ahead of `UseStatusCodePages`, which would add a body to a relayed bodyless `404`, and of every middleware that changes a response (rewrite, cookies, compression, caching), so a relayed response reaches the client as the destination sent it; the wrappers ahead of it still add their headers, as an edge should. A request no route takes goes on to them. Its routes match the client's path, not a rewritten one. It runs ahead of authentication, authorization and rate limiting, so a forwarded request passes none of them until routes become endpoints (#1897). |
| 10 | `UseStatusCodePages` | Web.ErrorHandling | Inside the boundary and ahead of the middleware whose bodyless `4xx`/`5xx` responses it fills in, including the `404` and `405` from routing. |
| 11 | `UseRewrite` | Web.Rewrite | Ahead of everything after it that reads the path (static files, routing, the endpoint), so they all see the rewritten URL; middleware ahead of it, `UseReverseProxy` included, and the server's request telemetry, keep the client's. Inside the boundary and after status-code pages, so a fault in a rule becomes a problem-details response and its `400` gets a body. After `UseForwardedHeaders` and `UseHostFiltering`, so its canonicalization redirects read the client's scheme and a validated host. Inside it, register redirects ahead of internal rewrites. |
| 12 | `UseCookiePolicy` | Web.CookiePolicy | Ahead of every middleware that writes a cookie: sessions, the authentication cookie, antiforgery. Its `Secure` decision reads the effective scheme. |
| 13 | `UseResponseCompression` | Web.Compression | Here when the application does not use output caching. With `UseOutputCache` it moves after the cache (row 25). |
| 14 | `UseRequestDecompression` | Web.Compression | Ahead of every middleware that reads the request body. |
| 15 | `UseStaticFiles` | Web.StaticFiles | Ahead of `UseRouting`, so an existing file is served without routing or authentication. |
| 16 | `UseAuthentication` | Web.Authentication | Anywhere ahead of `UseAuthorization`. In this position, branches and middleware ahead of routing also see `context.User`. |
| 17 | `UseSessions` | Web.Sessions | After `UseCookiePolicy`, which applies the policy to the session cookie. The session loads on first use, so a request that never touches it costs nothing. |
| 18 | `UseRouting` | Web.Routing | Publishes the matched endpoint and calls `next`; the pipeline terminal runs the endpoint. Every middleware after it can read the endpoint. `RequireHost` routes match the effective host, so behind a proxy that rewrites `Host` routing must follow `UseForwardedHeaders`. |
| 19 | `UseCors` | Web.Cors | Ahead of every middleware that can reject a preflight. A preflight carries no credentials, so authorization would answer it `401`, a rate limit `429` and antiforgery `400`. |
| 20 | `UseAuthorization` | Web.Authorization | After authentication, and ahead of output caching so an unauthorized request is never served from the cache. |
| 21 | `UseRequestTimeouts` | Web.RequestTimeouts | Ahead of the work it bounds. The rate limiter waits for a permit on the request's cancellation token, so a timeout here also cuts off a request still queued for a permit. |
| 22 | `UseRateLimiting` | Web.RateLimiting | Ahead of the expensive middleware, so excess requests are rejected before any body is read or token decrypted. |
| 23 | `UseForms` | Web.Forms | Optional, because it parses every request. It goes after the limits, so rejected requests are never parsed, and ahead of `UseAntiforgery`, which reuses the parsed form. |
| 24 | `UseAntiforgery` | Web.Antiforgery | After the limits, and inside the timeout so its form read is bounded. |
| 25 | `UseOutputCache` | Web.Caching | After authorization. `UseResponseCompression` comes right after it, so the cache stores and replays the compressed bytes. Ahead of `UseWebSockets` safely: it passes every protocol switch (an `Upgrade` request, a `CONNECT`) through untouched, never answering a handshake from the cache or storing a taken-over exchange. |
| 26 | `UseWebSockets` | Web.WebSockets | After `UseForwardedHeaders`, whose effective scheme and host its same-origin check reads, and ahead of every endpoint that accepts a socket. Last, so host filtering, authorization and rate limiting apply to a handshake first; it refuses a cross-site or malformed handshake before the endpoint runs. Map socket endpoints with `MapWebSocket`, which routes the HTTP/1.1 `GET` and the HTTP/2 and HTTP/3 `CONNECT` handshakes alike (a `MapGet` socket fails over HTTP/2) and applies the default policy itself when this middleware is absent. A request timeout cancels a socket's endpoint when it fires, so WebSocket endpoints disable it (`DisableRequestTimeout()`). |

## What fails closed, and what does not

Endpoint metadata that needs a middleware to enforce it fails the endpoint at dispatch when that
middleware is missing or registered ahead of `UseRouting`; the exception names the middleware. This
covers CORS, authorization, request timeouts, rate limiting and antiforgery (Web.Routing DESIGN,
"Endpoint metadata consumers and ordering"). Nothing checks the order of any other two middleware.
A `UseCookiePolicy` registered after `UseSessions`, or a `UseHostFiltering` registered ahead of
`UseForwardedHeaders`, runs without error and quietly weakens what it protects. Enforced ordering
is the open #26/#145 work.

## Response-starting headers

`UseSecurityHeaders` and `UseCors` register callbacks on Web.Server's `IWebResponseStartingFeature`.
The server applies them before committing a buffered or streamed head, after any boundary has replaced
an error response. A boundary ahead of `UseCors` therefore keeps CORS headers on a fault response once
CORS has processed that request. Responses that short-circuit before reaching either middleware do not
register its callback. CORS still stages `Vary: Origin` before `next` so output caching can partition
its stored responses before the transport commits their headers.

## Outside this order

- **Branches.** `Map`, `MapWhen` and `UseWhen` run after whatever is registered ahead of them. A
  branch registered ahead of `UseRouting` sees no endpoint, and its own pipeline registers any of
  these middleware it needs.
- **Application middleware**, including Web.Query's `UseQueryValidation` and
  `UseQueryConditionals`, goes after the endpoint policy middleware, closest to the endpoint, unless
  it has to answer before one of them.
