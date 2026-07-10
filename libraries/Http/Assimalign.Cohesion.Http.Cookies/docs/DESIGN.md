# Assimalign.Cohesion.Http.Cookies &mdash; Design

## Design intent

Restore the familiar `request.Cookies` / `response.Cookies` property
ergonomics on top of a protocol core that doesn't ship a typed cookie
model. The package layers a parsed cookie collection and a per-exchange
feature pair on top of the wire-level `Cookie` and `Set-Cookie` headers
that the core already exposes, without re-coupling the protocol
abstractions to the cookie types.

The design is shaped by three constraints that pull against each other:

1. The protocol core (`Assimalign.Cohesion.Http`) must stay free of
   cookie types so that protocol-only consumers (HTTP clients, reverse
   proxies, edge caches, observability layers, the DNS-over-HTTPS
   transport, &hellip;) don't pay for an application-layer convenience
   they never use.
2. Application code that does need cookies wants `request.Cookies` and
   `response.Cookies` as properties, not `request.GetCookies()` or a
   collection accessed through some context-side bag.
3. The transports still need to read the `Cookie` header into a
   structured collection (when something asks) and drain queued
   `HttpCookie` instances into `Set-Cookie` headers on response flush
   &mdash; both are wire-level operations that have to happen even
   when no application code reads `request.Cookies` first.

The .NET 10 `extension(...)` property syntax plus a request/response
feature pair on `IHttpContext.Features` resolves all three.

## Why two features, not one

Sessions, authentication, and forms each ship a single feature
(`IHttpSessionFeature`, `IHttpAuthenticationFeature`, `IHttpFormFeature`)
because their per-exchange state is conceptually one object that
might be read or written. Cookies are different: the request side is
**parsed input** (a snapshot of what arrived on the wire) and the
response side is **queued output** (a list of cookies pending
emission). The two have different lifetimes, different write semantics,
and different middleware concerns &mdash; signing the response cookies
doesn't imply parsing the request cookies, and a cookie-policy middleware
that mutates inbound cookies before authentication doesn't want to
touch outbound state.

ASP.NET Core makes the same split (`IRequestCookiesFeature` /
`IResponseCookiesFeature`) for the same reason. The Cohesion shape
mirrors that split.

## Why parsing is lazy

The transport message readers used to call `ParseCookies(headers)` at
request-construction time, install the result on
`Http1Request`/`Http2Request`/`Http3Request`, and hand the populated
collection to the application. That made every request pay the
tokenization cost even when no application code ever reads cookies
&mdash; a protocol-only proxy that just shuttles bytes through the
stack was tokenizing cookies it would never look at.

Lazy parsing in the extension property pushes the work onto the first
read. Requests that never touch `Cookies` allocate nothing for cookie
handling. Requests that do read cookies pay the parse cost once and
cache the result in `IHttpRequestCookieFeature` for subsequent reads.

## Why the parse is cached even when the Cookie header is empty

The first read of `request.Cookies` always installs the feature, even
when the result is an empty collection. Two reasons:

1. **Idempotent semantics.** A second read returns the same instance,
   regardless of what was parsed. Without the always-install, a no-cookie
   request would re-allocate an empty collection on every read.
2. **Observability.** The presence of `IHttpRequestCookieFeature`
   signals "cookies have been observed for this exchange" &mdash;
   middleware can branch on `context.Features.Get<IHttpRequestCookieFeature>()
   is not null` to detect whether anything has touched cookies yet,
   independent of whether the parse returned content.

## Why the response feature is also lazy

`response.Cookies` only installs `IHttpResponseCookieFeature` on first
read, not eagerly at context construction. A response that emits no
cookies (the overwhelming majority) costs nothing for cookie state.
The transport's response writers explicitly handle the "no feature"
case as "no `Set-Cookie` headers to emit" &mdash; no allocations, no
enumeration.

## How the transports stay decoupled from cookie semantics

`Assimalign.Cohesion.Http.Connections` depends on this package, but
only for the type identity (the `HttpCookie.ToString()` serialization
and the feature lookup). The transports do not parse the `Cookie`
header themselves (that's the request extension's job), and they do
not own the typed collection (that's the response feature's job).
They only:

