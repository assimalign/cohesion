# Value Object Adoption: Scope

**Date:** 2026-10-14. **Status:** proposed; decisions V1–V7 (§5) await the owner.
**Prompt:** the owner's cookie refactor (cb113e55a) and the request to find other places that should validate input through `<CohesionValueType>` value objects, and to improve the generator.
**Program:** HTTP/Web PR #1094 cleanup (decision 59, feature #1463).
**Tracking:**
- generator: #1472;
- Http/Web adoption: #1473–#1479;
- other areas (§4.2) are not filed yet; V1 decides whether they are.

Claims are tagged **[Certain]** (the orchestrator checked them against the code at `4e3b9f949`, most by running it), **[Certain, survey]** (a survey checked the code and the orchestrator did not re-check), **[Likely]** or **[Guessing]**.

## 1. Findings first

### 1.1 `default` is the hole in the type-system guarantee, and it crashed

- **[Certain] `default` skips validation.** A struct value object always has a `default` that never ran `IsValid`. The owner's refactor already relies on a boundary check for it: `HttpAntiforgery.Create` refuses `options.CookieName.Value is null`. Value objects move a grammar into the type. They do not remove the boundary check wherever "no value" is invalid.
- **[Certain] The generated types crashed on it.** For the four cookie types, `default(T) == default(T)`, `GetHashCode` and `<` threw `NullReferenceException`, and so did `new HashSet<HttpCookieName> { default }`. String ordering also followed the current culture: under `da-DK`, `(HttpCookieName)"aa" < "z"` was false. #1472 fixes both.
- **[Certain] Six hand-written core Http types have the same defect.** `HttpHeaderKey`, `HttpMethod`, `HttpPath`, `HttpHost`, `HttpQueryKey` and `HttpQuery` throw `ArgumentNullException` from `GetHashCode()` at `default`, so `new Dictionary<HttpHeaderKey, int> { [default] = 1 }` throws. #1479.

### 1.2 Two injection defects shipped in preview.1

- **[Certain] Cookie extension attributes.** `HttpCookieOptions.Extensions` is written verbatim (`HttpCookie.cs:185`). `Extensions.Add("x; Domain=evil.example")` therefore emits a `Domain` attribute that never passed `HttpCookieDomain`'s check, which #1153 added for exactly this untrusted-input case. #1474.
- **[Certain] Server-Sent Events.** `ServerSentEvent.EventType`, `Id` and `Comment` are documented as single-line but are not checked, and `ServerSentEventFormatter.WriteField` writes them verbatim. A line break in one of them injects fields or whole events into the stream. #1475.

Both files are in `v10.0.0-preview.1`. Each defect is reachable only when an application puts untrusted text into those members. V7 covers advisories.

### 1.3 The Antiforgery pattern recurs in three Web packages

- **[Certain] Web.Sessions.** `SessionPipelineExtensions.UseSessions` constructs `_ = new HttpCookie(options.CookieName)` and `_ = new HttpCookieOptions { Path = options.CookiePath }` inside try/catch (lines 84 and 99).
- **[Certain] Web.CookiePolicy.** `CookiePolicyRules.Create` constructs `_ = new HttpCookie(options.ConsentCookieName, options.ConsentCookieValue, ...)` inside try/catch (line 146).
- **[Certain] Web.Authentication.Cookie.** `CookieAuthenticationOptions.CookieName` is a `string` that `CookieAuthenticationHandler.Create` never checks. An invalid name passes composition and fails at sign-in or sign-out (`CookieAuthenticationHandler.cs:263`, `:172`) with a 500.

The existing `HttpCookieName`, `HttpCookiePath` and `HttpCookieValue` cover all three. #1473.

### 1.4 Configured values that fail later, on a request

