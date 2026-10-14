# Assimalign.Cohesion.Http.Cookies

This package supplies the cookie model, request and response cookie
collections, and their per-exchange features. It depends on
`Assimalign.Cohesion.Http`; cookie-aware Web features depend on it.
Protocol-only consumers can continue to use the raw headers without
referencing this package.

`HttpCookieName`, `HttpCookieValue`, `HttpCookiePath`, and `HttpCookieDomain`
validate RFC 6265 grammar at construction. String conversions make them
usable in the cookie model without repeating grammar checks at every call
site:

```csharp
HttpCookie cookie = new("session", "token", new HttpCookieOptions
{
    Path = "/",
    Domain = "example.com",
    Secure = true,
    HttpOnly = true
});
```

For candidate input, use static validation or non-throwing creation:

```csharp
bool valid = HttpCookieName.IsValid(candidate, out string? message);
if (HttpCookieName.TryCreate(candidate, out HttpCookieName name))
{
    HttpCookie cookie = new(name, "token");
}
```

`TryCreate` returns false for invalid or null input and sets its result to
default. A default value object holds no string and bypasses validation.
Defaults compare and hash safely, sort first, and return an empty string
from `ToString()`. A boundary that requires a value must still refuse
default: `HttpCookie` rejects a default name. Optional path and domain
attributes can be cleared with null.

Ordering is ordinal and independent of culture. Domain equality, hashing,
and ordering ignore case; names, values, and paths remain case-sensitive.
The original domain spelling is preserved on the wire. JSON null maps to
default, invalid JSON text produces `JsonException`, and generated
converters support dictionary keys.

See [DESIGN.md](DESIGN.md) for grammar details, lazy feature behavior,
default semantics, the model/policy boundary, and the changes in #1472.
