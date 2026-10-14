# Assimalign.Cohesion.Http.Cookies

This package supplies the cookie model, request and response cookie
collections, and their per-exchange features. It depends on
`Assimalign.Cohesion.Http`; cookie-aware Web features depend on it.
Protocol-only consumers can continue to use the raw headers without
referencing this package.

`HttpCookieName`, `HttpCookieValue`, `HttpCookiePath`, `HttpCookieDomain`,
and `HttpCookieExtension` validate RFC 6265 grammar at construction.
String conversions make them usable in the cookie model without repeating
grammar checks at every call site:

```csharp
HttpCookie cookie = new("session", "token", new HttpCookieOptions
{
    Path = "/",
    Domain = "example.com",
    Secure = true,
    HttpOnly = true
});
```

`HttpCookieOptions.Extensions` is an `IList<HttpCookieExtension>`. Add a
single extension attribute with the implicit string conversion:

```csharp
cookie.Options.Extensions.Add("Priority=High");
cookie.Options.Extensions.Add("Partitioned");
```

An extension must be non-empty ASCII `0x20`–`0x7E`, excluding `;`.
Invalid strings throw when converted for addition or replacement, and
the collection also rejects `default(HttpCookieExtension)`. This prevents
one extension from injecting another attribute or a line break. It does
not reserve attribute names; applications still choose the extension
names and values they trust. Copying cookie options copies the typed
list, and serialization writes each item's `.Value`.

When reading `Set-Cookie`, valid unknown attributes round-trip. Invalid
extensions are dropped without discarding the cookie or throwing. The
parser trims SP and HTAB around attributes but does not trim away CR/LF.

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
default: `HttpCookie` rejects a default name, and the extension collection
rejects default items. Optional path and domain attributes can be cleared
with null.

Ordering is ordinal and independent of culture. Domain equality, hashing,
and ordering ignore case; names, values, paths, and extensions remain
case-sensitive. The original domain spelling is preserved on the wire.
JSON null maps to default, invalid JSON text produces `JsonException`,
and generated converters support dictionary keys.

The extension collection's type change from `IList<string>` is a source
and binary break from 10.0.0-preview.1; recompile consumers and use the
typed list or project `.Value` for string readers. Direct string `Add`
calls still compile, but invalid text now throws before it enters the list.

See [DESIGN.md](DESIGN.md) for grammar details, lazy feature behavior,
default semantics, the model/policy boundary, and the changes in #1472
and #1474.