- **[Certain] Bearer realm.** `JwtBearerHandler.Sanitize` strips only `"`, `\`, CR and LF. NUL and the other controls reach `WWW-Authenticate`, where response encoding throws, so the intended 401 or 403 becomes a failed exchange.
- **[Certain, survey] Field values that reach a response unchecked:**
  - redirect locations: the cookie-authentication `LoginPath`/`AccessDeniedPath` and the Query redirect helpers;
  - static-file content types;
  - output-cache header values from a custom store, which fail when replayed;
  - the Alt-Svc authority: `bad\r\n:443` passes binding, then fails on every response that advertises it.
- **[Certain, survey] Header names that never match.** Antiforgery `HeaderName`, the CORS header lists and output-cache `VaryByHeaders` accept malformed names. Such a name never matches a request, and an empty one throws while a cache key is built.

#1476 adds two core grammar types, `HttpFieldName` and `HttpFieldValue`, and types these surfaces with them. #1477 and #1478 cover the rest of §4.1.

### 1.5 The generator (#1472, in progress)

**Defects being fixed** [Certain]:
- `default` crashes;
- culture-sensitive ordering;
- `IsValid` was an instance method;
- `TryParse` threw `ArgumentException` when `IsValid` rejected a parsed number;
- `Parse` ignored its provider;
- `ISpanParsable` was not declared;
- `ToString()` returned `null` for `default`;
- JSON `null` reached the constructor;
- the build target never regenerated after a generator change;
- the Scheduler kept stale `*.generated.cs` files checked in;
- the item was documented under a name that does not exist (`CohesionCodeGenValueType`).

**Added:**
- a static `IsValid`;
- `TryCreate`, which validates once and never throws;
- an `ObjectComparison` metadata (`Ordinal` | `OrdinalIgnoreCase`; `HttpCookieDomain` uses `OrdinalIgnoreCase` per RFC 6265 §5.1.3);
- `ISpanParsable` and `ISpanFormattable` for numeric, `Guid` and `Ulid` types;
- JSON dictionary keys;
- XML docs on the generated members;
- `COHVT` build errors;
- a build target that always runs but writes a file only when its content changes.

## 2. What the surveys covered

Six read-only Codex surveys (Astra 6, ultra) read `libraries/` and `resources/` at `4e3b9f949`, excluding `resources/Database/`. They reported:
- **178 candidates:** 104 string-backed and 74 numeric;
- **170 rejections**, each with a reason;
- **the generator gaps** in §6.

Each candidate has a kind:
- construct-to-validate (8);
- setter validation (7);
- a grammar checked in several places (46);
- a validated primitive (62);
- an unvalidated option (37);
- a hand-written value object (17);
- other (1).

The orchestrator re-checked the findings in §1 by reading or running the code.

## 3. Recommendation

1. **Value objects are for identifiers and grammar-bearing strings at a public boundary.**
   - **Good fit:** a name, token, path, location, field name or value, or identifier whose rule is reused, or whose bad value would otherwise surface on a request.
   - **Poor fit: numeric ranges.** Thresholds, sizes, counts and timeouts stay primitives with an `ArgumentOutOfRangeException.ThrowIf*` guard at composition. Each generated type adds roughly 40 public members, plus documentation and JSON surface. A range check earns none of that. 50 of the 74 numeric candidates are already validated at composition and are declined; the numeric defects the surveys did find (§4.2, bucket O) are fixed with guards or option snapshots.
2. **Keep structs.** A class kind would turn "no value" into a nullable-reference warning, which closes §1.1's hole at compile time. But it costs an allocation per value, and the cookie types are created per parsed request cookie. The boundary `default` check stays (V5).
3. **Scope the PR to Http/Web.** PR #1094 takes the Http/Web buckets (A–G). The other areas become backlog items in their own PRs, because the cleanup is meant to shrink this PR's review, not grow it (V1).
4. **Make it a rule.** Add the standard to `general-rules.md` and the checklist (V4): "A single-valued input with a validity rule is a `<CohesionValueType>` value object with `IsValid`; never construct an unrelated object, or call a method, only to make it throw."

## 4. Work breakdown

### 4.1 PR #1094 (Http/Web)

| Bucket | Item | What | Candidates |
| --- | --- | --- | --- |
| — | #1472 | Generator correctness and surface (§1.5) | 1 |
| A | #1473 | Sessions, CookiePolicy and cookie-authentication options use the existing cookie types | 8 |
| B | #1474 | `HttpCookieExtension`: refuse attribute injection | 1 |
| C | #1475 | Single-line SSE field type: refuse line breaks | 1 |
| D | #1476 | `HttpFieldName`/`HttpFieldValue` in core Http, on the configured header names and values | 9 |
| E | #1477 | Other Http/Web option values that fail on a request (guards, or existing types) | 15 |
| F | #1478 | Wire-encoding range defects (structured fields, Forwarded, QPACK) | 5 |
| G | #1479 | Hand-written Http value objects made safe at `default` | 5 |

**Order:**
- #1472 lands first, because A–D need its static `IsValid` and `TryCreate`.
- #1479 is independent and runs in parallel.
- D, E and F follow V2 and V3.

### 4.2 Other areas (backlog, separate PRs)

| Bucket | Area | Defects found | Candidates |
| --- | --- | --- | --- |
| H | ApplicationModel | **[Certain, survey]** A probe path such as `Http = "ready"` passes manifest and plan validation, then throws during reconcile. A mount source of `parameter:` followed by spaces passes, then resolves as unresolved input. `ApplicationName.Parse` and the generated conversions bypass `ValidateApplicationName`. A `with`-expression bypasses `ApplicationCredential`'s checks. | 5 |
| I | SecretStore | **[Certain, survey]** `store:key:extra` passes `AddSecret`, then fails at delivery. The certificate leaf names `.`, `..` and backslash pass composition, then fail at execution. SAN names are unchecked until issuance. Reserved paths are refused only at delivery. | 5 |
| J | OpenTelemetry | **[Certain]** A malformed `TraceId`/`SpanId` throws during export, and the batch loop discards the whole batch with it. | 2 |
| K | Amqp | **[Certain, survey]** Unicode `AmqpSymbol` values are encoded as symbols, which AMQP defines as ASCII. A `default` symbol throws when hashed. A max frame size below 512 is accepted. | 2 |
| L | Dns | **[Certain, survey]** `DnsTxtRecord` keeps the caller's list, so a later overlong string truncates its length byte while the full payload is written. `DnsName` accepts `a..`. | 2 |
| M | Content.Yaml | **[Certain, survey]** Programmatic anchors and tags are emitted without checks; a space, newline or `>` changes token boundaries. A negative indent throws at emit. | 3 |
| N | Security.DataProtection | **[Certain, survey]** `new KeyDocument("../escape", ...)` passes construction and is refused only by the repository. | 1 |
| O | Numeric guards and retained options | **[Certain, survey]** Problems the surveys found in several areas: <ul><li>negative QUIC backlog and stream limits are accepted and then fail every incoming connection;</li><li>DNS and gateway options are retained, so later assignments bypass validation;</li><li>NaN passes the cache-compaction and memory-pool ratio guards;</li><li>a negative object-pool retention limit is accepted;</li><li>an OIDC `MaxAge` of `-1` flags a new token;</li><li>`Configuration.Key` hashes `default` and empty differently while comparing them equal.</li></ul> | 15 |
| P | Generator hooks | §6 | — |

## 5. Decisions for the owner

- **V1 — Scope.** PR #1094 takes buckets A–G; H–P become backlog items under their area epics. *Recommended.*
- **V2 — Two core grammar types.** Add `HttpFieldName` (RFC 9110 §5.1 token, `OrdinalIgnoreCase`) and `HttpFieldValue` (§5.5 field content) to core Http.
  - **Why they fit decision 20:** they are base grammar, not feature contracts.
  - **Why `HttpHeaderKey` and `HttpHeaderValue` stay as they are:** validating them would put a second check on the per-request parse path, and `HttpHeaderKey` has to accept pseudo-headers.
  - *Recommended.*
- **V3 — Numbers stay primitives.** Numeric ranges stay primitives with composition guards (§3.1). *Recommended.*
- **V4 — The rule.** Add the value-object standard to `general-rules.md` and `checklist.md`. *Recommended.*
- **V5 — Structs.** Keep struct value objects, plus the boundary `default` check, rather than switching to a class kind. *Recommended.*
- **V6 — Generator hooks (§6).** Build them in the backlog item that first needs them. The first is bucket H, whose credential value needs a redacted `ToString`. None of A–G needs one. *Recommended.*
- **V7 — Advisories.** Create private GHSA drafts for #1474 and #1475 once fixed, to publish with the next preview, as the earlier Stage findings were. *Recommended.*

## 6. Generator gaps the surveys found

Not needed by A–G; needed by bucket H or by migrating hand-written types:
- **A normalization hook.** Store a canonical form after validation: a lower-cased origin, a host without its trailing dot, a trimmed vocabulary value, `HttpPath`'s null-to-root. Reported by four surveys.
- **Member opt-outs.** Suppress or replace the generated `ToString`/`DebuggerDisplay` (credential redaction), equality and hashing (DNS trailing-dot equivalence), or the JSON converter. Customize the constructor's exception type and parameter name, for example `ArgumentOutOfRangeException`.
- **Span validation for wire-facing string types.** Reject a span without allocating, and create the string once. This blocks generating `HttpHeaderKey`, `Configuration.Key` or `FileName` without a per-request cost.

The other gaps the surveys found (domain-aware `TryParse` and JSON dictionary keys) are in #1472.

## 7. Candidate tables

Generated from the survey output: one row per candidate, with the first site and the defect each survey reported. "none found" means the value is already validated at composition. "Proposed type" is the survey's proposal. For buckets E, F and O the recommendation (V3) is a guard, or a type that already exists, rather than a new type.

### #1472 — handled by the generator change (1)

| Concept | Proposed type | Where | Defect today |
| --- | --- | --- | --- |
| Cookie domain grammar and identity | Reuse Assimalign.Cohesion.Http.HttpCookieDomain from libraries/Http/Assimalign.Cohesion.Http.Cookies/src/Value | `libraries/Http/Assimalign.Cohesion.Http.Cookies/src/ValueObjects/HttpCookieDomain.cs:33` | [Certain] The current generated equality is case-sensitive because comparison metadata is absent; differently cased domain strings therefore have different value-object identity despite domain matching ignoring case. |

### A — #1473 Sessions, CookiePolicy and cookie-authentication options (8)

| Concept | Proposed type | Where | Defect today |
| --- | --- | --- | --- |
| Session cookie name | Reuse Assimalign.Cohesion.Http.HttpCookieName from libraries/Http/Assimalign.Cohesion.Http.Cookies/src/ValueOb | `libraries/Http/Assimalign.Cohesion.Http.Sessions/src/HttpSessionOptions.cs:23` | [Certain] Initial composition already catches invalid names, so this is not an existing startup-validation omission. |
| Session cookie path | Reuse Assimalign.Cohesion.Http.HttpCookiePath from libraries/Http/Assimalign.Cohesion.Http.Cookies/src/ValueOb | `libraries/Http/Assimalign.Cohesion.Http.Sessions/src/HttpSessionOptions.cs:37` | [Certain] Composition currently validates this option; no initial deferred failure was found. |
| Cookie value validation during parsing | Reuse Assimalign.Cohesion.Http.HttpCookieValue from libraries/Http/Assimalign.Cohesion.Http.Cookies/src/ValueO | `libraries/Http/Assimalign.Cohesion.Http.Cookies/src/ValueObjects/HttpCookieValue.cs:26` | none found |
| Authentication cookie name | Reuse Assimalign.Cohesion.Http.HttpCookieName | `resources/Web/Assimalign.Cohesion.Web.Authentication.Cookie/src/CookieAuthenticationOptions.cs:28` | [Certain] Malformed names pass registration and fail in IssueCookie at line 263 or SignOutAsync at line 172. |
| Session cookie name | Reuse Assimalign.Cohesion.Http.HttpCookieName | `resources/Web/Assimalign.Cohesion.Web.Sessions/src/Extensions/SessionPipelineExtensions.cs:84` | [Certain] Invalid initial names already fail during UseSessions. |
| Session cookie path | Reuse Assimalign.Cohesion.Http.HttpCookiePath | `resources/Web/Assimalign.Cohesion.Web.Sessions/src/Extensions/SessionPipelineExtensions.cs:99` | [Certain] The existing composition probe already rejects malformed initial paths before session establishment. |
| Consent cookie name | Reuse Assimalign.Cohesion.Http.HttpCookieName | `resources/Web/Assimalign.Cohesion.Web.CookiePolicy/src/CookiePolicyOptions.cs:139` | [Certain] Initial malformed names already fail at UseCookiePolicy registration through CookiePolicyRules.Create. |
| Consent cookie value | Reuse Assimalign.Cohesion.Http.HttpCookieValue | `resources/Web/Assimalign.Cohesion.Web.CookiePolicy/src/CookiePolicyOptions.cs:146` | [Certain] Existing registration rejects malformed and empty values before requests. |

### B — #1474 Cookie extension attributes (1)

| Concept | Proposed type | Where | Defect today |
| --- | --- | --- | --- |
| Opaque Set-Cookie extension attribute | New Assimalign.Cohesion.Http.HttpCookieExtension | `libraries/Http/Assimalign.Cohesion.Http.Cookies/src/HttpCookieOptions.cs:137` | [Certain] A caller-supplied semicolon becomes another serialized cookie attribute. |

### C — #1475 Server-Sent Events single-line fields (1)

| Concept | Proposed type | Where | Defect today |
| --- | --- | --- | --- |
| Single-line Server-Sent Events metadata | New Assimalign.Cohesion.Http.ServerSentEventFieldLine | `libraries/Http/Assimalign.Cohesion.Http.ServerSentEvents/src/ServerSentEvent.cs:46` | [Certain] Embedded CR/LF in these properties creates extra event-stream fields or event boundaries during formatting/writing. |

### D — #1476 HTTP field names and values on configuration surfaces (9)

| Concept | Proposed type | Where | Defect today |
| --- | --- | --- | --- |
| Antiforgery request-token header name | Reuse Assimalign.Cohesion.Http.HttpHeaderKey after the core-header-key grammar enhancement | `libraries/Http/Assimalign.Cohesion.Http.Antiforgery/src/HttpAntiforgeryOptions.cs:55` | [Certain] Invalid nonempty names survive construction and Web composition, then cannot match a valid wire header. |
| One HTTP field-line value | New Assimalign.Cohesion.Http.HttpFieldValue Keep HttpHeaderValue as a collection | `libraries/Http/Assimalign.Cohesion.Http/src/HttpFieldNormalization.cs:103` | [Certain] Current constructors accept invalid outgoing field text, which fails during response encoding. |
| Alt-Svc host component | New Assimalign.Cohesion.Http.HttpAltServiceHost Do not reuse permissive HttpHost unchanged | `libraries/Http/Assimalign.Cohesion.Http/src/ValueObjects/HttpAltService.cs:76` | [Certain] CR/LF in a directly supplied host survives Format and subsequently fails outgoing field validation. |
| Configured Alt-Svc authority | New Assimalign.Cohesion.Http.HttpAltServiceAuthority sharing host validation with core-alt-host | `libraries/Http/Assimalign.Cohesion.Http.Connections/src/HttpAltServiceAdvertisementOptions.cs:72` | [Certain] 'bad\r\n:443' passes assignment, listener construction and binding, then fails field encoding on an HTTP/1 or HTTP/2 response that injects the advertisement. |
| Bearer authentication realm | New Assimalign.Cohesion.Web.Authentication.Bearer.JwtBearerRealm | `resources/Web/Assimalign.Cohesion.Web.Authentication.Bearer/src/JwtBearerOptions.cs:107` | [Certain] NUL and other non-HTAB controls survive Sanitize and reach WWW-Authenticate; HttpResponseFieldRules.EnsureValidValue throws during response encoding. |
| HTTP redirect location | New Assimalign.Cohesion.Http.HttpRedirectLocation | `resources/Web/Assimalign.Cohesion.Web.Authentication.Cookie/src/CookieAuthenticationOptions.cs:66` | [Certain] Cookie challenge/forbid copy malformed configured paths into Location at handler lines 188 and 204; Query helpers do likewise at lines 46 and 63. |
| Ordinary HTTP field name | New Assimalign.Cohesion.Http.HttpFieldName with a checked-in conversion to existing HttpHeaderKey. Current Htt | `resources/Web/Assimalign.Cohesion.Web.Cors/src/CorsPolicyBuilder.cs:160` | [Certain] A null/empty item inserted directly into VaryByHeaders fails while building a request cache key; malformed non-token names can silently never match valid requests. |
| Cached HTTP field-line value | New Assimalign.Cohesion.Http.HttpFieldLineValue | `resources/Web/Assimalign.Cohesion.Web.Caching/src/OutputCacheHeader.cs:25` | [Certain] A custom cache store can return a field value containing CR/LF; replay reaches transport validation and throws on a cache hit. |
| Concrete response Content-Type | New Assimalign.Cohesion.Http.HttpContentType retain HttpMediaType as the parsed model | `resources/Web/Assimalign.Cohesion.Web.StaticFiles/src/StaticFilesOptions.cs:53` | [Certain] CR/LF in a configured fallback or mapping passes composition and reaches Content-Type, where transport encoding throws. |

### E — #1477 Other Http/Web option values that fail on a request (15)

| Concept | Proposed type | Where | Defect today |
| --- | --- | --- | --- |
| HTTP parameter/directive name | New Assimalign.Cohesion.Http.HttpFieldParameterName | `libraries/Http/Assimalign.Cohesion.Http/src/ValueObjects/HttpMediaTypeParameter.cs:21` | [Certain] Public constructors accept names rejected by their parsers. |
| Charset, encoding or language preference token | New Assimalign.Cohesion.Http.HttpPreferenceToken | `libraries/Http/Assimalign.Cohesion.Http/src/ValueObjects/HttpQualityValue.cs:20` | [Certain] Public construction accepts spellings the parser rejects. |
| Extended CONNECT protocol name | New Assimalign.Cohesion.Http.HttpProtocolName | `libraries/Http/Assimalign.Cohesion.Http/src/HttpExchangeInterceptorRequestContext.cs:80` | [Certain] Values containing internal spaces or '/' pass current general field-value and nonempty checks and reach dispatch. |
| Content-type mapping extension | New Assimalign.Cohesion.Http.HttpFileExtension | `libraries/Http/Assimalign.Cohesion.Http/src/HttpContentTypes.cs:125` | [Certain] CreateMap stores 'a/b' as '.a/b', but lookup rejects that extension, producing an ineffective mapping rather than an exception. |
| WebSocket subprotocol token | New Assimalign.Cohesion.Http.HttpWebSocketSubProtocol | `libraries/Http/Assimalign.Cohesion.Http.WebSockets/src/HttpWebSocketAcceptOptions.cs:34` | [Certain] A malformed configured selection survives option assignment and policy copying, then cannot match the validated offers and throws during AcceptAsync. |
| Default static document filename | New Assimalign.Cohesion.Web.StaticFiles.StaticDocumentName | `resources/Web/Assimalign.Cohesion.Web.StaticFiles/src/StaticFilesOptions.cs:28` | [Certain] A name containing an illegal path character can pass composition and throw from FileSystemPath.Parse during default-document resolution. |
| Static-file lookup path | New Assimalign.Cohesion.Web.StaticFiles.StaticFileRelativePath | `resources/Web/Assimalign.Cohesion.Web.StaticFiles/src/Extensions/WebApplicationStaticFilesExtensions.cs:55` | [Certain] A fallback such as bad?.html passes the unsafe-segment composition check but fails parsing on a request and becomes 404. |
| Maximum decompressed request-body bytes | New Assimalign.Cohesion.Web.Compression.DecompressedBodyLimit | `resources/Web/Assimalign.Cohesion.Web.Compression/src/RequestDecompressionOptions.cs:24` | [Certain] A negative configured limit survives composition and causes the first nonempty decoded read to throw; middleware catches the limit exception and returns 413 when possible. |
| Problem Details HTTP status | Reuse Assimalign.Cohesion.Http.HttpStatusCode | `resources/Web/Assimalign.Cohesion.Web.ProblemDetails/src/ProblemDetails.cs:48` | [Certain] An out-of-range Status is serialized before WriteProblemDetailsAsync converts it to HttpStatusCode and throws. |
| Configured CORS HTTP method | Reuse Assimalign.Cohesion.Http.HttpMethod | `resources/Web/Assimalign.Cohesion.Web.Cors/src/CorsPolicyBuilder.cs:117` | [Certain] Configuration currently accepts tokens longer than 32 characters, but preflight rejects them, so those configured entries cannot authorize requests. |
| Output-cache byte budget | New Assimalign.Cohesion.Web.Caching.OutputCacheByteLimit | `resources/Web/Assimalign.Cohesion.Web.Caching/src/OutputCacheOptions.cs:47` | [Certain] A nonpositive policy override bypasses the option/store checks and abandons body capture on the first write instead of rejecting configuration. |
| Compressible media-type pattern | New Assimalign.Cohesion.Web.Compression.CompressionMediaTypePattern | `resources/Web/Assimalign.Cohesion.Web.Compression/src/ResponseCompressionOptions.cs:87` | [Certain] A malformed entry such as 'application json' survives composition and silently fails to match real media types. |
| Response compression size threshold | New Assimalign.Cohesion.Web.Compression.CompressionSizeThreshold | `resources/Web/Assimalign.Cohesion.Web.Compression/src/ResponseCompressionOptions.cs:78` | [Certain] A negative value causes every nonempty eligible body to cross the threshold. |
| Route literal text | New Assimalign.Cohesion.Web.Routing.RouteLiteralContent | `resources/Web/Assimalign.Cohesion.Web.Routing/src/Patterns/RoutePatternFactory.cs:225` | [Certain] Direct construction of the public literal segment bypasses the factory's '?' restriction. |
| Problem Details type and instance URI references | New Assimalign.Cohesion.Web.ProblemUriReference | `resources/Web/Assimalign.Cohesion.Web.ProblemDetails/src/ProblemDetails.cs:36` | [Certain] Invalid URI-reference text is emitted as valid JSON but violates the documented payload contract. |

### F — #1478 Wire-encoding range defects (5)

| Concept | Proposed type | Where | Defect today |
| --- | --- | --- | --- |
| Structured-field integer and date-second range | New Assimalign.Cohesion.Http.StructuredFieldInteger reuse for the identical date-second bound | `libraries/Http/Assimalign.Cohesion.Http/src/ValueObjects/StructuredFieldBareItem.cs:61` | [Certain] An out-of-range HttpWantDigestPreference is accepted, then throws when HttpWantDigestField serializes it through FromInteger. |
| Serializable structured-field decimal | New Assimalign.Cohesion.Http.StructuredFieldDecimal | `libraries/Http/Assimalign.Cohesion.Http/src/ValueObjects/StructuredFieldBareItem.cs:76` | [Certain] 999999999999.9999 passes the factory's current truncated-magnitude check but rounds out of range during serialization. |
| Structured-field display-string Unicode content | New Assimalign.Cohesion.Http.StructuredFieldDisplayString | `libraries/Http/Assimalign.Cohesion.Http/src/ValueObjects/StructuredFieldBareItem.cs:169` | [Certain] An unpaired surrogate is accepted by the factory and fails later during WriteDisplayString's strict encoding. |
| Numeric Forwarded node port | New Assimalign.Cohesion.Http.HttpForwardedPort | `libraries/Http/Assimalign.Cohesion.Http/src/ValueObjects/HttpForwardedNode.cs:113` | [Certain] FromIPAddress accepts 100000, but the serialized numeric port is rejected by the same type's parser. |
| QPACK capacity and blocked-stream settings | New Assimalign.Cohesion.Http.Connections.Http3SettingValue | `libraries/Http/Assimalign.Cohesion.Http.Connections/src/Options/Http3QPackOptions.cs:39` | [Certain] 2^62 is accepted but encodes as zero after the encoding-prefix bits replace its high bits; decoder state retains the original value. |

### G — #1479 Hand-written Http value objects at default (5)

| Concept | Proposed type | Where | Defect today |
| --- | --- | --- | --- |
| HTTP header-field key | Generate scaffolding for existing Assimalign.Cohesion.Http.HttpHeaderKey | `libraries/Http/Assimalign.Cohesion.Http/src/ValueObjects/HttpHeaderKey.cs:16` | [Certain] A nonempty malformed name can enter the current wrapper and is refused later when response fields are encoded. |
| HTTP method | Generate scaffolding for existing Assimalign.Cohesion.Http.HttpMethod | `libraries/Http/Assimalign.Cohesion.Http/src/ValueObjects/HttpMethod.cs:43` | none found |
| HTTP path carrier | Generate scaffolding for existing Assimalign.Cohesion.Http.HttpPath | `libraries/Http/Assimalign.Cohesion.Http/src/ValueObjects/HttpPath.cs:27` | none found |
| Query/form key identity | Generate scaffolding for existing Assimalign.Cohesion.Http.HttpQueryKey | `libraries/Http/Assimalign.Cohesion.Http/src/ValueObjects/HttpQueryKey.cs:16` | none found |
| Raw request authority carrier | Generate scaffolding for existing Assimalign.Cohesion.Http.HttpHost | `libraries/Http/Assimalign.Cohesion.Http/src/ValueObjects/HttpHost.cs:40` | [Certain] Permissive construction is documented behavior, not an established validation defect. |

### H — ApplicationModel (backlog) (5)

| Concept | Proposed type | Where | Defect today |
| --- | --- | --- | --- |
| Mount-source expression | New Assimalign.Cohesion.ApplicationModel.ResourceMountSource | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/src/Manifests/ResourceManifest.cs:395` | [Certain] parameter: followed only by spaces passes manifest validation because it checks suffix length, and provider validation skips parameter sources. |
| Absolute resource HTTP probe and control-plane path | New Assimalign.Cohesion.ApplicationModel.ResourceHttpPath existing HttpPath has different acceptance and defau | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/src/Manifests/ResourceManifestProbe.cs:22` | [Certain] Http='ready' passes manifest and plan validation, then throws in ProbeSpec.Http during LocalPlanController.ReconcileAsync -> LocalResourcePreparer.PrepareAsync. |
| Application identity | Reuse existing Assimalign.Cohesion.ApplicationModel.ApplicationName; add IsValid | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/src/Assimalign.Cohesion.ApplicationModel.csproj:21` | [Certain] Build already rejects invalid application names. |
| Application credential authentication scheme | New Assimalign.Cohesion.ApplicationModel.ApplicationCredentialScheme | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/src/Providers/ApplicationCredential.cs:23` | [Certain] 'Bad Scheme' can be constructed and is rejected only during telemetry resolution. |
| Single-line application credential value | New Assimalign.Cohesion.ApplicationModel.ApplicationCredentialValue conditional on secret-safe generated prese | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/src/Providers/ApplicationCredential.cs:28` | [Certain] A nonblank credential with line breaks can be constructed and cached, then fail in RequireBearer or telemetry resolution. |

