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

`Assimalign.Cohesion.Http.Connections` does not reference this package.
The transports do not parse the `Cookie` header themselves (that's the
request extension's job), and they do not own the typed collection
(that's the response feature's job). The response collection writes
every mutation through to `response.Headers[Set-Cookie]`, one header
value per cookie, so the transports only:

1. **Serialize the `Set-Cookie` header** like any other response
   field, at write time.
2. **Honour the RFC 6265 §3 rule** that each `Set-Cookie` value MUST
   be on its own line; no comma folding.

No serializer reads `IHttpResponseCookieFeature`. The HTTP/1.1 upgrade
writer in `Assimalign.Cohesion.Http.ProtocolUpgrade` was the last one: it
wrote the header's values comma-folded on one line and then each cookie of
the feature again, so a `101` sent every cookie twice. Since #1154 it
writes the header the way `Http1MessageWriter` does and takes no
dependency on this package. A replacement feature therefore has to keep
the header in sync, which it does by queuing into a header-synchronized
collection, as `Web.CookiePolicy` does; whatever the header holds is what
every response, the `101` included, sends.

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
| `Assimalign.Cohesion.Http.Connections` | Wire-level HTTP/1.1, HTTP/2, HTTP/3 readers + writers | None on this package: it serializes the `Set-Cookie` header the response collection maintains |
| `Assimalign.Cohesion.Http.ProtocolUpgrade` | HTTP/1.1 upgrade writer | None on this package: the `101` head serializes the same `Set-Cookie` header, one line per value (#1154) |
| `Assimalign.Cohesion.Web.Authentication.Cookie` | Cookie-scheme auth handler | This package + Web.Authentication |
| `Assimalign.Cohesion.Web.CookiePolicy` | Cookie-policy enforcement; replaces the response feature | This package |
| Future: `&hellip;Http.Cookies.Signing` | Signed-cookie middleware | This package |

Dependency direction is one-way: cookie-aware packages depend on this
one; this one depends only on the protocol core. The protocol core
never gains a back-reference.

## Wire-safety hardening (RFC 6265bis) &mdash; the model / policy split

The package owns the RFC 6265 / 6265bis rules that make a cookie
**well-formed on the wire**. It deliberately does *not* own the rules
that decide whether a well-formed cookie is **allowed by policy**. The
dividing line is: *"could this corrupt the header or the store?"* is a
model concern; *"should this cookie be permitted / rewritten for this
site?"* is a policy concern. The first three model-level guards were
added under issue [L01.01.11.30], the fourth under [L01.01.11.30.01]
(#1153), and the fifth under [L03.01.01.49.10] (#1474):

1. **Octet-grammar validation at construction (anti header-splitting).**
   `HttpCookie.Name` and `Value` are the `HttpCookieName` and
   `HttpCookieValue` value objects, which validate the RFC 6265 §4.1.1
   `token` grammar and `*cookie-octet` (optionally a single surrounding
   DQUOTE pair) when they are constructed, throwing `ArgumentException` on
   a violation. A `string` passed to the `HttpCookie` constructor is
   converted, and so checked, before the constructor runs. Because
   `Name`/`Value` are immutable, a constructed cookie can *never* hold a `;`, `,`,
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

4. **Attribute grammar in the `Path` and `Domain` value types (#1153).**
   `HttpCookie.ToString()` appends both values verbatim, so before #1153
   a path of `/x; SameSite=None` added an attribute to the line, and a
   control character corrupted it. Applications build these values from
   untrusted input (a tenant segment, a forwarded host), and
   `Web.CookiePolicy` trusts them, so the model has to refuse them.
   - **`Path`** must be an RFC 6265 §4.1.1 `path-value`: US-ASCII with
     no control character (`0x00`–`0x1F`, `0x7F`) and no `;`. RFC 6265bis
     writes the same set as `av-octet = %x20-3A / %x3C-7E`. The empty
     string is in the grammar.
   - **`Domain`** must be a `domain-value`: an RFC 1034 §3.5 `subdomain`
     as enhanced by RFC 1123 §2.1. That is dot-separated labels of 1–63
     letters, digits and hyphens that neither start nor end with a
     hyphen, at most 253 characters in all. One leading dot is tolerated
     because a user agent ignores it (RFC 6265 §5.2.3), and it does not
     count toward the 253. An empty label, a
     trailing dot, an underscore, a port, an IPv6 literal and a
     non-ASCII name are refused; an internationalized name is written as
     its `xn--` A-label.
   - **`null` stays legal for both**, because `CookiePolicyRules` repairs
     a `__Host-` cookie with `Domain = null` and the collection parser
     starts from `Path = null`. The properties are `HttpCookiePath?` and
     `HttpCookieDomain?`, and each value type declares a hand-written
     `string?` → `T?` conversion that keeps a `null` string `null`. Without
     it, C# routes a `null` string through the generated `string` → `T`
     conversion and wraps the result, which yields a non-null `T?` holding
     a null `Value` (or, once the type refuses null, an exception). An
     empty `Domain` also converts to `null`, meaning "no attribute", as
     `ToString()` has always read it and as RFC 6265 §5.2.3 tells a user
     agent to treat an empty `Domain=`; a constructed `HttpCookieDomain`
     is never empty. An empty `Path` is a valid `path-value` and is kept.
   - **The conversions throw `ArgumentException`** whose message names the
     attribute; `ParamName` is `value`, the conversion's own parameter,
     because the value type's constructor throws before the setter runs.
     The message does not echo the value, which may hold the control
     character that made it invalid. Like the name and value checks, this
     is a server bug surfacing, not a request rejection.
   - **Validation is in the type**, so every reader sees only valid
     values: the copy constructor (which copies the checked values),
     `HttpCookie`'s copy of its options, and a later write to
     `cookie.Options`.
   - **`default` is the one unchecked value.** `default(HttpCookieName)`
     and the other four hold a null `Value`. `HttpCookie` refuses a
     `default` name with `ArgumentNullException` and reads a `default`
     value as empty; `ToString()` omits a `default` `Path` or `Domain`.
     Generated equality and hashing accept this state: two defaults are
     equal, their hash code is zero, and default sorts before every
     constructed value, including an empty string. Each value object's
     own `ToString()` returns `string.Empty` for default. Default bypasses
     validation, so an accepting boundary still has to reject it when a
     value is required, as `HttpAntiforgery.Create` does for its cookie name
     and `HttpCookieOptions.Extensions` does for every item.
   - **The parser drops a cookie** whose `Path` or `Domain` attribute is
     outside the grammar, as it drops an out-of-grammar name or value,
     instead of throwing on wire input. It does not keep the cookie
     without the attribute: re-emitted that way, the cookie would have a
     different scope from the one its sender wrote. An empty `Domain=` is
     the exception; it is ignored, per §5.2.3.

5. **Typed extension attributes (#1474).** `HttpCookieExtension` accepts a
   non-empty RFC 6265 §4.1.1 `extension-av`: every character must be in
   `0x20`–`0x7E` and must not be `;`. Empty strings, non-ASCII characters,
   and all controls, including CR, LF, NUL, TAB and DEL, are refused.
   Spaces, `=`, commas, quotes and backslashes are allowed by this grammar.

   - `HttpCookieOptions.Extensions` is `IList<HttpCookieExtension>`.
     `options.Extensions.Add("Priority=High")` still compiles through the
     implicit string conversion, which throws `ArgumentException` for
     invalid text before the list changes. The diagnostic comes from
     `ErrorMessages` and never echoes the rejected input.
   - The internal collection also rejects `default(HttpCookieExtension)`
     from `InsertItem` and `SetItem`. This covers adding, inserting and
     replacing an item, since a default struct bypasses construction.
     The options copy constructor takes an independent typed snapshot;
     `HttpCookie` and cookie-policy rewrites preserve that behavior.
     `HttpCookie.ToString()` appends each extension's checked `.Value`.
   - The `Set-Cookie` parser uses `HttpCookieExtension.TryCreate` for
     unknown flags, unknown `name=value` attributes, and the extension
     fallbacks for unparseable `Expires`, `Max-Age` and `SameSite` values.
     It drops an extension it cannot represent and retains the rest of
     the cookie. Attribute parsing trims only SP and HTAB at segment,
     name and value boundaries; boundary CR/LF or other Unicode whitespace
     is not sanitized into a valid extension. Valid unknown attributes
     still round-trip, subject to the existing parse limits.
   - This is a grammar rule, not a reserved-name policy. A single
     `Domain=example.com` is a representable extension, as is a single
     `Priority=High`; a string such as `x; Domain=example.com` cannot add a
     second attribute. Applications choose which extension names and
     values they trust, and use the typed options for standard attributes.
     The transport still validates every response field at encode time
     (#1183).

**Breaking changes from 10.0.0-preview.1 (#1474).** The `Extensions` getter
changes from `IList<string>` to `IList<HttpCookieExtension>`, which breaks
binary compatibility and source that binds the collection as
`IList<string>`. Recompile consumers and use the typed list or project
each item's `.Value` when strings are needed. Direct `Add("Priority=High")`
calls remain source-compatible. Invalid strings previously accepted by
the list now throw at conversion; malformed received extension attributes
are dropped instead of being re-emitted. These changes follow decision
15's preview compatibility terms.

**Advisory triage (#1474).** The defect shipped in the
`Assimalign.Cohesion.Http.Cookies` library package in 10.0.0-preview.1:
an extension was appended verbatim after `; `, allowing embedded
semicolons to add attributes and controls to survive cookie serialization.
Unauthenticated remote exploitation requires an application to put
untrusted text into `Extensions`; the default framework flow does not do
so. The impact is cookie-attribute injection or response emission failure
(CWE-74 / CWE-113), conditional on that application dataflow.

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

## Generated value-object contract (#1472)

The five types are `readonly partial struct` declarations generated from
`CohesionValueType` items. Their handwritten partials own the grammar and
expose static `IsValid(string value, out string? message)`, so a caller can
validate a candidate without constructing an instance. The generated
`TryCreate(string? value, out T result)` returns false for null or invalid
input, with a default result. It validates once and constructs without
validating again. Constructors and implicit conversions continue to throw
`ArgumentException` with `ParamName = "value"` for invalid input.

Equality, hashing, and ordering use ordinal string comparison. Names,
values, paths, and extensions are case-sensitive. `HttpCookieDomain` declares
`ObjectComparison="OrdinalIgnoreCase"`, because RFC 6265 §5.1.3 compares
domain names without regard to case. This preserves the original spelling
for serialization; it does not strip the optional leading dot or perform
domain-suffix matching. `example.com` equals `EXAMPLE.COM`, while
`.example.com` remains a different stored value. Ordering is independent
of the current culture.

The generated JSON converter writes a default value as JSON null and reads
JSON null as default. Invalid cookie text throws `JsonException` with the
validation reason. The types also work as dictionary keys through the
converter's property-name methods; JSON property names cannot represent a
default null string. Applications using source-generated
`JsonSerializerContext` metadata keep serialization NativeAOT-safe.

The generator changes are breaking changes to the generated surface:
`IsValid` is static, string `ToString(format, provider)` is removed, ordering
is ordinal, domain equality and hashing ignore case, and invalid JSON now
uses `JsonException`. Default operations are now safe, and default JSON
round trips as null. These cookie value types were introduced after
10.0.0-preview.1; the shared generator's other consumers shipped in that
release. See the [value-type build contract](../../../../docs/build/MSBUILD_COHESION_PROPS.md)
for the complete metadata and generated surface.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>` is inherited from the shared
build targets. The package contains no reflection, no runtime code
generation, and no dynamic type loading. The feature lookup uses
`typeof(IHttpRequestCookieFeature)` / `typeof(IHttpResponseCookieFeature)`
as JIT-time constant keys into the underlying
`Dictionary<Type, object>`; trim and AOT roots are unaffected. The
parser is a string-tokenization loop with no regex, and the octet-grammar
guard (`HttpCookieGrammar`) classifies characters with
`SearchValues<char>` membership scans and character-range scans — again
no regex, no reflection.

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

## Exception message resources (#1467)

Resource keys name the failure with a short PascalCase noun phrase, at most 40 characters
(#1471). These internal names are independent of the preserved message text and placeholders.

Exception diagnostics live in `src/Properties/ErrorMessages.resx`. The shared build targets
use MSBuild strongly typed resource generation to produce an internal `ErrorMessages` class
in this assembly's `RootNamespace`, under the intermediate output directory. No designer source
is checked in, and a command-line or NativeAOT build uses the same embedded resources.
Formatted diagnostics use `string.Format(CultureInfo.CurrentCulture, ErrorMessages.<Name>, ...)`.
The invariant messages, exception types, error codes and existing input-redaction rules stay
unchanged; this introduces no public API or breaking behavior against 10.0.0-preview.1.
EventSource templates, log messages and HTTP response bodies keep their existing owners.