1. **Reach into the response feature** at write time
   (`context.Features.Get<IHttpResponseCookieFeature>()`) and, when
   present, emit one `Set-Cookie` line per queued cookie.
2. **Honour the RFC 6265 §3 rule** that each `Set-Cookie` value MUST
   be on its own line; no comma folding.

The wire-level `Cookie` header remains on `request.Headers` whether
or not the cookies package is referenced. A consumer that never reads
`request.Cookies` doesn't load the cookies package and doesn't pay
its allocation cost &mdash; but the raw `Cookie` header is still
available for inspection.

## Why the same `IHttpCookieCollection` for both sides

A read-only collection on the request side and a write-only collection
on the response side (ASP.NET Core's `IRequestCookieCollection` /
`IResponseCookies` split) is more precise, but it doubles the surface
and forces callers to learn two shapes for what is conceptually the
same data type.

The single `IHttpCookieCollection : ICollection<HttpCookie>` here is
mutable on both sides. The request side initialises it from the parse
output (callers can in theory mutate, but the cached collection is
discarded at end-of-exchange and middleware that wants to alter inbound
cookies should install a custom feature). The response side starts
empty and accumulates cookies until the transport drains it. The
trade-off favours uniform call-site shape over strict directional
typing; a future PR can introduce direction-specific interfaces if
real consumers prove the looser shape is a footgun.

## Family map

| Package | Role | Dependencies |
|---------|------|---------------|
| `Assimalign.Cohesion.Http.Cookies` | Typed cookie model + request/response features + extensions | `Assimalign.Cohesion.Http` |
| `Assimalign.Cohesion.Http.Connections` | Wire-level HTTP/1.1, HTTP/2, HTTP/3 readers + writers | `Assimalign.Cohesion.Http.Cookies` (for `Set-Cookie` emission) |
| Future: `&hellip;Http.Cookies.Signing` | Signed-cookie middleware | This package |
| Future: `&hellip;Web.Authentication.Cookie` | Cookie-scheme auth handler | This package + Web.Authentication |

Dependency direction is one-way: cookie-aware packages depend on this
one; this one depends only on the protocol core. The protocol core
never gains a back-reference.

## Wire-safety hardening (RFC 6265bis) &mdash; the model / policy split

The package owns the RFC 6265 / 6265bis rules that make a cookie
**well-formed on the wire**. It deliberately does *not* own the rules
that decide whether a well-formed cookie is **allowed by policy**. The
dividing line is: *"could this corrupt the header or the store?"* is a
model concern; *"should this cookie be permitted / rewritten for this
site?"* is a policy concern. Three model-level guards were added under
issue [L01.01.11.30]:

1. **Octet-grammar validation at construction (anti header-splitting).**
   `HttpCookie` validates its `Name` against the RFC 6265 §4.1.1
   `token` grammar and its `Value` against `*cookie-octet` (optionally a
   single surrounding DQUOTE pair) in the constructor, throwing
   `ArgumentException` on a violation. Because `Name`/`Value` are
   immutable, a constructed cookie can *never* hold a `;`, `,`,
   whitespace, control character, or CR/LF octet — so `ToString()` can
   never emit a value that splits or corrupts the `Set-Cookie` line, and
   the request-side fold (`name=value; …`) is equally safe. This is an
   argument-validation error (a server bug), not a request rejection, so
   it surfaces as `ArgumentException` rather than an area `HttpException`
   with an `HttpErrorCode` (those are reserved for transport-level
   request rejection). The grammar itself lives in the internal
   `HttpCookieGrammar` helper as `SearchValues<char>` membership scans —
   no regex, no allocation, AOT-safe.

2. **Configurable parse-side size limits (`HttpCookieLimits`).**
   `HttpCookieCollection` parsing enforces RFC 6265bis-aligned defaults:
   name+value ≤ 4096 octets, per-attribute value ≤ 1024 octets, and a
   bounded retained-attribute count (default 50, a DoS backstop that
   keeps a hostile `Set-Cookie` from growing the attribute/extension list
   without bound). Oversized cookies and attributes are **ignored**
   (dropped) rather than throwing, matching the RFC's parsing-robustness
   posture — a malformed inbound header must not crash the server. The
   limits are a constructor parameter (`HttpCookieLimits.Default` when
   omitted), so a host can tune or effectively disable a dimension
   (`int.MaxValue`). The outer bound on total header length stays with
   the transport's request/response header-size limits; these are the
   per-cookie inner defense.