### I — SecretStore (backlog) (5)

| Concept | Proposed type | Where | Defect today |
| --- | --- | --- | --- |
| Secret-only command source expression | New SecretStoreCommandSource namespace Assimalign.Cohesion.ApplicationModel | `resources/SecretStore/Assimalign.Cohesion.SecretStore.ApplicationModel/src/Extensions/SecretStoreResourceCommandExtensions.cs:40` | [Certain] 'store:key:extra' passes AddSecret's first-colon check and opaque command-payload composition, then fails SecretStoreAddSecretInputResolver.ResolveAsync at line 114 because IsResourceSource forbids another colon. |
| Unprefixed certificate leaf name | New SecretStoreCertificateLeafName namespace Assimalign.Cohesion.ApplicationModel | `resources/SecretStore/Assimalign.Cohesion.SecretStore.ApplicationModel/src/Extensions/SecretStoreResourceCommandExtensions.cs:65` | [Certain] IssueCertificate accepts '.', '..' and backslash-containing names during composition. |
| Logical path permitted for an add-secret command | New SecretStoreCommandPath namespace Assimalign.Cohesion.ApplicationModel | `resources/SecretStore/Assimalign.Cohesion.SecretStore.ApplicationModel/src/Extensions/SecretStoreResourceCommandExtensions.cs:35` | [Certain] Reserved paths compose successfully and are rejected only by StoreCommandSecretAsync during delivery. |
| Full certificate retrieval reference | New SecretStoreCertificateName namespace Assimalign.Cohesion.SecretStore.Client. Keep Root='ca/root' and leaf- | `resources/SecretStore/Assimalign.Cohesion.SecretStore.Client/src/Abstractions/ISecretStoreClient.cs:58` | [Certain] Invalid nonblank names are sent over HTTP before rejection. |
| Each DNS-or-IP certificate subject alternative name | New SecretStoreCertificateAlternativeName namespace Assimalign.Cohesion.ApplicationModel. Existing DnsName doe | `resources/SecretStore/Assimalign.Cohesion.SecretStore.ApplicationModel/src/Extensions/SecretStoreResourceCommandExtensions.cs:59` | [Certain] Non-IP strings reach AddDnsName at authority line 757 only during issuance. |

