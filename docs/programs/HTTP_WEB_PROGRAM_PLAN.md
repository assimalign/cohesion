# HTTP / Web Program Plan

**Status:** active — Phase 2 opened 2026-09-30, see §7 · **Created:** 2026-07-03 · **Owner:** Chase Crawford · **Scope:** the HTTP protocol stack (`libraries/Http*`), its cross-area foundations (`libraries/Connections`, `libraries/Security`, `libraries/Hosting`), and the Web resource (`resources/Web/*`, GitHub Web Platform epic **#6 / L03.01**).

> **Why this file exists.** This program spans ~50 GitHub work items across 8 epics and will be implemented by many separate AI coding sessions. No single session holds the whole picture in context. This document is the **durable sequencing index**: it records what depends on what, what is safe to do in parallel, and the protocol each session follows so the work scales out **without losing the order things must happen in**. GitHub issues hold the *what* and *acceptance criteria*; this file holds the *when* and *in-what-order*. It is a living doc — update the Progress Log and check items off as PRs merge.

This file is temporary scaffolding for the duration of the program. When the Web resource is assembled and this backlog is drained, fold anything durable into the relevant `docs/DESIGN.md` files and delete this doc.

---

## 1. How to run this across multiple sessions (read first)

The safe unit of work is **one GitHub issue = one session = one branch = one PR**. Do not batch unrelated issues into a session; they will collide and the sequencing breaks.

**The session protocol (every session follows this):**

1. **Pick an issue that is unblocked.** An issue is workable only if every entry in its *Blocked by* column (§4) is merged. Never start a blocked issue — its prerequisites define types/seams you would otherwise invent and later fight.
2. **Read three things before coding:** (a) the issue body and its acceptance criteria; (b) this plan's row for the issue in §4 and the lane guardrails in §3; (c) the repo coding rules (`.claude/rules/`, auto-loaded in Claude sessions) + the area's `docs/DESIGN.md`.
3. **Branch:** `feature/<wbs>-<slug>` naming the issue's WBS (e.g. `feature/L03.01.01.05-problem-details`). The `cohesion-work-items` skill infers scope-creep placement from this branch.
4. **Implement to the acceptance criteria.** If you discover out-of-scope work, file it with the `cohesion-work-items` skill (don't expand the current issue) and call it out in your PR description so the orchestrator can slot it into §4.
5. **Open a PR** with the `Closes #NNNN` block (use `New-CohesionWorkItem.ps1 -EmitClosesBlock` from the same worktree). Close the parent feature manually only when all its children are done.
6. **Do not edit this plan file.** The orchestrator reconciles the §5 Progress Log from merged PRs — this removes shared-doc merge conflicts when many sessions run in parallel. Just make sure your PR's `Closes #NNNN` block is correct; that is the signal the orchestrator reconciles from.

**Golden rule for parallelism:** issues in different **lanes** (§3) at the same **stage** (§2) can run concurrently in separate sessions with no coordination. Two sessions in the *same* lane touching the same project should be serialized — check the Progress Log for an in-flight sibling before starting.

### How to reference this plan when you prompt a session (avoiding confusion)

Reference **by issue number + this file path**, and let the plan tell the session what to do. Do **not** paste the whole plan into the prompt or say "work on the HTTP stuff" — that reintroduces the ambiguity this file removes.

**Recommended prompt template (copy/paste, fill the number):**

```
Work GitHub issue #NNNN in assimalign/cohesion.

Before coding, read docs/programs/HTTP_WEB_PROGRAM_PLAN.md — follow the Session Protocol
in §1, confirm the issue is unblocked per §4, and honor the lane guardrails in §3.
Follow the repo coding rules (auto-loaded from .claude/rules). Branch, implement to the
issue's acceptance criteria, and open a PR that closes it. Do not edit the plan file —
the orchestrator reconciles the §5 Progress Log from merged PRs.

Do not start any work its "Blocked by" prerequisites haven't merged; if it's blocked,
stop and tell me which prerequisite is outstanding.
```

**Variations:**
- *Let the session choose:* replace the first line with `Pick the highest-priority unblocked issue from Stage <N>, Lane <X> in docs/programs/HTTP_WEB_PROGRAM_PLAN.md and work it.` Good when you don't want to micromanage ordering.
- *A primitive that many things wait on* (e.g. #771, #762): add `This is a fan-out prerequisite — several issues are blocked on it (see §4), so keep the public surface conservative and get the DESIGN.md right.`
- *Kicking off several in parallel:* open one session per issue, each with the template above and a **different** issue number, only choosing issues that are (a) unblocked and (b) in different lanes. Send them at once.

**Anti-patterns that cause confusion:**
- Referencing "the plan" without the file path or an issue number → the session guesses.
- Giving one session two issues "since they're related" → branch/PR collision, and the dependency between them stops being enforced.
- Starting a Web-middleware issue before **#762** merges → you build on the accept loop that's being replaced.
- Re-deriving a primitive inline because "it's small" → duplicates a filed foundation item (e.g. inventing media-type parsing instead of consuming #771).

---

## 2. Stages (dependency gates)

A **stage** is a gate, not a calendar. Everything in a stage may proceed once the prior stage's items it depends on are merged. Within a stage, the **lanes** in §3 run in parallel. (Stages are finer-grained than the GitHub `Wave` field — treat Wave as a coarse hint and this document as the authority on order.)

| Stage | Theme | Gate to enter |
|---|---|---|
| **0 — Clear the ground** | Delete dead/duplicate code and fix trivially-independent defects so later work isn't built on confusion. | none |
| **1 — Foundations** | Protocol primitives, transport hardening, cross-area drivers, and the **one** Web-runtime blocker (#762). Everything downstream imports from here. | none (parallel with 0) |
| **2 — Build-out** | HTTP protocol features and the first wave of Web middleware + routing, each consuming Stage-1 primitives. | its Stage-1 prerequisites merged |
| **3 — Composition** | Features that compose multiple Stage-2 pieces (h3 end-to-end, caching, groups/links, health endpoint, WebSockets). | its Stage-2 prerequisites merged |
| **4 — Surface** | The developer-facing API surface that sits on everything: source-gen binding, auth handlers, controller/function execution. | its Stage-3 prerequisites merged |
| **5 — Make what ships safe** *(Phase 2)* | Fix security, protocol-conformance and DX defects in delivered code before adding surface (§7.2). | none |
| **6 — Endpoint-aware pipeline** | Let middleware run between route match and handler; endpoint metadata on typed endpoints; branching. | Stage 5 reviewed |
| **7 — Security and browser interop** | CORS, authorization, cookie policy, antiforgery, security headers. | #1054, #1055 |
| **8 — API surface** | Handler return values, validation, files, OpenAPI. | #1055 |
| **9 — Server and operations** | ALPN multi-protocol endpoints, diagnostics, lame-duck drain, telemetry, mTLS. | #1049 |
| **10 — Gated** | WebSockets, rewrite, OIDC, trailers/gRPC, distributed stores. | an ADR, a decision, or another program |

**The single most important edge in the whole program:** **#762 (rewrite `WebApplicationServer`) is the gate for nearly all Web middleware.** It is a Stage-1, P001 item. Land it early. Until it merges, the only Web-side work that is safe is the Stage-0 deletions and pure-primitive Http-library items.

---

## 3. Lanes (what can run in parallel) + per-lane guardrails

| Lane | Area | Projects | Guardrail (the thing sessions get wrong) |
|---|---|---|---|
| **A — HTTP transport** | protocol wire behavior | `libraries/Http/Assimalign.Cohesion.Http.Connections` | Internal types only; no DI/Logging/Config refs. Wire-level failure isolation already lives here — Web must not duplicate it. h3 changes gate on #748 (server control stream). |
| **B — HTTP primitives** | protocol value objects | `libraries/Http/Assimalign.Cohesion.Http` | Value objects with `TryParse`/serialize, span-based, AOT-safe, **no** field-value parsing in `Http.Connections`. These are the shared toolkit many Web items import — keep surfaces conservative, they're hard to change later. |
| **C — Cross-area foundations** | drivers & security & hosting | `libraries/Connections/*`, `libraries/Security/*`, `libraries/Hosting`, new `libraries/Health` | Peer-driver placement (`Connections.InMemory` beside Tcp/Udp/Quic). Security crypto is BCL-only, key material never hand-rolled again. |
| **D — Web runtime** | the composition root & server | `resources/Web/Assimalign.Cohesion.Web`, `...Web.Hosting` | **#762 first.** DI/Logging/Config integration happens **only** here (builder-time). No ASP.NET-style per-concern micro-packages. |
| **E — Web middleware** | request-pipeline features | `resources/Web/Assimalign.Cohesion.Web.*` feature projects | Each is a thin feature project consuming a Stage-1 primitive + the pipeline. Extensibility via `IHttpFeatureCollection` typed features, not request-time service location. All gate on #762. |
| **F — Routing & API surface** | endpoints, binding, formatting | `...Web.Routing`, `...Web.Api`, `...Web.ProblemDetails`, `analyzers/...SourceGeneration.Web` | Endpoint **metadata bag (#150)** is the seam auth/CORS/OpenAPI/docs consume — get it right early; AOT mandates source-gen for binding, never reflection. **Direction (2026-07-10): middleware-first** — fluent `.Use(...)` / `IWebApplicationMiddleware` composition over a return-value result model; IResult withdrawn pre-merge, controllers/functions set aside (`Web.Api.Controllers` + `Web.Functions` removed). |

Cross-cutting rules (all lanes): file-scoped namespaces; `CohesionProjectReference`/`CohesionPackageReference`; **no `Microsoft.Extensions.*`**; `IsAotCompatible=true`, no reflection; interface-first with internal impls; XML docs on public APIs; Shouldly tests co-located; create/update `docs/DESIGN.md` in the same change. The path-scoped rules in `.claude/rules/` are canonical and auto-load in Claude sessions.

---

## 4. The work items (with blockers)

Legend: **B** = HTTP primitives, **A** = HTTP transport, **C** = cross-area, **D** = Web runtime, **E** = Web middleware, **F** = routing/API. "Blocked by" lists only *hard* prerequisites (types/seams that must exist first); soft coordination is noted in the issue body.

### Stage 0 — Clear the ground (no blockers; do these first, any order)

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| #761 | D | Delete dead pre-redesign `Web.ApplicationModel` src | — |
| #766 | D | Delete vestigial `Web.Server` project | — |
| #759 | B | Retire `Assimalign.Cohesion.Http.Identity` (skeleton) | — |
| #768 | B | Fix `Sec-WebSocket-Protocol` header-key naming | — |
| #760 | B | True up `Http.Forms` docs + convenience surface | — |

### Stage 1 — Foundations

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| **#762** | **D** | **Rewrite `WebApplicationServer`: per-connection dispatch, error isolation, disposal, graceful stop** | — · **(gates most of Lane E)** |
| #763 | D | Add TLS convenience surface to the Web server builder | #762 |
| #791 | A | Enforce HTTP/1.1 server limits & timeouts (DoS-critical) | — |
| #764 | A | Harden HTTP/2 against abuse (rapid reset, CONTINUATION flood…) | — |
| #750 | A | Bound HTTP/2 request-body buffering (flow-control backpressure) | — |
| #757 | B | Harden cookie model per RFC 6265bis | — |
| #747 | B | RFC 9651 Structured Field Values parser/serializer | — |
| #771 | B | `HttpMediaType` + Accept/q-value negotiation primitives | — · **(fan-out)** |
| #792 | B | RFC 9110 range-request + precondition primitives | — |
| #770 | B | RFC 7239 `Forwarded` + `X-Forwarded-*` parsing primitives | — |
| #755 | B | Typed RFC 9111 caching primitives (Cache-Control, validators) | — |
| #772 | C | Build `Connections.InMemory` driver | — |
| #774 | C | Purpose-bound data protection + rotating key ring | — |
| #773 | C | Finish Unix domain sockets + add named-pipe driver | — |
| #748 | A | HTTP/3 server control stream (SETTINGS emission) | — · **(gates h3 fan-out)** |

### Stage 2 — Build-out

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| #769 | A/B | Streaming response write path (h1/h2/h3) + SSE primitives | #750 (soft) |
| #751 | A | Bridge HTTP/1.1 transport to ProtocolUpgrade (101) | — |
| #752 | A | 1xx interim responses (100-continue, 103 Early Hints) | — |
| #749 | A | Graceful GOAWAY drain (h2 window + h3 lifecycle) | #748 (h3 half) |
| #758 | A | QPACK dynamic table + encoder Huffman | #748 |
| #847 | A | Emit QPACK Section-Ack / Stream-Cancellation on the decoder stream | #758 ✓ |
| #753 | A/B | RFC 9218 extensible priorities | #747 |
| #756 | B | RFC 9530 Digest Fields | #747 |
| #746 | B | RFC 10008 HTTP QUERY method semantics | #747, #755 |
| #754 | A | Alt-Svc advertisement (RFC 7838) | — |
| #819 | A | Wire request-parse interceptors (#818 seam) into h2/h3 request paths | #818 ✓ |
| ~~#776~~ | E | ~~Pipeline exception boundary~~ — **superseded by #881** (PR #844 abandoned unmerged; branch kept as salvage reference) | — |
| #881 | E | Exception boundary, status-code pages, 404 terminal **via the #864 `OnError` hook** (was: over IResult; supersedes #776) | #864 |
| #877 | E | RFC 10008 server-side QUERY handling — umbrella for tasks #878 (Content-Type validation), #879 (redirect preservation), #880 (conditional-QUERY-as-GET); one session closes all four | #746 ✓, #762 ✓ |
| #876 | A/B | Content-Digest verification safe for h2 streamed bodies (lazy verify-on-read) | #756 ✓, #819 ✓ |
| #777 | E | `Web.StaticFiles` over the FileSystem library | #762, #792, #771 (the #864 edge dropped with IResult) |
| #778 | E | Forwarded-headers middleware + trust model | #762, #770 |
| #779 | E | `Web.Compression` (response + request) | #762, #769, #771 |
| #780 | E | `Web.HttpsPolicy` (HTTPS redirection + HSTS) | #763 |
| #781 | E | Host-filtering middleware (allowed hosts) | #762 |
| #783 | E | `Web.RateLimiting` (global limiter first) | #762 |
| #784 | E | Request-timeout policies over the #703 abort feature | #762 |
| #794 | E | `Web.Diagnostics` (HTTP logging + W3C access logs) | #762 |
| #785 | E | Async session-store seam + out-of-process sessions | #762 |
| #793 | E | `Web.Testing` factory over the in-memory driver | #762, #772 |
| #148 | F | Matcher precedence/405 fixes (existing) | — |
| #150 | F | Endpoint metadata bag (existing) — **fan-out seam** | — |
| #864 | F | **Content-serialization registry + `OnError` hook design** (re-scoped 2026-07-10; IResult withdrawn pre-merge — the ProblemDetails payload shipped separately as `Web.ProblemDetails` on PR #887) — **fan-out seam** | #769 ✓, #771 ✓ — design item, **unblocked** |
| #149 | F | Content negotiation over the #864 serializer registry (re-scoped; was ObjectResult/Ok<T> + IResultFormatter) | #864, #771 |
| #789 | F | Typed route values, constraints, per-app router state | #148 |

### Stage 3 — Composition

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| #767 | D | HTTP/3 (QUIC) registration surface on the Web builder | #762, #763, #748, #749 |
| #795 | E | `Web.Caching` (server-owned output caching) | #762, #755, #792 |
| #782 | E | `Web.Rewrite` (URL rewriting/redirects) | #762 + request-mutation seam decision (#24/#25) |
| #775 | C/E | Health-check framework + `/healthz` endpoint | #762 (endpoint half), host-lifecycle epics |
| #810 | A | h1 request-body data-rate limits + per-request body-size override | #769 (streaming-body rework) |
| #786 | F | Route groups (MapGroup) | #148, #150 |
| #787 | F | Named routes + LinkGenerator | #148 |
| #788 | F | Host-based route matching (RequireHost) | #150 |
| #765 | A/E | WebSockets decision + RFC 6455 (if "build") | #751 (h1), #748 (h3 via #382); supersedes #380–#382 |

### Stage 4 — Surface

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| #796 | F | Source-generated middleware/handler binding + validation (AOT) — middleware-first, no IResult | #150, #864, #771 |
| #790 | F | Auth scheme model + Cookie/Bearer handlers | #774, #150, IdentityModel #610 |
| ~~#151~~ | F | ~~Controller/action + function endpoint binding~~ — **set aside 2026-07-10** (niceties; middleware-first direction; `Web.Api.Controllers` + `Web.Functions` projects removed; closed not-planned) | — |

### Phase 2 (opened 2026-09-30)

The audit behind these stages is §7. Within each stage, rows are in the recommended build order. Project #13 fields: Stage 5 = P001/W01, Stages 6–8 = P002/W02–W04, Stage 9 = P003/W05, Stage 10 = W06.

### Stage 5 — Make what ships safe and honest

**Status:** delivered 2026-09-30 on the Phase 2 branch and awaiting owner review. Commits and follow-ups are in §5.

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| #1045 | E | Serve static files from a web root, never the process working directory (D1) | — |
| #1046 | E | Make JWT Bearer validation fail closed on issuer, audience and algorithm (D2) | — |
| #1047 | D | Bind a default listener in plain-mode apps; Sdk.Web content defaults; runnable template tests (D3) | — |
| #1048 | A | HTTP/2: flow control on buffered responses, HEAD suppression, request-body cap (D5) | — |
| #1066 | A | HTTP/3: incremental request streams, body cap, HEAD, malformed frames (D6) | — |
| #1049 | D | Dispatch HTTP/2 and HTTP/3 streams concurrently in `WebApplicationServer` (D4) | #1066 (soft, HTTP/3 half only) |
| #937 | A | Reject a malformed `:path` per stream on HTTP/2 and HTTP/3 (D10) | — |
| #1050 | E | Proxy-aware HttpsPolicy, HostFiltering, Sessions, Cookie auth, Compression, Diagnostics (D7) | — |
| #1051 | F | Build the router at startup; route-template error messages; cancellation (D8) | — |
| #1052 | D | Representative Web NativeAOT guard published in CI | — |
| #1053 | B | CI and a release decision for Http.ServerSentEvents, DigestFields and InterimResponses (D11) | — |
| #699 | B | Replace the AttachContext wire-up with an HttpContext back-reference | serialize with #1048, #1066 and #937 (same transport files) |

### Stage 6 — Endpoint-aware pipeline (fan-out seam)

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| #1054 | F | Split route matching from endpoint dispatch; preflight-aware matching | — |
| #1055 | F | Return an endpoint builder from `Map*` so endpoints and groups carry metadata | #1054 |
| #1056 | D/F | Pipeline branching (`Map`, `MapWhen`, `UseWhen`, `Run`) and fallback routes | #1054 |

### Stage 7 — Security and browser interop

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| #3 (+#109, #110, #111) | E | CORS policy engine and endpoint integration; one session closes all four | #1054, #1055 |
| #155 | E | Authorization: requirements, policies, `UseAuthorization`, per-endpoint schemes | #1054, #1055 |
| #156 | E | Cookie-policy enforcement, including a secure default for Cookie auth | — |
| #1057 | E | Enforce antiforgery in the Web pipeline | #1054, #1055 |
| #1058 | E | Security-headers middleware | #1055 (soft) |

### Stage 8 — API surface

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| #1059 | F | Serialize handler return values; compile-time diagnostics for unsupported handlers | #1055 |
| #1060 | F | Validate bound parameters; validation problem responses | #1059 |
| #1061 | F | File binding; file and stream response helpers | #1060; #1057 (soft) |
| #1062 | C | Onboard the remaining OpenApi packages to CI and the release | — |
| #152 | F | Web OpenAPI adapter (`IOpenApiEndpointSource`), document endpoint, AOT test | #1055, #1059, #1062 |

### Stage 9 — Server and operations

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| #1063 | D/A | HTTP/1.1 and HTTP/2 on one TLS endpoint via ALPN; complete server config binding (D9) | #1049 (soft) |
| #147 | D | Hosting diagnostics: startup failures, connection faults, drain timeouts | — |
| #146 | D | Lame-duck drain | #1049 |
| #1064 | D/A | Server telemetry (`ActivitySource`, `Meter`, trace context); settles #1039 | — (`http.route` is exact only after #1054) |
| #1065 | A/C | TLS client certificates and connection TLS metadata (mTLS) | — |

### Stage 10 — Gated

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| #765 | A/E | WebSockets (RFC 6455) | an ADR |
| #782 | E | `Web.Rewrite` | the request-mutation seam decision |
| #829, #830 | C | OIDC discovery/JWKS runtime and keyed token crypto in IdentityModel; after those, an OIDC handler and JWT Bearer `Authority` discovery | the IdentityModel program |
| — | A | HTTP/2 and HTTP/3 trailers | a gRPC ADR |
| #806–#808 | C | Distributed data-protection key storage; distributed session and output-cache stores get filed alongside | post-v1 |

### Post-v1 follow-ups (filed, not scheduled)
Discovered on #774 and deferred out of its v1: **#806** (SecretStore-backed `IKeyRepository` + escrow), **#807** (at-rest key-document encryption), **#808** (cross-service key sharing) — align with SecretStore #99/#277/#278; pull in when the identity/secret-store lanes need them, not before.

### Deliberately NOT in this program (ADR-gated)
Real-time hub framework (SignalR-analogue) and gRPC hosting are **decisions, not features** — each needs an ADR first (real-time gates on the #765 WebSocket outcome; gRPC gates on a protobuf-vs-code-first serialization decision). Do not start either without a recorded decision. Skipped entirely: IIS/HTTP.sys, OWIN, SPA dev-proxy, Razor/Blazor UI, request localization.

---

## 5. Progress Log (orchestrator-reconciled from merged PRs)

The orchestrator maintains this table by reconciling merged PRs from GitHub; sessions do not edit it (that avoids shared-doc merge conflicts when many run in parallel). Each row is a merged item and the dependents it unblocks.

- **Wave 1 (2026-07-03): 13 merged** — the foundational spine (#762 gate, #747/#771 fan-out primitives, #774 data protection, #772 in-memory driver, #748 h3 control stream, #791 h1 limits + #818 interceptor seam) plus all Stage-0 cleanup.
- **Wave 2 (2026-07-06): 16 merged + #776 in review ([PR #844](https://github.com/assimalign/cohesion/pull/844))** — #763 TLS, #150 endpoint metadata, #148 matcher, #792 range, #770 forwarded, #755 caching, #769 streaming/SSE, #751 upgrade bridge, #749 GOAWAY drain, #758 QPACK dynamic, #819 interceptor h2/h3, #753 priorities, #756 digest, #764 h2 abuse, #750 h2 backpressure, #757 cookies. Scope-creep filed: #847 (QPACK decoder-stream ack). **→ Stage 1 (Foundations) is complete; nearly the entire remaining backlog is now unblocked leaf work — see the frontier note below.**
- **Frontier after Wave 2:** unblocked and workable — Web middleware #777/#778/#779/#780/#781/#783/#784/#785/#793/#794/#795, #767 (UseHttp3), routing #786/#787/#788/#789 + #149 (result writers, gates #796→#151), #746 (QUERY), #752 (1xx), #754 (Alt-Svc), #810 (h1 data-rate), #847, #790 (auth handlers — all deps now merged), #773 (UDS/pipes), #775 (health). **Still gated on a decision, not a dependency:** #765 (WebSockets ADR — #751/#748 now merged, so it's actionable) and #782 (URL-rewrite request-mutation seam). Blocked only by #149: #796 (source-gen binding) → #151 (controllers).
- **Wave 3a (2026-07-06): 9 merged** — #790 auth scheme+Cookie/Bearer handlers, #746 QUERY (RFC 10008), #789 typed constraints + per-app router, #775 health checks + /healthz, #773 UDS/named pipes, #752 1xx interim, #754 Alt-Svc, #810 h1 data-rate, #847 QPACK decoder acks.
- **#776 → #881 supersession (2026-07-06):** PR #844 deliberately abandoned unmerged (would have been immediately refactored onto #864); #776 closed as superseded. **#864** now owns the ProblemDetails payload/writer (`Problem` built-in; salvage reference = the closed #844 branch `feature/L03.01.01.05-problem-details`) and is **fully unblocked**; **#881** rebuilds the exception boundary / status-code pages / 404 terminal as an IResult consumer, blocked by #864. The Web-middleware cluster (#781/#783/#784/#785/#794) now takes its boundary/registration pattern from #881.
- **Wave 4 batching (2026-07-11):** **Batch 4a dispatched** — #864 (serializer registry + `OnError` hook design, lead/fan-out), the collision-light half of the middleware cluster (#777 static files, #778 forwarded, #781 host filter, #784 timeouts, #794 logging), #877(+#878–#880) server-side QUERY, #876 h2-safe digest. Every new-Web-project session touches `frameworks/Assimalign.Cohesion.App.props` + the CI matrix (one line each — trivial rebases expected). **Batch 4b (after #864 merges):** #881, #149, #796 + remaining middleware #779/#780/#783/#785/#795/#767. **Standing decision gates:** #782 (request-mutation seam) and #765 (WebSockets, deferred).
- **Wave 3b (2026-07-10): 4 merged + 1 partially merged/re-scoped** — #786 route groups ([PR #883](https://github.com/assimalign/cohesion/pull/883)), #787 LinkGenerator ([PR #885](https://github.com/assimalign/cohesion/pull/885)), #788 host matching ([PR #884](https://github.com/assimalign/cohesion/pull/884)), #793 Web.Testing factory ([PR #886](https://github.com/assimalign/cohesion/pull/886)); #864's [PR #887](https://github.com/assimalign/cohesion/pull/887) merged the Web-area hosting-isolation rules, App.Web framework delivery, and `Web.ProblemDetails` — with the IResult implementation withdrawn pre-merge (see the direction change below). New items filed during the wave: #876 (h2-safe digest verification), #877/#878/#879/#880 (server-side QUERY handling, from #746).
- **Wave 4a (2026-07-16): 8 merged** — #864 serialization registry + OnError hook ([PR #893](https://github.com/assimalign/cohesion/pull/893)), #777 static files ([#897](https://github.com/assimalign/cohesion/pull/897)), #778 forwarded headers ([#892](https://github.com/assimalign/cohesion/pull/892)), #781 host filtering ([#891](https://github.com/assimalign/cohesion/pull/891)), #784 request timeouts ([#894](https://github.com/assimalign/cohesion/pull/894)), #794 HTTP logging ([#896](https://github.com/assimalign/cohesion/pull/896)), #877(+#878/#879/#880) server-side QUERY ([#898](https://github.com/assimalign/cohesion/pull/898)), #876 lazy digest ([#889](https://github.com/assimalign/cohesion/pull/889)). Scope-creep filed during the wave: #890 (RouteHostConstraint → HttpHost rebase), #895 (h1 percent-decode parity).
- **Batch 4b (2026-07-19): dispatched as a STACKED PR series [#920](https://github.com/assimalign/cohesion/pull/920)→[#927](https://github.com/assimalign/cohesion/pull/927)** — orchestrated in one session (agent sessions + orchestrator review) rather than spun-off chips; one branch per issue, each based on the previous, merge bottom-up in order #881 → #149 → #779 → #780 → #783 → #785 → #795 → #767 (GitHub retargets each PR as its base merges). #796 (source-gen binding, Stage 4) deliberately held for focused follow-up after this stack; standing decision gates #782 (request-mutation seam) and #765 (WebSockets) unchanged. Scope-creep filed: #928 (h3 server control-stream defect — `H3_CLOSED_CRITICAL_STREAM` blocks full h3 round-trips; surfaced by #767's e2e, reproduced against the pre-existing Http.Connections example).
- **Wave 4b (2026-07-20): all 8 stacked PRs merged** — #881 ([PR #920](https://github.com/assimalign/cohesion/pull/920)), #149 ([#921](https://github.com/assimalign/cohesion/pull/921)), #779 ([#922](https://github.com/assimalign/cohesion/pull/922)), #780 ([#923](https://github.com/assimalign/cohesion/pull/923)), #783 ([#924](https://github.com/assimalign/cohesion/pull/924)), #785 ([#925](https://github.com/assimalign/cohesion/pull/925)), #795 ([#926](https://github.com/assimalign/cohesion/pull/926)), #767 ([#927](https://github.com/assimalign/cohesion/pull/927)). Squash-merge note for future stacks: a stacked branch whose wiring lines sit alphabetically adjacent to a sibling's (Web.Caching next to Web.Compression in App.props/CI/slnx) conflicts on retarget — resolved by merging main into the branch (#926's `303a9b2e`).
- **Batch 5 (2026-07-20): dispatched as a STACKED PR series [#932](https://github.com/assimalign/cohesion/pull/932)→[#936](https://github.com/assimalign/cohesion/pull/936)** — merge bottom-up in order #928 → #895 → #890 → #796 (PRs #932, #933, #934, #936). #928's root cause was the h3 send path never ending the request stream (FIN), not the control stream; #895 made h1 percent-decode parity uniform (decoded space/control chars are illegal `HttpPath` characters on every transport — widening that set is an owner decision if space-named resources should serve); #796 landed to the middleware-first re-scope (typed-delegate input binding via the interceptor generator + ObjectValidation AOT hardening; no result types). **With #796, §4 is drained except the two decision gates** — #782 (request-mutation seam) and #765 (WebSockets ADR); the plan's retirement clause becomes actionable once they resolve. Scope-creep filed: #937 (graceful h2/h3 malformed-`:path` rejection, from #895).
- **Batch 5 merged (2026-07-20):** #928 ([PR #932](https://github.com/assimalign/cohesion/pull/932)), #895 ([#933](https://github.com/assimalign/cohesion/pull/933)), #890 ([#934](https://github.com/assimalign/cohesion/pull/934)), #796 ([#936](https://github.com/assimalign/cohesion/pull/936)). §4 drained except the two decision gates (#782, #765).
- **Project #13 inventory (2026-09-29):** closed #25, #28 and #142–#145 as delivered, #153 as not planned, #154 and #380–#382 as duplicates; added "Scope update" notes to #27, #29, #146, #147, #152 and #155.
- **Phase 2 opened (2026-09-30):** a code-read audit of `main` found that §4 never scheduled the May-filed security and API items (CORS #3/#109–#111, authorization #155, cookie policy #156, OpenAPI #152, host lifecycle #146/#147), and that shipped code carries security, conformance and DX defects. Snapshot, defects and decisions are in §7. The owner approved the lineup the same day; it was filed as #1045–#1066 and added to §4 as Stages 5–10. By owner instruction, Phase 2 runs on one branch (`claude/http-web-program-inventory-798a2d`) with at least one commit per stage and an owner review before each next stage starts, in place of §1's one-issue-per-branch protocol.
- **Stage 5 delivered (2026-09-30), awaiting owner review.** One commit per issue on the Phase 2 branch, not pushed:
  - #1046 `cd47f0d0`, #1045 `656233d3`, #1047 `5da6d7dc`, #1053 `8cfffc11`, #1050 `23a2874c`
  - #1052 `e190ca7b` (DI call-site factory made AOT-clean) and `f4b509d5` (guard and CI job)
  - #1048 `10def15e`, #937 `fdd4f232` (HTTP/2) and `7667fb7e` (HTTP/3), #1051 `a4401268`, #1049 `db758492`
  - #1066 `f45b4e30`, plus `2a2e0afc` (Content-Digest verified lazily on HTTP/3)
  - #699 `0ebbdc1d`
  - `2ac5c565` regenerated `docs/DEPENDENCIES.md`; `70f2ad34` fixed the §7.5 docs drift.

  Verification: every touched suite passes, including Http.Connections 520, Http 1231, Web.Hosting 136 and Web.Testing 19. The Web NativeAOT guard publishes and passes 7/7 smoke checks. The `cohesion-web` and `cohesion-spa` template run tests pass against a locally packed SDK.

  #1047 had a second root cause: plain resource executables were framework-dependent on frameworks that ship only as runtime packs. The base SDK now defaults every resource executable to self-contained.

  Behavior changes for the review:
  - `AddJwtBearer` throws at registration when issuers or audiences are unset.
  - `UseStaticFiles()` serves only `<content root>/wwwroot`.
  - Plain entry points bind `Http:Endpoints`, or `127.0.0.1:5000` when none is configured.
  - HTTP/2 and HTTP/3 enforce the body cap with 413.
  - Routes mapped after start throw.
  - A faulting HTTP/2 or HTTP/3 exchange gets a 500 or a stream reset instead of taking down the connection. HTTP/1.1 mid-body limit breaches now surface as 500 (#1071).
  - A Content-Digest mismatch on HTTP/3 surfaces at the terminal read instead of a pre-dispatch 400.

  Scope-creep filed: #1071–#1076 and #1077–#1085. #1080 records that real-QUIC resets carry the driver's default error code until the Connections contract can carry one.
- **Direction change (2026-07-10, owner decision):** the Web API surface is **middleware-first** — composition via fluent `.Use(...)` / `IWebApplicationMiddleware`, not a return-value result model. The #864 IResult implementation was withdrawn from PR #887 before merge (Cohesion has no return-value handler seam; the abstraction was premature ahead of #796/#151 — and #151 is now set aside entirely). What survived: the RFC 9457 payload as **`Web.ProblemDetails`** (model + AOT-safe writer + `WriteProblemDetailsAsync`), plus PR #887's Web-area hosting-isolation rule (build-enforced, `build/Targets/Build.Rules.targets`) and App.Web framework delivery. **#864 is re-scoped** to the *content-serialization registry + `OnError` hook* design: builder-time registration of request/response formatting (media-type-keyed, AOT via resolver registration) and a fault hook through which applications own error responses (overridable default renders problem+json). #149 negotiates over that registry; #881 builds the boundary on the hook; #777's #864 edge dropped. `Web.Api.Controllers` and `Web.Functions` projects were removed; #151 closed as set-aside.

| Date | Issue | PR | Notes |
|---|---|---|---|
| 2026-07-03 | #768 | [#797](https://github.com/assimalign/cohesion/pull/797) | Stage 0 · Lane B. Corrected `HttpHeaderKey.SecWebSocketProtocol` to emit `Sec-WebSocket-Protocol` (RFC 6455 §11.3.4) and removed the redundant `WebSocketSubProtocols` alias (one canonical key remains). Added round-trip tests over `IHttpHeaderCollection`. |
| 2026-07-03 | #762 | [#803](https://github.com/assimalign/cohesion/pull/803) | Stage 1 · Lane D · P001. `WebApplicationServer` rewritten: per-connection dispatch (idle keep-alive no longer starves others), application-exception isolation, connection+context disposal, graceful `StopAsync` drain, optional `MaxConcurrentConnections`. Web.Hosting `docs/DESIGN.md` added. **Unblocks most of Lane E** (#763, #776–#781, #783–#785, #793–#794). |
| 2026-07-03 | #748 | [#802](https://github.com/assimalign/cohesion/pull/802) (open) | Stage 1 / Lane A. HTTP/3 server control stream: SETTINGS emission (`ENABLE_CONNECT_PROTOCOL=1`, QPACK cap=0), critical-stream / connection-first teardown, inbound post-SETTINGS drain (GOAWAY / MAX_PUSH_ID parse-and-discard). **On merge, unblocks** the h3 half of #749, plus #758, #767, and WebSocket-over-h3 #382. |
| 2026-07-03 | #774 | [#809](https://github.com/assimalign/cohesion/pull/809) | Stage 1 · Lane C — new `libraries/Security/Assimalign.Cohesion.Security.DataProtection`: purpose-bound protector (AES-256-GCM + HKDF-SHA256, versioned key-id header), rotating key ring with grace-period unprotect, file-system `IKeyRepository`. Rewired `Http.Antiforgery` onto a pluggable `IHttpAntiforgeryProtector` seam (no Security dependency). Follow-ups filed: #806 (SecretStore repo/escrow), #807 (at-rest key encryption), #808 (cross-service sharing); Web.Hosting builder-time wiring stays gated on #762. Unblocks #790. |
| 2026-07-03 | #772 | [#812](https://github.com/assimalign/cohesion/pull/812) | Stage 1 · Lane C. Built `libraries/Connections/Assimalign.Cohesion.Connections.InMemory` as a Tcp/Udp/Quic peer: cross-wired duplex-pipe connection pairs (`InMemoryConnectionPair.Create`, `InMemoryConnectionListener` + `InMemoryConnectionFactory`) supporting live multi-round-trip exchange, plus a multiplexed variant (`InMemoryMultiplexed…`) for h2/h3-shaped stream tests. Migrated the four transport test projects' duplicated pipe-pair doubles onto it (Security uses the driver directly; Connections/Http.Connections/Amqp re-base their `TestConnection` as thin adapters over the driver, deleting the bespoke pipe wiring). **Unblocks #793** (Web.Testing factory). |
| 2026-07-03 | #760 | [#801](https://github.com/assimalign/cohesion/pull/801) | Stage 0 · Lane B. Http.Forms trued up: README rewritten to match the shipped parsers; `context.ReadFormAsync(...)` + `request.Form` setter added; urlencoded charset parameter honored (AOT-safe allow-list); unused `BufferBody`/`BufferBodyLengthLimit` options removed and `MultipartBoundaryLengthLimit` enforced; `docs/DESIGN.md` created; `Web.Forms` `UseForms()` middleware tests added. |
| 2026-07-03 | #771 | [#805](https://github.com/assimalign/cohesion/pull/805) | Stage 1 / Lane B fan-out prerequisite. `HttpMediaType` + `HttpQuality`, `HttpAcceptParser` (Accept family), `HttpContentNegotiation` (media-type / token / encoding), and the `HttpContentTypes` FrozenDictionary. **In review** — unblocks #149, #746, #777, #779 on merge. |
| 2026-07-03 | #747 | [#804](https://github.com/assimalign/cohesion/pull/804) _(open)_ | RFC 9651 Structured Field Values toolkit (Items/Lists/Dictionaries + all 8 bare types incl. Date & Display String) in core `Assimalign.Cohesion.Http`. Fan-out foundation: unblocks #753 (Priority), #756 (Digest), #746 (QUERY/Accept-Query), and future Proxy-Status / Signature-Input. Lands as span-based AOT-safe value objects (`StructuredField*`); `HttpFieldRules` stays name-classification only. |
| 2026-07-03 | #791 (Stage 1 / Lane A) | [#811](https://github.com/assimalign/cohesion/pull/811) | HTTP/1.1 server limits & timeouts (414/431/413, keep-alive + request-headers timeouts, `HttpServerLimits`, Web.Hosting config binding). Data-rate limits deferred to #810 (streaming-body rework). Had no blockers. |
| 2026-07-03 | #818 (Lane A/B, discovered on #791's branch) | [#811](https://github.com/assimalign/cohesion/pull/811) | Request-parse interceptor seam (`IHttpRequestInterceptor` + context + typed rejection in core Http) + new `Http.RequestLimits` package owning `IHttpMaxRequestBodySizeFeature` (moved out of core); transport decoupled from feature packages; Web.Hosting default-installs the limits interceptor. h2/h3 hook wiring is #819; future parse-time consumers: #756 digest, #779 decompression. |
| 2026-07-03 | #761 | [#799](https://github.com/assimalign/cohesion/pull/799) | Stage 0 · Lane D — deleted dead pre-redesign `Web.ApplicationModel/src`; csproj + slnx preserved for the Phase-4 Layer-3d manifest rebuild (`libraries/ApplicationModel/DESIGN.md` §9.4). No `Assimalign.Cohesion.Transports` code references remain. |
| 2026-07-03 | #759 | [#798](https://github.com/assimalign/cohesion/pull/798) | Stage 0 (Lane B). Retired the `Assimalign.Cohesion.Http.Identity` skeleton — deleted the directory and removed its entries from all three solution files. Restores the commit-481a6fb layering invariant: no `System.Security.Claims`-typed public surface in the `Assimalign.Cohesion.Http` protocol core. Decision recorded in `resources/Web/Assimalign.Cohesion.Web.Authentication/docs/DESIGN.md`: no `request.User` accessor absorbed (redundant with the existing `context.User`; the skeleton's `ClaimsPrincipal.Current` fallback deliberately not carried over). |
| 2026-07-03 | #766 | [#800](https://github.com/assimalign/cohesion/pull/800) | Stage 0 (Lane D) · Deleted the vestigial `Web.Server` project pair; `UseHttp1`/`UseHttp2(Action<TcpConnectionListenerOptions>)` sugar now lives only in `WebHostingExtensions` (Web.Hosting, deferred-factory form), with new wrapper tests added. |

---

## 6. Fast reference

- **Epics:** Http `#314` (L01.01.11) · Net/Connections `#324` (L01.01.14) · Security `#325` (L01.01.18) · Hosting `#313` (L01.01.10) · Web Platform `#6` → Runtime/Pipeline `#24`/`#25`/`#26`, API/Tooling `#27`, Routing `#28`, Security/Browser `#2`/`#3`/`#30`.
- **Skills/rules:** coding rules auto-load from `.claude/rules/` · `cohesion-work-items` skill (file scope-creep, emit PR close blocks).
- **Canonical rules:** `.claude/rules/` (auto-loaded). **Roadmap context:** `docs/programs/DELIVERY_ROADMAP.md`.
- **This program's north star:** assemble the Web resource by wiring the `libraries/Http` stack into `resources/Web`; once assembled, the next major effort is pulling the new ApplicationModel design together (`libraries/ApplicationModel/DESIGN.md`).

---

## 7. Phase 2 — completeness inventory and lineup (2026-09-30)

**Why this section exists.** §4 drained on 2026-07-20, but §4 was never the whole Web backlog. The May-filed security and API items were never scheduled here: CORS **#3** (tasks #109–#111), authorization **#155** and cookie policy **#156** (under #30), OpenAPI **#152** (under #29), and host lifecycle **#146**/**#147** (under #26). So "drained" meant this plan's list was empty, not that the Web framework was complete.

This section is a code-read audit of `main` at `0e28bf16`. Nothing was built or run, so runtime effects below are inferred from the code. The owner approved the lineup on 2026-09-30. It was filed the same day as #1045–#1066 and moved into §4 as Stages 5–10, which is the authority for order and blockers; the tracking column in §7.3 carries the issue numbers.

### 7.1 What ships today

The Web area is 30 libraries plus the two App.Web framework producers: about 29.6k source lines (XML docs included) and 844 test methods. Test counts are in parentheses.

| Group | Projects | State |
|---|---|---|
| Runtime | `Web` (3), `Web.Hosting` (96), `Web.Hosting.Resources` (12), `Web.Hosting.Health` (5), `Web.Testing` (19), `Web.ApplicationModel` (9) | real |
| Routing and API | `Web.Routing` (176), `Web.Api` (13; placeholder `Map*` overloads the generator replaces), `SourceGeneration.Web` (6), `Web.Serialization` (42), `Web.ErrorHandling` (37), `Web.ProblemDetails` (11), `Web.Query` (28) | real |
| Middleware | `Web.StaticFiles` (52), `Web.Compression` (22), `Web.Caching` (29), `Web.RateLimiting` (20), `Web.RequestTimeouts` (30), `Web.Diagnostics` (38), `Web.ForwardedHeaders` (39), `Web.HostFiltering` (14), `Web.HttpsPolicy` (29), `Web.Sessions` (17), `Web.Health` (37) | real |
| Authentication | `Web.Authentication` (35), `.Bearer` (16), `.Cookie` (13) | real; Cookie and JWT Bearer handlers only |
| Placeholders | `Web.Cors` (0) and `Web.Authorization` (0) are empty assemblies that still ship in App.Web. `Web.CookiePolicy` (0): `UseCookiePolicy` does nothing, and it is also released as its own package. `Web.Forms` (2) is a 45-line `UseForms()` | stub |

What works end to end:
- **Transport:** HTTP/1.1, HTTP/2 and HTTP/3 over TCP, TLS and QUIC.
- **Routing:** templates, 17 constraints, precedence, 405, route groups, named routes with link generation, host matching.
- **Handler binding:** source-generated typed binding from route, query, header, JSON body and form scalars, with 400/415 problem responses.
- **Responses and errors:** serializer registry with content negotiation, exception boundary and status-code pages, RFC 9457 problem details.
- **Middleware:**
  - static files with ETag, single ranges and precompressed files;
  - Brotli/gzip compression and request decompression;
  - output cache;
  - rate limiting (four BCL algorithms, partitioned, per endpoint);
  - request timeouts;
  - HTTP and W3C access logging;
  - forwarded headers, host filtering, HTTPS redirection with HSTS;
  - sessions (in-memory store);
  - health endpoints.
- **Authentication:** Cookie and JWT Bearer handlers.
- **Testing and orchestration:** in-memory and `FromProgram` test factories, and the orchestration control plane.

### 7.2 Defects in shipped code

| # | Defect | Where | Kind |
|---|---|---|---|
| D1 | `UseStaticFiles()` with no arguments serves the process working directory. `IWebApplicationContext.ContentRootPath` is never set, and `.json` is a served content type, so `appsettings*.json` can be downloaded. The options and middleware are also rebuilt on every request. | `Web.StaticFiles/src/Extensions/WebApplicationStaticFilesExtensions.cs:24-53`; `Web.Hosting/src/WebApplicationBuilder.cs:108` | security |
| D2 | JWT Bearer skips issuer and audience validation when `ValidIssuers`/`ValidAudiences` are empty, and accepts any `alg` when the algorithm list is empty. | `Web.Authentication.Bearer/src/JwtBearerOptions.cs:24-36`; `Internal/JwtBearerHandler.cs:187-204` | security |
| D3 | A plain (orchestration-disabled) app gets no listener unless it calls `Server.UseServer` or `UseConfiguration`, because the default server is silently inactive. As a result, both default templates (`cohesion-web`, `cohesion-spa`) listen on nothing, and the template tests only build them. `appsettings.json` is also never copied to the output folder, which is where the host reads it from. | `WebApplicationBuilder.cs:110-120`; `WebApplicationServerBuilder.cs:35-41`; `tooling/templates/.../TemplateTests.cs:408-409` | DX, P0 |
| D4 | The server handles one exchange at a time per connection (`await pipeline`, then `await SendAsync`, then the next). HTTP/2 and HTTP/3 multiplexing therefore gives no concurrency, and one faulting exchange aborts every stream on the connection. | `Web.Hosting/src/Internal/WebApplicationServer.cs:305-345` | correctness |
| D5 | HTTP/2 buffered responses, the Web default, send DATA frames without acquiring send-window credit, which RFC 9113 §6.9 forbids. They also never debit the window, so the client's WINDOW_UPDATEs overflow it after about 2 GiB on one connection, and the server then raises FLOW_CONTROL_ERROR against a compliant client. HEAD response bodies are not suppressed on HTTP/2 or HTTP/3. | `Http.Connections/src/Internal/Http2/Http2ConnectionContext.cs:1691-1709` vs `:972-990` | conformance |
| D6 | HTTP/3 reads each request stream to FIN into memory inside the accept loop. That means no body cap, requests on the same connection wait behind each other, and request bodies and tunnels can't be streamed. #365 (incremental HTTP/3 request processing) was closed as completed on 2026-06-02, but this path still buffers. | `Internal/Http3/Http3ConnectionContext.cs:207`, `:1151-1157` | DoS |
| D7 | `Web.ForwardedHeaders` publishes the effective scheme, host and client IP, but only RateLimiting reads them. HTTPS redirection and HSTS, session `Secure` cookies, the compression BREACH guard, host filtering and access logs all read the raw connection values. Behind a TLS-terminating proxy that means redirect loops and no HSTS header. | `HttpsRedirectionMiddleware.cs:55`, `HstsMiddleware.cs:50`, `WebSessionFeature.cs:215`, `ResponseCompressionMiddleware.cs:53`, `HttpLoggingMiddleware.cs:336` | correctness |
| D8 | The router is built lazily on the first request. Route-table errors therefore surface on every request, and routes mapped after the first request are ignored. The route-template parser has 19 error paths that set an empty message, and cancellation tokens are dropped. | `Web.Routing/src/Internal/RouterFeature.cs:19`; `Patterns/RoutePatternParser.cs`; `RouterRouteHandler.cs` | correctness |
| D9 | Each TLS endpoint advertises exactly one ALPN protocol. One HTTPS port can't serve both HTTP/1.1 and HTTP/2, and the ambient orchestration endpoint is HTTP/1.1 only. | `Web.Hosting/src/Extensions/WebHostingExtensions.cs:147,188` | capability |
| D10 | #937: a malformed `:path` kills the HTTP/2 connection without sending GOAWAY, and on HTTP/3 it aborts the whole QUIC connection. | #937 | robustness |
| D11 | Unfinished packages reach consumers: the three placeholders in §7.1 (`Web.Cors`, `Web.Authorization`, `Web.CookiePolicy`). `Http.ServerSentEvents` is an App.Web member but is excluded from CI, so its tests never run; the same is true of `Http.DigestFields` and `Http.InterimResponses`. | `Web.Runtime/Directory.Build.props`; `$script:CohesionCiMatrixExclusion` in `CohesionPackaging.psm1` | packaging |
| D12 | Key-material defaults don't survive real deployments. The antiforgery protector defaults to a per-process random key, so tokens die on restart and don't work across instances. The DataProtection key ring defaults to `AppContext.BaseDirectory/DataProtection-Keys`, which fails in read-only and multi-instance containers. | `Http.Antiforgery/src/HttpAntiforgeryOptions.cs:52`; `Web.Authentication/src/AuthenticationBuilder.cs:69-73` | ops |

**After Stage 5 (2026-09-30):**
- Fixed: D1–D8, D10, and D11's CI half.
- Still open:
  - D9 → Stage 9, #1063.
  - D12 → Stage 10, #806–#808.
  - D11's placeholder half: the empty `Web.Cors` and `Web.Authorization` and the no-op `Web.CookiePolicy` still ship in App.Web until Stage 7 fills them (#3, #155, #156).

### 7.3 Missing capabilities

| Capability | Status | Tracking |
|---|---|---|
| Endpoint-aware pipeline (middleware between route match and handler) | Absent. `UseRouting` matches and dispatches in one terminal step, and RateLimiting, RequestTimeouts, Caching and Diagnostics each work around that differently. Typed `Map*` endpoints can't carry metadata. A CORS preflight (`OPTIONS`) resolves to 405 with no endpoint. | #1054, #1055; gates CORS, authorization, antiforgery and OpenAPI |
| CORS | Absent (empty project) | #3, #109–#111 |
| Authorization (policies, `RequireAuthorization`, `UseAuthorization`) | Absent (empty project) | #155 |
| Cookie-policy enforcement | No-op | #156 |
| Antiforgery in the pipeline | `Http.Antiforgery` ships but nothing wires it in; there is no DataProtection adapter | #1057 |
| OpenAPI for Web endpoints | Absent. OpenApi.Attributes, Generation, Integration and Versioning are unreleased and excluded from CI, and the release rule blocks an adapter that depends on them | #152, #1062 |
| Handler return values (`Task<T>`); generator diagnostics | Only `void`/`Task`/`ValueTask`. Unsupported handler shapes compile and then throw at runtime | #1059 |
| Validation problem responses | Absent (descoped from #796) | #1060 |
| File binding (`IHttpFormFile`); file and stream results | Absent | #1061 |
| Pipeline branching (`Map(path)`, `MapWhen`, `UseWhen`, `Run`); fallback routes (`MapFallbackToFile`) | Absent | #1056 |
| Server telemetry (`ActivitySource`, `Meter`, `traceparent`, request ID) | Absent in both Web and Http | #1064 |
| Hosting diagnostics; lame-duck drain | Absent | #147; #146 |
| mTLS (client certificates visible to handlers); multi-protocol ALPN endpoints; config for HTTP/3, limits and the connection cap | Absent | #1065; #1063 |
| HTTP/2 and HTTP/3 request-body cap (413); HTTP/2 timeouts; trailers | Body cap delivered in Stage 5 on both protocols (#1048, #1066). HTTP/3 now surfaces request trailers. HTTP/2 and HTTP/3 timeouts and data rates are still absent. | #1085; HTTP/2 request trailers deferred along with gRPC |
| Security headers (CSP, nosniff, Referrer-Policy, frame-ancestors) | Absent | #1058 |
| A representative Web app AOT-published in CI | Delivered in Stage 5: `Web.AotGuard` is published NativeAOT and smoke-tested by the `resource-web.yml` `aot-guard` job | #1052 |
| OIDC handler; JWT Bearer authority/JWKS discovery | Absent | blocked on IdentityModel #829/#830 |
| WebSockets | Absent. The HTTP/1.1 Upgrade and extended CONNECT bootstrap exist | #765 (needs an ADR) |
| URL rewrite | Absent | #782 (needs the request-mutation seam decision) |

### 7.4 Owner decisions

Decisions 1–4 were adopted with the lineup on 2026-09-30: the owner approved the suggested stages, which rest on these recommendations. Decision 5 is still open.

1. **Which claim model authorization runs on.**
   - Web: authenticates onto BCL `ClaimsPrincipal` by a recorded decision (`Web.Authentication/docs/DESIGN.md:157-167`).
   - IdentityModel: its DESIGN makes canonical claims the authorization input and calls an authorization model a non-goal (`IdentityModel/docs/DESIGN.md:1004-1009, 1030-1032`). #828 (an authorization model over canonical claims) is still open.
   - Recommendation: `Web.Authorization` evaluates over `ClaimsPrincipal`, and #828 later contributes an adapter instead of being a prerequisite.
2. **How to split routing.** Recommendation: `UseRouting()` becomes non-terminal (match, publish the endpoint, call `next`), and the pipeline's terminal runs the matched endpoint. This is a breaking change: middleware registered after `UseRouting()` would start running for matched requests.
3. **Forwarded headers: read the effective values, or rewrite the request.** Recommendation: keep the documented model where middleware read the effective values, and make every consumer read them (D7).
4. **Security-headers middleware in v1?** Recommendation: yes; it is small, P3.
5. **The Web v1 date.** `DELIVERY_ROADMAP.md` ends L3.1 on 2026-10-15, and Stages 5–7 alone are about 20 items. Either move the date or cut v1 at the end of Stage 7.
6. **Standing gates, open since July:** the WebSockets ADR (#765) and the request-mutation seam for rewrite (#782).

### 7.5 Lineup

Filed on 2026-09-30 as #1045–#1066 and moved into §4 as Stages 5–10, which is now the authority for order and blockers. The plan items map to issues as follows:

| Plan item | Issue | Plan item | Issue | Plan item | Issue |
|---|---|---|---|---|---|
| 5.1 | #1045 | 5.7 | #937 | 7.3 | #156 |
| 5.2 | #1046 | 5.8 | #1050 | 7.4 | #1057 |
| 5.3 | #1047 | 5.9 | #1051 | 7.5 | #1058 |
| 5.4 | #1048 | 5.10 | #1052 | 8.1–8.3 | #1059–#1061 |
| 5.5 | #1066 | 5.11 | #1053 | 8.4, 8.5 | #1062, #152 |
| 5.6 | #1049 | 5.12 | #699 | 9.1–9.5 | #1063, #147, #146, #1064, #1065 |
| 6.1–6.3 | #1054–#1056 | 7.1, 7.2 | #3 (+#109–#111), #155 | | |

**Docs drift** to fix alongside the items that touch these files:
- The area README lists `Web.Cors` as a working library and undercounts `Web.Hosting.Resources` consumers.
- Routing README and DESIGN still name functions, controllers and results.
- These notes are stale: ProblemDetails "future `OnError`", Query "future server output cache", Hosting "31f follow-up", Diagnostics "until #778".
- Authentication docs name `IHttpAuthenticationFeature`; the code type is `IAuthenticationFeature`.
- The Http.Connections DESIGN cites a `DESIGN_SUGGESTION.md` that doesn't exist.
- `DEVELOPER_EXPERIENCE_DESIGN.md:720` cites the old `frameworks/` path.
- The OpenApi README marks five unreleased packages "Implemented".
