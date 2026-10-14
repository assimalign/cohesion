# Assimalign.Cohesion.Http.Sessions — Overview

Per-exchange session state for the Cohesion HTTP family. The package provides the `IHttpSession`
binary store, typed string and integer accessors, the asynchronous `IHttpSessionStore` seam, and
an in-memory store. `Assimalign.Cohesion.Web.Sessions` supplies cookie establishment and the
per-request lifecycle through `UseSessions`.

## Dependencies and options

The package references core `Assimalign.Cohesion.Http` for contexts and features, and
`Assimalign.Cohesion.Http.Cookies` for the cookie option value types. `HttpSessionOptions` stays
independent of any particular session store.

```csharp
using Assimalign.Cohesion.Http;

HttpSessionOptions options = new()
{
    CookieName = ".Cohesion.Session", // HttpCookieName
    CookiePath = "/", // HttpCookiePath
    IdleTimeout = TimeSpan.FromMinutes(20),
};
```

`CookieName` requires a non-empty RFC 6265 token. `CookiePath` requires an RFC 6265 path-value:
US-ASCII with no semicolon or control characters; the empty path is valid. Invalid strings throw
`ArgumentException` from their implicit conversion when assigned, with parameter `value`.
`default` bypasses struct validation, so both Web.Sessions `UseSessions` overloads refuse it
before registering middleware, using the existing option-specific message and parameter `configure`.

Since 10.0.0-preview.1, the `CookieName` and `CookiePath` property types change from `string` to
`HttpCookieName` and `HttpCookiePath` (#1473). Valid string assignments keep their spelling; code
that requires a string-specific member uses `.Value`. The property-type changes break binary
compatibility. Other session options retain their types.

See the [package README](../README.md) for session access examples and the [design](DESIGN.md)
for storage, lifetime, and compatibility details.