### J — OpenTelemetry (backlog) (2)

| Concept | Proposed type | Where | Defect today |
| --- | --- | --- | --- |
| OTLP trace identifier | New Assimalign.Cohesion.OpenTelemetry.OtlpTraceId use OtlpTraceId? for optional values | `libraries/OpenTelemetry/Assimalign.Cohesion.OpenTelemetry/src/OtlpLogRecord.cs:13` | [Certain] Malformed TraceId passes construction and enqueue, then throws from OtlpJsonEncoder.Hex during ExportAsync. |
| OTLP span identifier | New Assimalign.Cohesion.OpenTelemetry.OtlpSpanId use OtlpSpanId? for optional values | `libraries/OpenTelemetry/Assimalign.Cohesion.OpenTelemetry/src/OtlpLogRecord.cs:14` | [Certain] A malformed SpanId survives construction and enqueue, fails in Hex during serialization, and causes the background exporter to discard the whole drained batch. |

### K — Amqp (backlog) (2)

| Concept | Proposed type | Where | Defect today |
| --- | --- | --- | --- |
| AMQP symbol | Reuse existing Assimalign.Cohesion.Amqp.Connections.AmqpSymbol at libraries/Amqp/Assimalign.Cohesion.Amqp.Conn | `libraries/Amqp/Assimalign.Cohesion.Amqp.Connections/src/ValueObjects/AmqpSymbol.cs:8` | [Certain] Unicode symbols are accepted and UTF-8 encoded under AMQP symbol format codes, including symbol arrays, producing nonconforming wire data. |
| AMQP maximum frame size | New Assimalign.Cohesion.Amqp.Connections.AmqpMaximumFrameSize | `libraries/Amqp/Assimalign.Cohesion.Amqp.Connections/src/AmqpTransportOptions.cs:26` | [Certain] The standalone codec accepts sub-512 limits when the encoded frame fits; zero fails only after body encoding at AmqpEncoding.Protocol.cs:82. |

### L — Dns (backlog) (2)

| Concept | Proposed type | Where | Defect today |
| --- | --- | --- | --- |
| One DNS TXT character-string | New Assimalign.Cohesion.Dns.DnsTxtString | `libraries/Dns/Assimalign.Cohesion.Dns/src/Records/DnsTxtRecord.cs:22` | [Certain] Constructor validation is immediate, but Strings retains the caller's list. |
| DNS presentation-form name | Reuse Assimalign.Cohesion.Dns.DnsName | `libraries/Dns/Assimalign.Cohesion.Dns/src/ValueObjects/DnsName.cs:34` | [Certain] The current validator accepts 'a..': removing one trailing dot leaves a final empty label exempted by line 141. |

