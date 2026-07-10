# Assimalign.Cohesion.Web.CookiePolicy &mdash; Design

## Design intent

`Web.CookiePolicy` is the request-pipeline middleware that enforces a
site's **cookie policy** on top of the wire-level cookie model in
`Assimalign.Cohesion.Http.Cookies`. Where the model library answers
*"is this cookie well-formed on the wire?"*, this package answers
*"is this well-formed cookie **allowed**, and if not, is it rejected or
rewritten?"* — the decisions that depend on deployment posture rather
than on the RFC grammar.

The middleware is installed builder-time via `UseCookiePolicy(...)` and
intercepts the response cookie collection (and, where relevant, the
request collection) surfaced by `Http.Cookies`, applying the configured
`CookiePolicyOptions` before the transport drains cookies into
`Set-Cookie` headers.

## The model / policy split (RFC 6265 / 6265bis)

The split is deliberate and documented on both sides (see
`Assimalign.Cohesion.Http.Cookies/docs/DESIGN.md` → "Wire-safety
hardening"). The rule of thumb: *wire-safety and well-formedness* live in
the model; *site policy* lives here.

| RFC 6265 / 6265bis rule | Owner | Rationale |
|---|---|---|
| `cookie-name` token / `cookie-value` octet-grammar validation (anti header-splitting) | **`Http.Cookies`** (model) | Well-formedness; a malformed value corrupts the wire regardless of policy. Enforced at `HttpCookie` construction. |
| Per-cookie size limits (name+value ≤ 4096, attribute value ≤ 1024, bounded attribute count) | **`Http.Cookies`** (model) | Wire/parse robustness; oversized input is ignored during parsing via `HttpCookieLimits`. |
| 400-day lifetime **cap math** (`HttpCookie.ClampLifetime`) | **`Http.Cookies`** (model) | Pure, clock-free mechanism, deterministically testable. |
| **Applying** the lifetime cap to outbound cookies at emission (supplying "now") | **`Web.CookiePolicy`** (policy) | Deciding to override an app's explicit lifetime — and reading the clock — is a policy/emission act. |
| `__Host-` / `__Secure-` **prefix invariants** (require `Secure`; `__Host-` also requires `Path=/` and no `Domain`) | **`Web.CookiePolicy`** (policy) | Encodes a site's trust decision, not the wire grammar. |
| `SameSite=None` **requires** `Secure` (reject or upgrade) | **`Web.CookiePolicy`** (policy) | A deployment-posture pairing rule (reject vs. upgrade is configurable). |

The model provides the *mechanisms*; this middleware decides *whether and
when* to apply the policy-shaped ones and owns the prefix/pairing rules
outright.

## Dependency direction

One-way: `Web.CookiePolicy` → `Assimalign.Cohesion.Web` (pipeline) +
`Assimalign.Cohesion.Http.Cookies` (model) → `Assimalign.Cohesion.Http`
(core). The model never gains a back-reference to the policy layer, which
is what lets the policy compose the model's mechanisms freely. Enforcement
is expected to intercept via `IHttpResponseCookieFeature` /
`IHttpRequestCookieFeature` replacement, as documented in the
`Http.Cookies` README.

## AOT posture

`<IsAotCompatible>true</IsAotCompatible>`. Policy enforcement is plain
string/span comparison (prefix checks, `Secure`/`SameSite` inspection) and
reuse of the model's `ClampLifetime` — no reflection, no regex-with-
compilation, no runtime code generation.

## Status and non-goals

- **Current status: stub.** `CookiePolicyOptions` is empty and
  `UseCookiePolicy(...)` is a pass-through that installs no enforcement
  yet. The cookie model hardening it composes (octet-grammar validation,
  `HttpCookieLimits`, `HttpCookie.ClampLifetime`) shipped with issue
  [L01.01.11.30]; the middleware that consumes them is tracked under the
  Web platform epic (cookie security, issue #30) and is **not** delivered
  by [L01.01.11.30]. This document records the intended split so that
  future work implements the policy side without re-litigating the
  boundary.
- **Not the wire model.** This package never re-implements cookie parsing,
  serialization, octet validation, size limits, or the lifetime-cap math —
  those are `Http.Cookies`' job and are consumed, not duplicated.
- **Not signing / encryption.** Confidentiality/integrity of cookie
  payloads is a separate concern (a future `Http.Cookies.Signing`), not
  policy enforcement.