3. **Lifetime cap mechanism (`HttpCookie.ClampLifetime`).** The model
   supplies the RFC 6265bis §5.5 400-day constant
   (`HttpCookieLimits.DefaultMaxLifetime`) and the *pure* clamping math:
   `ClampLifetime(referenceTime[, maxLifetime])` returns a cookie whose
   `Max-Age` is reduced to the cap when longer and whose `Expires` is
   pulled back to `referenceTime + cap` when further out, while a zero or
   negative `Max-Age` (a deletion signal, RFC 6265 §5.2.2) and a
   past/near `Expires` round-trip untouched. Clamping is a pure function
   with no hidden clock: **deciding when to apply it** — at emission,
   against the current time — is an emission/policy responsibility, so
   the mechanism lives here and the "apply the cap to outbound cookies"
   trigger is left to the emitter / `Web.CookiePolicy`. Keeping the clock
   out of the model is what makes the cap deterministically testable.

**What stays here vs. what is `Web.CookiePolicy`'s job.** Everything above
is wire-safety / well-formedness and lives in this model library. The
following are *policy* and are explicitly **not** implemented here — they
belong to `Assimalign.Cohesion.Web.CookiePolicy` (see its `docs/DESIGN.md`)
because they encode a site's decision about acceptable cookies, not the
wire grammar:

- `__Host-` / `__Secure-` **prefix invariants** (require `Secure`, and for
  `__Host-` also `Path=/` with no `Domain`).
- **`SameSite=None` requires `Secure`** pairing (reject or upgrade).
- **Applying** the 400-day lifetime cap to outbound cookies at emission
  time (the model provides the mechanism; the middleware decides to run it
  and supplies "now").

The one-way dependency direction (`Web.CookiePolicy` → `Http.Cookies` →
`Http` core, never a back-reference) is what lets the policy layer compose
these model mechanisms without the model ever learning about policy.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` is inherited from the shared
build targets. The package contains no reflection, no runtime code
generation, and no dynamic type loading. The feature lookup uses
`typeof(IHttpRequestCookieFeature)` / `typeof(IHttpResponseCookieFeature)`
as JIT-time constant keys into the underlying
`Dictionary<Type, object>`; trim and AOT roots are unaffected. The
parser is a string-tokenization loop with no regex, and the octet-grammar
guard (`HttpCookieGrammar`) classifies characters with
`SearchValues<char>` membership scans — again no regex, no reflection.

## Non-goals

- **Cookie attribute parsing on the request side.** RFC 6265 §4.2.1
  is explicit that user agents send cookies as bare `name=value` pairs
  on the wire &mdash; attributes are server-set output, not client
  input. The request parser deliberately doesn't try to round-trip
  attributes back from the `Cookie` header.
- **Cookie signing / encryption.** A future package
  (`Assimalign.Cohesion.Http.Cookies.Signing` or similar) layers on
  top of this one. The base package stays focused on the typed model
  and the wire/feature plumbing.
- **Cookie-policy enforcement.** Middleware that strips, rewrites, or
  rejects cookies based on policy (Secure-only, `SameSite=None`+`Secure`
  pairing, `__Host-`/`__Secure-` prefix enforcement, and *deciding when*
  to apply the 400-day lifetime cap to outbound cookies) belongs in a
  dedicated package (`Assimalign.Cohesion.Web.CookiePolicy`) that's
  allowed to depend on this one. This library provides the wire-safety
  *mechanisms* (octet-grammar validation, size limits, the
  `ClampLifetime` math); the policy layer decides *whether and when* to
  apply the policy-shaped ones. See "Wire-safety hardening" above for the
  full split.
- **Direction-specific interfaces.** As discussed above, a future
  refactor may introduce `IRequestCookieCollection` /
  `IResponseCookies` if real call sites prove the unified shape is
  error-prone. For now, the single `IHttpCookieCollection` keeps the
  surface compact.