### M — Content.Yaml (backlog) (3)

| Concept | Proposed type | Where | Defect today |
| --- | --- | --- | --- |
| YAML block indentation increment | New Assimalign.Cohesion.Content.Yaml.YamlIndentSize | `libraries/Content/Assimalign.Cohesion.Content.Yaml/src/YamlWriterOptions.cs:11` | [Certain] Negative indentation is accepted at writer creation and fails when nested block content is emitted. |
| YAML anchor name | New Assimalign.Cohesion.Content.Yaml.YamlAnchorName | `libraries/Content/Assimalign.Cohesion.Content.Yaml/src/Nodes/YamlNode.cs:19` | [Certain] Programmatic names containing spaces/newlines bypass parsing and are inserted directly into anchor and alias syntax for shared nodes. |
| Resolved YAML tag | New Assimalign.Cohesion.Content.Yaml.YamlTag | `libraries/Content/Assimalign.Cohesion.Content.Yaml/src/Nodes/YamlNode.cs:26` | [Certain] Programmatic tags bypass ReadTag. |

### N — Security.DataProtection (backlog) (1)

| Concept | Proposed type | Where | Defect today |
| --- | --- | --- | --- |
| Storage-safe data-protection key document name | New Assimalign.Cohesion.Security.DataProtection.KeyDocumentName | `libraries/Security/Assimalign.Cohesion.Security.DataProtection/src/KeyDocument.cs:17` | [Certain] new KeyDocument("../escape", content) passes its constructor and fails only at FileSystemKeyRepository.StoreKey -> ValidateName, before writing. |

### O — Numeric guards and retained options (backlog) (15)

| Concept | Proposed type | Where | Defect today |
| --- | --- | --- | --- |
| QUIC inbound stream allowance | New Assimalign.Cohesion.Connections.Quic.QuicInboundStreamLimit | `libraries/Connections/Assimalign.Cohesion.Connections.Quic/src/QuicConnectionListenerOptions.cs:98` | [Certain] Cohesion performs no composition range check. |
| QUIC listener backlog | New Assimalign.Cohesion.Connections.Quic.QuicListenBacklog | `libraries/Connections/Assimalign.Cohesion.Connections.Quic/src/QuicConnectionListenerOptions.cs:62` | [Certain] The inspected BCL validator substitutes its default for zero but does not reject negatives. |
| UDP DNS response buffer limit | New Assimalign.Cohesion.Dns.DnsUdpResponseSize | `libraries/Dns/Assimalign.Cohesion.Dns.Client/src/UdpDnsTransportOptions.cs:27` | [Certain] Initial options are checked, but the transport retains the mutable options object at line 69. |
| Maximum iterative DNS referral depth | New Assimalign.Cohesion.Dns.DnsReferralDepthLimit | `libraries/Dns/Assimalign.Cohesion.Dns.Client/src/IterativeDnsResolverOptions.cs:54` | [Certain] The resolver retains its options at line 102. |
| Maximum upstream exchanges per DNS resolution | New Assimalign.Cohesion.Dns.DnsQueryBudget | `libraries/Dns/Assimalign.Cohesion.Dns.Client/src/IterativeDnsResolverOptions.cs:60` | [Certain] Initial invalid values are rejected, but retained mutable options permit a later zero assignment. |
| Named-pipe server instance limit | New Assimalign.Cohesion.Connections.NamedPipes.NamedPipeServerInstanceLimit | `libraries/Connections/Assimalign.Cohesion.Connections.NamedPipes/src/NamedPipeConnectionListenerOptions.cs:30` | [Certain] Invalid values survive listener creation and reach BCL validation only in CreateServerStream, called by BindAsync and later accepts. |
| Socket or named-pipe buffer size | New Assimalign.Cohesion.Connections.ConnectionBufferSize | `libraries/Connections/Assimalign.Cohesion.Connections.NamedPipes/src/NamedPipeConnectionListenerOptions.cs:69` | [Certain] Named-pipe values reach validation during CreateServerStream, at constructor arguments on lines 270–271 or 290–291. |
| Memory-pool peak retention fraction | New Assimalign.Cohesion.MemoryPoolRetentionRatio | `libraries/Core/Assimalign.Cohesion.Core/src/System/Buffers/AdaptiveMemoryPoolPressurePolicy.cs:55` | [Certain] NaN passes the current value < 0 // value > 1 check and reaches multiplication, Math.Ceiling and integer conversion during retention calculation. |
| Maximum recursive name-server sub-resolution depth | New Assimalign.Cohesion.Dns.DnsNameServerResolutionDepthLimit | `libraries/Dns/Assimalign.Cohesion.Dns.Client/src/IterativeDnsResolverOptions.cs:121` | [Certain] Initial negative values fail construction, but later negative assignment to retained options suppresses the sub-resolution branch. |
| Maximum permitted authentication age | New Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect.OpenIdConnectMaximumAuthenticationAge | `libraries/IdentityModel/Assimalign.Cohesion.IdentityModel.Protocols.OpenIdConnect/src/OpenIdConnectIdTokenValidationOptions.cs:67` | [Certain] With MaxAge=-1, zero ClockSkew and AuthTime equal to ValidateAt, the comparison reports MaxAgeExceeded for a newly authenticated token instead of refusing the policy value. |
| Configuration key segment | Reuse Assimalign.Cohesion.Configuration.Key | `libraries/Configuration/Assimalign.Cohesion.Configuration/src/ValueObjects/Key.cs:11` | [Certain] Validation already occurs at construction. |
| Consecutive liveness-failure threshold | New Assimalign.Cohesion.ApplicationModel.Gateway.LivenessFailureThreshold | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Gateway/src/LocalGatewayOptions.cs:77` | [Certain] Initial options are validated by both gateway constructors. |
| Maximum restart attempts | New Assimalign.Cohesion.ApplicationModel.Gateway.RestartAttemptLimit | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Gateway/src/LocalGatewayOptions.cs:98` | [Certain] Constructors validate initial options, but retained mutable options can subsequently receive a negative limit without revalidation. |
| Cache compaction fraction | New Assimalign.Cohesion.Caching.InMemory.CacheCompactionPercentage | `libraries/Cache/Assimalign.Cohesion.Caching.InMemory/src/MemoryCacheOptions.cs:73` | [Certain] The current <=0 or >1 guard accepts NaN. |
| Object-pool retention limit | New Assimalign.Cohesion.ObjectPool.ObjectPoolRetentionLimit | `libraries/ObjectPool/Assimalign.Cohesion.ObjectPool/src/Extensions/ObjectPoolExtensions.cs:62` | [Certain] Negative limits reach pool operations without rejection, and Return still retains its first object. |

<details>
<summary>Declined: 98 candidates (already validated, a guard suffices, or the semantics go beyond the generator)</summary>

| Concept | Proposed type | Where | Defect today |
| --- | --- | --- | --- |
| HTTP status code | Generate scaffolding for existing Assimalign.Cohesion.Http.HttpStatusCode | `libraries/Http/Assimalign.Cohesion.Http/src/ValueObjects/HttpStatusCode.cs:118` | [Certain] Existing construction validates immediately; no deferred failure found. |
| Host allowlist pattern | New Assimalign.Cohesion.Http.HttpHostPattern | `libraries/Http/Assimalign.Cohesion.Http/src/HttpHostMatcher.cs:108` | [Certain] Create already validates eagerly, including entries after '*'; no deferred failure found. |
| Maximum request-body byte count | New Assimalign.Cohesion.Http.HttpBodySize | `libraries/Http/Assimalign.Cohesion.Http/src/HttpExchangeInterceptorRequestContext.cs:155` | [Certain] Numeric validation already occurs on assignment. |
| Structured-field dictionary/parameter key | New Assimalign.Cohesion.Http.StructuredFieldKey | `libraries/Http/Assimalign.Cohesion.Http/src/Internal/StructuredFieldGrammar.cs:73` | [Certain] Public constructors already validate; no delayed failure found. |
| Structured-field token | New Assimalign.Cohesion.Http.StructuredFieldToken | `libraries/Http/Assimalign.Cohesion.Http/src/Internal/StructuredFieldGrammar.cs:51` | [Certain] Factory validation already prevents malformed public token construction. |
| Structured-field ASCII string content | New Assimalign.Cohesion.Http.StructuredFieldString | `libraries/Http/Assimalign.Cohesion.Http/src/ValueObjects/StructuredFieldBareItem.cs:93` | [Certain] The factory already rejects invalid content; the AcceptQuery catch is error translation, not discarded construction. |
| Alt-Svc protocol identifier spelling | New Assimalign.Cohesion.Http.HttpAltServiceProtocolId | `libraries/Http/Assimalign.Cohesion.Http/src/ValueObjects/HttpAltService.cs:78` | [Certain] Existing token validation is immediate. |
| Alt-Svc port | New Assimalign.Cohesion.Http.HttpAltServicePort | `libraries/Http/Assimalign.Cohesion.Http/src/ValueObjects/HttpAltService.cs:83` | [Certain] Existing paths validate this bound; no missing port check found. |
| Alt-Svc maximum-age seconds | New Assimalign.Cohesion.Http.HttpAltServiceMaxAge | `libraries/Http/Assimalign.Cohesion.Http/src/ValueObjects/HttpAltService.cs:88` | [Certain] Existing constructor rejects negatives immediately. |
| HTTP priority urgency | New Assimalign.Cohesion.Http.HttpUrgency | `libraries/Http/Assimalign.Cohesion.Http/src/ValueObjects/HttpPriority.cs:56` | [Certain] Existing public constructor validates; parser intentionally ignores invalid values. |
| Minimum HTTP data rate | New Assimalign.Cohesion.Http.HttpBytesPerSecond | `libraries/Http/Assimalign.Cohesion.Http/src/ValueObjects/HttpMinDataRate.cs:44` | [Certain] Current constructor validates immediately; no deferred failure established. |
| Opaque entity-tag content | New Assimalign.Cohesion.Http.HttpEntityTagContent | `libraries/Http/Assimalign.Cohesion.Http/src/ValueObjects/HttpEntityTag.cs:66` | [Certain] Existing factories validate; no missing check found. |
| Byte coordinates and representation lengths | New Assimalign.Cohesion.Http.HttpByteCount positive slice length remains an additional aggregate constraint | `libraries/Http/Assimalign.Cohesion.Http/src/ValueObjects/HttpRange.cs:67` | [Certain] Existing public boundaries already validate numeric inputs; no deferred failure established. |
| Maximum accepted range count | New Assimalign.Cohesion.Http.HttpMaximumRangeCount | `libraries/Http/Assimalign.Cohesion.Http/src/HttpRangeSelector.cs:42` | [Certain] Already rejects invalid input immediately. |
| HTTP/3 decoded field-section size limit | New Assimalign.Cohesion.Http.Connections.Http3FieldSectionSizeLimit | `libraries/Http/Assimalign.Cohesion.Http.Connections/src/Options/Http3QPackOptions.cs:92` | [Certain] Both bounds already fail on assignment. |
| Positive request framing and field-section byte limits | New Assimalign.Cohesion.Http.Connections.HttpByteSizeLimit | `libraries/Http/Assimalign.Cohesion.Http.Connections/src/Options/Http1ConnectionListenerOptions.cs:49` | [Certain] All five setters already reject nonpositive values during configuration. |
| Positive header, stream and flood-event count limits | New Assimalign.Cohesion.Http.Connections.HttpCountLimit | `libraries/Http/Assimalign.Cohesion.Http.Connections/src/Options/Http1ConnectionListenerOptions.cs:66` | [Certain] Existing setters already validate at composition. |
| Accepted connection queue capacity | Reuse proposed Assimalign.Cohesion.Http.Connections.HttpCountLimit, or use a semantic HttpConnectionBacklogCap | `libraries/Http/Assimalign.Cohesion.Http.Connections/src/HttpConnectionListenerOptions.cs:137` | [Certain] Invalid values already fail in the setter before listener construction. |
| Initial exchange feature capacity | New Assimalign.Cohesion.Http.Connections.HttpExchangeFeatureCapacity | `libraries/Http/Assimalign.Cohesion.Http.Connections/src/HttpConnectionListenerOptions.cs:178` | [Certain] The nonnegative constraint already fails on assignment. |
| Named HTTP client's redirect limit | New Assimalign.Cohesion.Http.HttpRedirectLimit | `libraries/Http/Assimalign.Cohesion.Http.ClientFactory/src/NamedHttpClientOptions.cs:67` | [Certain] Setter validation already occurs before handler construction. |
| Server WebSocket compression window bits | New Assimalign.Cohesion.Http.HttpWebSocketServerWindowBits | `libraries/Http/Assimalign.Cohesion.Http.WebSockets/src/HttpWebSocketAcceptOptions.cs:96` | [Certain] Existing setter already rejects invalid configuration immediately. |
| Configured serialized origin | New Assimalign.Cohesion.Http.HttpOrigin | `resources/Web/Assimalign.Cohesion.Web.Cors/src/CorsPolicyBuilder.cs:55` | [Certain] Both packages already reject malformed configured origins during composition; no initial-configuration late failure was found. |
| Host filtering and HSTS host pattern | New Assimalign.Cohesion.Http.HttpHostPattern | `resources/Web/Assimalign.Cohesion.Web.HostFiltering/src/HostFilteringOptions.cs:36` | [Certain] Both paths already validate patterns while composing their matcher. |
| HTTPS redirect destination port | New Assimalign.Cohesion.Http.HttpDestinationPort | `resources/Web/Assimalign.Cohesion.Web.HttpsPolicy/src/HttpsRedirectionOptions.cs:47` | [Certain] Both entry points already reject invalid ports during composition. |
| Concurrent Web server connection cap | New Assimalign.Cohesion.Web.WebConnectionLimit | `resources/Web/Assimalign.Cohesion.Web.Server/src/WebApplicationServerOptions.cs:34` | [Certain] Builder, binder and server already reject nonpositive caps before request handling. |
| Serialized static-file Cache-Control value | New Assimalign.Cohesion.Http.HttpCacheControlValue | `resources/Web/Assimalign.Cohesion.Web.StaticFiles/src/StaticFilesOptions.cs:37` | [Certain] Existing validation rejects malformed initial configuration; no late grammar failure was established. |
| Access-log filename prefix | New Assimalign.Cohesion.Web.Diagnostics.AccessLogFilePrefix | `resources/Web/Assimalign.Cohesion.Web.Diagnostics/src/W3CAccessLogOptions.cs:25` | [Certain] The provider already rejects invalid prefixes before constructing its writer. |
| HTTP logging body-capture byte limit | New Assimalign.Cohesion.Web.Diagnostics.HttpLogCaptureLimit | `resources/Web/Assimalign.Cohesion.Web.Diagnostics/src/HttpLoggingOptions.cs:140` | [Certain] Both UseHttpLogging overloads validate through HttpLoggingSnapshot.Create before middleware runs. |
| Access-log file-size limit | New Assimalign.Cohesion.Web.Diagnostics.AccessLogFileSizeLimit | `resources/Web/Assimalign.Cohesion.Web.Diagnostics/src/W3CAccessLogOptions.cs:37` | [Certain] Invalid limits already fail in the provider constructor. |
| Retained access-log file count | New Assimalign.Cohesion.Web.Diagnostics.AccessLogRetentionCount | `resources/Web/Assimalign.Cohesion.Web.Diagnostics/src/W3CAccessLogOptions.cs:44` | [Certain] Invalid counts already fail during provider construction. |
| Forwarded-header hop limit | New Assimalign.Cohesion.Web.ForwardedHeaders.ForwardedHopLimit | `resources/Web/Assimalign.Cohesion.Web.ForwardedHeaders/src/ForwardedHeadersOptions.cs:42` | [Certain] The resolver already rejects invalid limits during middleware registration. |
| Permits acquired per rate-limited request | New Assimalign.Cohesion.Web.RateLimiting.RateLimitPermitCount | `resources/Web/Assimalign.Cohesion.Web.RateLimiting/src/RateLimitingPolicy.cs:45` | [Certain] The existing constructor rejects nonpositive counts at composition. |
| Maximum rewrite passes | New Assimalign.Cohesion.Web.Rewrite.RewritePassLimit | `resources/Web/Assimalign.Cohesion.Web.Rewrite/src/RewriteOptions.cs:62` | [Certain] Invalid assignments already fail immediately. |
| CSP directive name | New Assimalign.Cohesion.Web.SecurityHeaders.CspDirectiveName | `resources/Web/Assimalign.Cohesion.Web.SecurityHeaders/src/ContentSecurityPolicyBuilder.cs:262` | [Certain] The builder already rejects invalid names immediately. |
| Generic CSP directive value | New Assimalign.Cohesion.Web.SecurityHeaders.CspDirectiveValue | `resources/Web/Assimalign.Cohesion.Web.SecurityHeaders/src/ContentSecurityPolicyBuilder.cs:279` | [Certain] Existing builder validation prevents delimiter/control input from reaching a composed policy. |
| CSP scheme source | New Assimalign.Cohesion.Web.SecurityHeaders.CspSchemeSource | `resources/Web/Assimalign.Cohesion.Web.SecurityHeaders/src/ContentSecurityPolicySourceListBuilder.cs:119` | [Certain] Current builder validation rejects malformed schemes at composition. |
| CSP host source | New Assimalign.Cohesion.Web.SecurityHeaders.CspHostSource | `resources/Web/Assimalign.Cohesion.Web.SecurityHeaders/src/ContentSecurityPolicySourceListBuilder.cs:146` | [Certain] Invalid host sources already fail during builder calls. |
| CSP framing ancestor source | New Assimalign.Cohesion.Web.SecurityHeaders.CspAncestorSource | `resources/Web/Assimalign.Cohesion.Web.SecurityHeaders/src/FramingPolicy.cs:77` | [Certain] Existing factory validation rejects malformed ancestors before creating the policy. |
| CSP base64 digest or nonce text | New Assimalign.Cohesion.Web.SecurityHeaders.CspBase64Value | `resources/Web/Assimalign.Cohesion.Web.SecurityHeaders/src/ContentSecurityPolicySourceListBuilder.cs:170` | [Certain] Hash input already fails at composition; an invalid custom feature nonce is explicitly rejected during Render. |
| CSP sandbox token | New Assimalign.Cohesion.Web.SecurityHeaders.CspSandboxToken | `resources/Web/Assimalign.Cohesion.Web.SecurityHeaders/src/ContentSecurityPolicyBuilder.cs:166` | [Certain] Invalid token syntax already fails in the builder. |
| CSP reporting endpoint identifier | New Assimalign.Cohesion.Web.SecurityHeaders.ReportingEndpointName | `resources/Web/Assimalign.Cohesion.Web.SecurityHeaders/src/ContentSecurityPolicyBuilder.cs:203` | [Certain] Invalid token syntax already fails during composition. |
| CSP report-uri reference | New Assimalign.Cohesion.Web.SecurityHeaders.CspReportUri | `resources/Web/Assimalign.Cohesion.Web.SecurityHeaders/src/ContentSecurityPolicyBuilder.cs:227` | [Certain] Existing checks reject invalid values during builder calls. |
| Permissions-Policy feature key | New Assimalign.Cohesion.Web.SecurityHeaders.PermissionsPolicyFeatureName | `resources/Web/Assimalign.Cohesion.Web.SecurityHeaders/src/PermissionsPolicyBuilder.cs:44` | [Certain] Set already validates feature syntax before constructing the policy. |
| Permissions-Policy origin expression | New Assimalign.Cohesion.Web.SecurityHeaders.PermissionsPolicyOrigin | `resources/Web/Assimalign.Cohesion.Web.SecurityHeaders/src/PermissionsPolicyBuilder.cs:64` | [Certain] CreateAllowlist already rejects malformed configured origin expressions. |
| Multiplexed connection application error code | New Assimalign.Cohesion.Connections.ConnectionApplicationErrorCode | `libraries/Connections/Assimalign.Cohesion.Connections.Quic/src/QuicConnectionListenerOptions.cs:248` | [Certain] Current setters and abort/reset boundaries already validate. |
| Maximum concurrent connection upgrades, including TLS handshakes | New Assimalign.Cohesion.Connections.ConnectionUpgradeLimit | `libraries/Connections/Assimalign.Cohesion.Connections/src/Extensions/ConnectionLayerExtensions.cs:73` | [Certain] Both public boundaries already reject invalid values during composition. |
| Validated, wire-exact protocol endpoint location | New Assimalign.Cohesion.IdentityModel.Protocols.ProtocolEndpointLocation | `libraries/IdentityModel/Assimalign.Cohesion.IdentityModel.Protocols/shared/EndpointLocation.cs:18` | none found |
| Explicit endpoint port | New Assimalign.Cohesion.EndpointPort | `libraries/Core/Assimalign.Cohesion.Core/src/System/UriExtensions.cs:52` | [Certain] Initial invalid values already fail endpoint creation or return false from parsing. |
| Resource shutdown grace in seconds | New Assimalign.Cohesion.Hosting.Resources.ResourceStopGraceSeconds | `libraries/Hosting/Assimalign.Cohesion.Hosting.Resources/src/ResourceRuntime.cs:244` | [Certain] Public registration already rejects invalid values. |
| Fully qualified resource content root | New Assimalign.Cohesion.Hosting.Resources.ResourceContentRoot retain conversion to existing FileSystemPath | `libraries/Hosting/Assimalign.Cohesion.Hosting.Resources/src/ResourceContext.cs:224` | [Certain] Both current construction routes validate; no delayed failure was established. |
| Endpoint URI scheme | New Assimalign.Cohesion.UriScheme | `libraries/Core/Assimalign.Cohesion.Core/src/System/UriExtensions.cs:42` | [Certain] Endpoint creation already validates. |
| Endpoint host component input | New Assimalign.Cohesion.EndpointHost | `libraries/Core/Assimalign.Cohesion.Core/src/System/UriExtensions.cs:47` | none found |
| Endpoint path component without query or fragment | New Assimalign.Cohesion.EndpointPath | `libraries/Core/Assimalign.Cohesion.Core/src/System/UriExtensions.cs:57` | [Certain] Both builders already enforce this exclusion rule; no missing composition failure was found. |
| Adaptive memory-pool block size | New Assimalign.Cohesion.MemoryPoolBlockSize | `libraries/Core/Assimalign.Cohesion.Core/src/System/Buffers/AdaptiveMemoryPoolOptions.cs:19` | [Certain] The setter already rejects nonpositive values, and the pool snapshots the integer at construction. |
| Nonnegative memory-pool retention count | New Assimalign.Cohesion.MemoryPoolRetainedBlockCount | `libraries/Core/Assimalign.Cohesion.Core/src/System/Buffers/AdaptiveMemoryPoolPressurePolicy.cs:16` | [Certain] Current setters maintain both nonnegativity and ordering. |
| Command-line switch mapping key | New Assimalign.Cohesion.Configuration.CommandLine.ConfigurationCommandLineSwitchName | `libraries/Configuration/Assimalign.Cohesion.Configuration.CommandLine/src/ConfigurationCommandLineProvider.cs:178` | [Certain] Invalid mappings already fail composition when ConfigurationBuilder.BuildAsync invokes the provider factory, before loading providers. |
| DNS cache capacity | New Assimalign.Cohesion.Dns.DnsCacheCapacity | `libraries/Dns/Assimalign.Cohesion.Dns.Client/src/ForwardingDnsResolverOptions.cs:53` | [Certain] Enabled caches validate during resolver construction and snapshot the integer. |
| File name | Reuse System.IO.FileName in the Core package, with a checked-in ValueObjects/FileName.cs partial; retaining it | `libraries/Core/Assimalign.Cohesion.Core/src/System/IO/FileName.cs:33` | [Certain] The three external primitive-to-FileName production sites already validate: IOExtensions.GetFileName, PhysicalFileSystemFile.Name and InMemoryFileSystem file creation. |
| Byte size with an empty sentinel | Reuse System.Size in the Core package, with a checked-in ValueObjects/Size.cs partial; retaining its existing  | `libraries/Core/Assimalign.Cohesion.Core/src/System/Size.cs:14` | [Certain] Existing construction already rejects values below -1; no delayed input-validation failure was established. |
| Canonical authentication protocol identifier | Reuse Assimalign.Cohesion.IdentityModel.AuthenticationProtocol | `libraries/IdentityModel/Assimalign.Cohesion.IdentityModel/src/ValueObjects/AuthenticationProtocol.cs:27` | none found |
| Canonical protocol transport binding | Reuse Assimalign.Cohesion.IdentityModel.Protocols.ProtocolBinding | `libraries/IdentityModel/Assimalign.Cohesion.IdentityModel.Protocols/src/ValueObjects/ProtocolBinding.cs:31` | none found |
| Canonical protocol party role | Reuse Assimalign.Cohesion.IdentityModel.Protocols.ProtocolRole | `libraries/IdentityModel/Assimalign.Cohesion.IdentityModel.Protocols/src/ValueObjects/ProtocolRole.cs:26` | none found |
| Canonical protocol endpoint kind | Reuse Assimalign.Cohesion.IdentityModel.Protocols.ProtocolEndpointKind | `libraries/IdentityModel/Assimalign.Cohesion.IdentityModel.Protocols/src/ValueObjects/ProtocolEndpointKind.cs:28` | none found |
| Resource ports and the automatic-allocation sentinel | New Assimalign.Cohesion.ApplicationModel.ResourcePort and RequestedResourcePort | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/src/Manifests/ResourceManifest.cs:497` | [Certain] Normal builder and manifest paths already validate during composition. |
| Registered mount-source provider name | New Assimalign.Cohesion.ApplicationModel.ResourceSourceName | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/src/Internal/Providers/ResourceSourceProviderDictionary.cs:18` | none found |
| Digest-pinned opaque container image reference | New Assimalign.Cohesion.ApplicationModel.Gateway.DigestPinnedImageReference | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Gateway/src/Extensions/ApplicationGatewayResourceExtensions.cs:35` | [Certain] AddContainer already validates at composition. |
| Positive replica count | New Assimalign.Cohesion.ApplicationModel.ResourceReplicaCount | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/src/Manifests/ResourceManifest.cs:628` | [Certain] Build validates manifest and planned counts, so no delayed operation failure was found. |
| Positive graceful-stop budget in seconds | New Assimalign.Cohesion.ApplicationModel.ResourceStopGraceSeconds | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/src/Manifests/ResourceManifest.cs:647` | [Certain] Both manifest and plan checks run during normal composition; no delayed failure was established. |
| Resource endpoint URI scheme | New Assimalign.Cohesion.ApplicationModel.ResourceEndpointScheme | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/src/Planning/ResourcePlanValidator.cs:271` | [Certain] Normal generated plans are checked during Build. |
| Hexadecimal manifest or closure SHA-256 digest | New Assimalign.Cohesion.ApplicationModel.ManifestDigest | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/src/Documents/ApplicationExportResource.cs:107` | [Certain] Export validation already checks syntax before supported serialization/import operations. |
| Single-line planner diagnostic label | New Assimalign.Cohesion.ApplicationModel.ResourcePlannerName | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/src/Internal/ApplicationBuilder.cs:709` | [Certain] Invalid labels already fail during Build, before diagnostic output. |
| Fully qualified in-process content root | New Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.InProcessContentRoot | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Gateway.InProcess/src/Extensions/InProcessResourceDescriptorExtensions.cs:54` | [Certain] Both methods already reject relative roots during registration. |
| Platform-neutral artifact reference | Generate existing Assimalign.Cohesion.ApplicationModel.ArtifactRef | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel/src/ValueObjects/ArtifactRef.cs:13` | [Certain] Direct construction can currently create blank references, but converter/plan paths reject them. |
| Local certificate filename stem | New Assimalign.Cohesion.ApplicationModel.Gateway.LocalCertificateLeafName | `libraries/ApplicationModel/Assimalign.Cohesion.ApplicationModel.Gateway/src/Internal/GatewayCertificateAuthority.cs:41` | [Certain] Validation currently occurs during issuance after directory/root setup. |
| Markdown heading level | New Assimalign.Cohesion.Content.Markdown.MarkdownHeadingLevel | `libraries/Content/Assimalign.Cohesion.Content.Markdown/src/Nodes/MarkdownHeading.cs:25` | none found |
| Ordered Markdown list start | New Assimalign.Cohesion.Content.Markdown.MarkdownListStart | `libraries/Content/Assimalign.Cohesion.Content.Markdown/src/Nodes/MarkdownList.cs:28` | [Certain] Negative assignments already fail. |
| Unsigned 16.16 fixed-point value | Generate existing FPoint over UInt owning RootNamespace is Assimalign.Cohesion.Content.Media | `libraries/Content/Assimalign.Cohesion.Content.Media/src/ValueTypes/FPoint.cs:5` | [Certain] No input-validation or delayed-failure bug is removed. |
| Known EBML element data size | New Assimalign.IO.Ebml.EbmlDataSize | `libraries/Content/Assimalign.Cohesion.Content.Ebml/src/VInt.cs:69` | none found |
| Payload accepted by VInt.MakeId | New Assimalign.IO.Ebml.EbmlElementIdData | `libraries/Content/Assimalign.Cohesion.Content.Ebml/src/VInt.cs:98` | [Certain] Capacity already validates at entry. |
| Explicit EBML VINT width | New Assimalign.IO.Ebml.EbmlVIntWidth | `libraries/Content/Assimalign.Cohesion.Content.Ebml/src/VInt.cs:13` | [Certain] UnknownSize(0) passes its initial guard and then throws in the private constructor; this is an intra-operation inconsistency. |
| Nonnegative positional file offset | New Assimalign.Cohesion.FileSystem.FileSystemOffset | `libraries/FileSystem/Assimalign.Cohesion.FileSystem/src/Abstractions/IFileSystemFileHandle.cs:27` | none found |
| Nonnegative file resize length | New Assimalign.Cohesion.FileSystem.FileSystemLength | `libraries/FileSystem/Assimalign.Cohesion.FileSystem/src/Abstractions/IFileSystemFileHandle.cs:39` | none found |
| Configured OTLP request header name | New Assimalign.Cohesion.OpenTelemetry.OtlpRequestHeaderName Existing HttpHeaderKey is unsuitable without redes | `libraries/OpenTelemetry/Assimalign.Cohesion.OpenTelemetry/src/OtlpExporterOptions.cs:15` | none found |
| Configured OTLP request header value | New Assimalign.Cohesion.OpenTelemetry.OtlpRequestHeaderValue Existing HttpHeaderValue is a multi-value collect | `libraries/OpenTelemetry/Assimalign.Cohesion.OpenTelemetry/src/OtlpExporterOptions.cs:14` | [Certain] Invalid configured headers already fail composition. |
| Nonnegative logical cache size | New Assimalign.Cohesion.Caching.CacheSize use CacheSize? for unspecified entry sizes and limits | `libraries/Cache/Assimalign.Cohesion.Caching/src/Abstractions/ICacheEntry.cs:81` | [Certain] Negative entry Size is accepted until MemoryCacheEntry.Dispose calls CommitEntry, which throws CacheException(InvalidEntry). |
| Circuit-breaker failure threshold | New Assimalign.Cohesion.Resilience.CircuitBreakerFailureThreshold | `libraries/Resilience/Assimalign.Cohesion.Resilience.CircuitBreaker/src/CircuitBreakerStrategyOptions.cs:19` | none found |
| Hedging attempt limit | New Assimalign.Cohesion.Resilience.HedgingAttemptLimit | `libraries/Resilience/Assimalign.Cohesion.Resilience.Hedging/src/HedgingStrategyOptions.cs:19` | none found |
| Positive resilience rate-limiter permit count | New Assimalign.Cohesion.Resilience.RateLimitPermitCount | `libraries/Resilience/Assimalign.Cohesion.Resilience.RateLimiting/src/RateLimiterStrategyOptions.cs:20` | none found |
| Positive OTLP queue or batch record count | New Assimalign.Cohesion.OpenTelemetry.OtlpRecordCount | `libraries/OpenTelemetry/Assimalign.Cohesion.OpenTelemetry/src/OtlpExporterOptions.cs:21` | none found |
| Nonnegative validation-queue capacity | New Assimalign.Cohesion.ObjectValidation.ValidationQueueCapacity | `libraries/ObjectValidation/Assimalign.Cohesion.ObjectValidation/src/ValidationItemQueue.cs:30` | none found |
| Identity command client and audience identifiers | New IdentityCommandIdentifier namespace Assimalign.Cohesion.ApplicationModel | `resources/IdentityHub/Assimalign.Cohesion.IdentityHub.ApplicationModel/src/Extensions/IdentityHubResourceCommandExtensions.cs:66` | none found |
| Identity command credential mount source | New IdentityHubCredentialSource namespace Assimalign.Cohesion.ApplicationModel | `resources/IdentityHub/Assimalign.Cohesion.IdentityHub.ApplicationModel/src/Extensions/IdentityHubResourceCommandExtensions.cs:45` | [Certain] Literal sources already fail composition. |
| Logical secret seed path | New SecretStoreSeedPath namespace Assimalign.Cohesion.SecretStore | `resources/SecretStore/Assimalign.Cohesion.SecretStore.Hosting/src/SecretStoreApplicationBuilder.cs:111` | none found |
| Configuration entry key | New ConfigurationEntryKey namespace Assimalign.Cohesion.ConfigurationStore. Existing Configuration.Key has inc | `resources/ConfigurationStore/Assimalign.Cohesion.ConfigurationStore.Hosting/src/Internal/ConfigurationNamespaceBuilder.cs:16` | [Certain] Existing composition paths already reject invalid keys; no deferred invalid-key failure established. |
| Single-segment namespace created by a declaration command | New ConfigurationDeclaredNamespaceName namespace Assimalign.Cohesion.ApplicationModel | `resources/ConfigurationStore/Assimalign.Cohesion.ConfigurationStore.ApplicationModel/src/Extensions/ConfigurationStoreResourceCommandExtensions.cs:27` | none found |
| Rezolvr record name or CNAME target | New RezolvrRecordName namespace Assimalign.Cohesion.ApplicationModel. Existing DnsName does not enforce this o | `resources/Rezolvr/Assimalign.Cohesion.Rezolvr.ApplicationModel/src/Extensions/RezolvrResourceCommandExtensions.cs:61` | [Certain] Composition already validates names. |
| Positive record lifetime in seconds | New RezolvrRecordTtlSeconds namespace Assimalign.Cohesion.ApplicationModel | `resources/Rezolvr/Assimalign.Cohesion.Rezolvr.ApplicationModel/src/Extensions/RezolvrResourceCommandExtensions.cs:28` | [Certain] Both declaration verbs already reject zero and negatives; no deferred TTL failure found on those paths. |
| Log query result limit | New LogQueryLimit namespace Assimalign.Cohesion.LogSpace.Hosting | `resources/LogSpace/Assimalign.Cohesion.LogSpace.Hosting/src/Internal/LogSpaceHttp.cs:107` | [Certain] The HTTP boundary already validates every request before calling storage; no bypassing public option was found. |

</details>
