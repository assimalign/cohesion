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
| **11 — Close the remote-triggerable holes** *(Phase 2 follow-ups, opened 2026-10-09)* | Every advisory candidate in the follow-up backlog, plus defects any client can turn into a 5xx or a log flood. | Stage 10 approved |
| **12 — Protocol conformance** | Response framing, HTTP/3 stream lifecycle, request-target and cookie rules, telemetry conformance. | Stage 11 reviewed |
| **13 — Web correctness** | Host start, the response-starting hook, repeated query and form values, validation error keys, trace ids. | Stage 12 reviewed |
| **14 — Performance, operations and DX** | Allocation and thread-hop costs, transport rejection telemetry, certificate authentication, template and docs gaps. | Stage 13 reviewed |

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

**Status:** delivered and approved 2026-09-30; the Phase 2 branch was published at `b11b6927`. Commits and follow-ups are in §5.

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

**Status:** delivered 2026-09-30. The owner published the branch, moved on to Stage 7 and is reviewing Phase 2 as [PR #1094](https://github.com/assimalign/cohesion/pull/1094). Commits, behavior changes and follow-ups are in §5.

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| #1054 | F | Split route matching from endpoint dispatch; preflight-aware matching | — |
| #1055 | F | Return an endpoint builder from `Map*` so endpoints and groups carry metadata | #1054 |
| #1056 | D/F | Pipeline branching (`Map`, `MapWhen`, `UseWhen`, `Run`) and fallback routes | #1054 |

### Stage 7 — Security and browser interop

**Status:** delivered 2026-10-01 on the Phase 2 branch and in owner review; the review's two decisions are applied. Commits, behavior changes and follow-ups are in §5.

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| #3 (+#109, #110, #111) | E | CORS policy engine and endpoint integration; one session closes all four | #1054, #1055 |
| #155 | E | Authorization: requirements, policies, `UseAuthorization`, per-endpoint schemes | #1054, #1055 |
| #156 | E | Cookie-policy enforcement, including a secure default for Cookie auth | — |
| #1057 | E | Enforce antiforgery in the Web pipeline | #1054, #1055 |
| #1058 | E | Security-headers middleware | #1055 (soft) |

### Stage 8 — API surface

**Status:** delivered 2026-10-02 on the Phase 2 branch, together with the defects found along the way (#1169, #1172–#1176, #1180, #1187, #1205, #1206), and approved 2026-10-06 with every recommendation adopted (§7.4, decisions 8–13). Commits, behavior changes and follow-ups are in §5.

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| #1059 | F | Serialize handler return values; compile-time diagnostics for unsupported handlers | #1055 |
| #1060 | F | Validate bound parameters; validation problem responses | #1059 |
| #1061 | F | File binding; file and stream response helpers | #1060; #1057 (soft) |
| #1062 | C | Onboard the remaining OpenApi packages to CI and the release | — |
| #152 | F | Web OpenAPI adapter (`IOpenApiEndpointSource`), document endpoint, AOT test | #1055, #1059, #1062 |

### Stage 9 — Server and operations

**Status:** delivered 2026-10-06 on the Phase 2 branch. It was approved 2026-10-07 with both recommendations adopted (§7.4, decisions 14–15). Defects found along the way were fixed in the same stage:
- the ObjectValidation defects #1291 and #1292;
- two denial-of-service holes in the released 10.0.0-preview.1, #1304 (TLS handshakes) and #1308 (TCP resets before the accept);
- #1309 and #1310.

Commits, behavior changes and follow-ups are in §5.

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| #1063 | D/A | HTTP/1.1 and HTTP/2 on one TLS endpoint via ALPN; complete server config binding (D9) | #1049 (soft) |
| #147 | D | Hosting diagnostics: startup failures, connection faults, drain timeouts | — |
| #146 | D | Lame-duck drain | #1049 |
| #1064 | D/A | Server telemetry (`ActivitySource`, `Meter`, trace context); settles #1039 | — (`http.route` is exact only after #1054) |
| #1065 | A/C | TLS client certificates and connection TLS metadata (mTLS) | — |
| #1221 | F | ObjectValidation evaluates rules and members in declaration order (§7.4, decision 11) | — |

### Stage 10 — Gated

**Status:** delivered 2026-10-07 on the Phase 2 branch and approved in the owner's review on 2026-10-09 (§7.4, decisions 19–20). The review follow-ups (#1367–#1369) were delivered and approved the same day (decision 21). Three ADRs cleared three gates (§7.4, decisions 16–18): WebSockets (#765), Web.Rewrite (#782) and trailers (#1314, #1315) are built. Defects found along the way were fixed in the same stage, among them an HTTP/1.1 request-smuggling desync in 10.0.0-preview.1 (#1333). #829 and #830 stay with the IdentityModel program, and #806–#808 stay post-v1. Commits, behavior changes and follow-ups are in §5.

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| #765 | A/E | WebSockets (RFC 6455) on HTTP/1.1, HTTP/2 and HTTP/3 | cleared by decision 16 |
| #782 | E | `Web.Rewrite` | cleared by decision 17 |
| #829, #830 | C | OIDC discovery/JWKS runtime and keyed token crypto in IdentityModel; after those, an OIDC handler and JWT Bearer `Authority` discovery | the IdentityModel program |
| #1314, #1315 | A | HTTP/2 request trailers decoded (a defect); response trailers on HTTP/2 and HTTP/3 | cleared by decision 18 |
| #806–#808 | C | Distributed data-protection key storage; distributed session and output-cache stores get filed alongside | post-v1 |

### Stages 11–14 — the Phase 2 follow-ups (lined up 2026-10-09)

Stages 5–10 filed 75 follow-ups that the branch did not close. A reconciliation on 2026-10-09 checked each one against the code at `305c5232`, and two skeptics tried to refute every closure:
- #1076 and #1081 are already fixed on the branch.
- #1071 duplicates #1339 and was closed.
- #1074 is only half fixed.

The planning critic found three defects nobody had filed: #1375, #1376 and the request-line half of #1341. The lineup, the scope changes and the decisions are in §7.4, decisions 22–31.

Project #13 has no Wave option past W06, so items in these stages keep W06. This plan is the authority for their order.

### Stage 11 — Close the remote-triggerable holes

**Status:** delivered 2026-10-10 on the Phase 2 branch and in owner review (§5). Sessions in the same row edit the same files, so they run serially as one session with one commit per issue. The rows run in parallel. Wave 2 starts once wave 1 is integrated.

| Session | Issues (in order) | Lane | Blocked by | Why |
|---|---|---|---|---|
| 1 | #1072 → #1075 → #1074 | A | — | HTTP/2 stream slots stay held while a reset exchange runs (rapid-reset and MadeYouReset class). A cancelled send leaks a slot under that rule, and the WINDOW_UPDATE half of #1074 edits the same bookkeeping |
| 2 | #1082 | A | — | HTTP/3 field sections have no decoded-size limit: 32 KB of QPACK costs ~0.85 s of CPU and 4 GB (P001) |
| 3 | #1080 | C/A | — | QUIC aborts carry no code, so complete HTTP/3 responses fail behind unread uploads. Widened to the connection close code |
| 4 | #1339 → #1375 | A | — | HTTP/1.1 body limits answered 500. Chunk framing lines and trailers are unbounded on every listener (P001) |
| 5 | #1341 | A/B | — | NUL, bare CR and bare LF are kept in HTTP/1.1 field values (smuggling vector). A non-ASCII request line is routed as a different path. Introduces the core field rule |
| 6 | #1301 | B/E | — | HTTP methods are case-folded (ACL and WAF bypass behind a proxy), Web.Cors included |
| 7 | #1186 | B/E | — | An extensionless file named `html` is served as HTML (stored XSS) |
| 8 | #1155 | C | — | An unknown key id forces an unthrottled key-ring reload under a process-wide lock |
| 9 | #1077 | F | — | `RequireHost` matches the raw host, not the effective one, behind a trusted proxy |
| 10 | #1377 | F | — | The Email validation rule backtracks quadratically, and Web.Validation runs it on request bodies (P001) |
| 11 | #1312 | C | #1308 (merged here) | An accept that fails for want of descriptors or buffers stops the endpoint |
| 12 | #1210 | E | — | `UseForms` answers an oversized or malformed form with a 500 |
| 13 (wave 2) | #1085 → #1084 | A | sessions 1, 3 | HTTP/2 and HTTP/3 Slowloris: no timeouts or data rates. The HTTP/3 streamed send path #1085 relies on gets #1084's body-cap gate |
| 14 (wave 2) | #1183 → #1376 | A/B | sessions 2, 5 | CR, LF and NUL are not rejected in response fields (CWE-113) on any writer, the Http.ProtocolUpgrade 101 writer included. HTTP/2 and HTTP/3 never validate inbound fields |
| 15 (wave 2) | #1340 | D/E | session 4 | A malformed body is logged as a 500 application fault on HTTP/1.1, and the client got 400. The HTTP/2 and HTTP/3 half is #1378, in Stage 12 |

**Owner-directed Web restructure, between the waves (decisions 32–36).**
- #1382, the COHRES002 change, landed first (`f71ab0af`, `57e1f049`).
- Delivered 2026-10-10, between the waves, at `d1062742`. Each item was implemented, reviewed, checked by skeptics and fixed in sequence:
  - **#1379** (`321b4a19`, fixes `6f1fb4ad`, `2ee47d33`, `83a39e32`):
    - The Web root declares no `IHttpFeature` contract.
    - The endpoint feature, path base, `Map`/`MapWhen` and the pipeline terminal moved to Web.Routing, under the namespace `Assimalign.Cohesion.Web.Routing`. That is a source break: callers add a `using`.
    - Request id, response completion and drain moved to the new `Web.Server`, which keeps the `Assimalign.Cohesion.Web` namespace.
    - Web.Hosting references both. Web.Server and Web.Routing (with Http.Forwarded) joined the 17 area frameworks privately.
  - **#1380** (`dae986a0`, `d2d83fd9`, `10105cc8`, fixes `94f49cca`, `ad4b7fa7`, `45c19af2`):
    - The eight registration verbs, plus `AddJsonSerialization`, are `builder.Services.AddX(...)` component integrations. Three use builder templates (Authentication, ErrorHandling, ContentSerialization) and six use static factories.
    - Authentication's `defaultScheme` overload and `dataProtectionProvider` parameter became `auth.Options.DefaultScheme` and `auth.UseDataProtection(...)`.
    - ErrorHandling and ContentSerialization builders now build immutable snapshots.
    - Web.Hosting enforces decision 35.
  - **#1381** (`3dbb2395`, `fdb90ac5`, fixes `0b0761a0`, `d00d8b7b`, `1398131c`, `d1062742`):
    - A generic `HttpConnectionListenerOptions.ExchangeFeatureCapacity`, sized by Web.Hosting and rounded to the unsized growth chain.
    - Per plain GET on HTTP/1.1: −128 B with no application features, −544 B at 4, −1,296 B at 16.
- Wave 2's #1340 touches HTTP logging and the exception boundary, so it runs after #1380.

### Stage 12 — Protocol conformance

**Status:** lined up. Gate: Stage 11 reviewed.

| Issue | Lane | Blocked by | Note |
|---|---|---|---|
| #1073 | A | — | `Content-Length` on 204, 304 and empty HTTP/1.1 HEAD (HTTP/2 and HTTP/3 HEAD done) |
| #1083 → #1306 | A | #1080 | HTTP/3 control-stream FIN and push PRIORITY_UPDATE. Requests after GOAWAY are reset with H3_REQUEST_REJECTED |
| #1330 | C/A | #1080, #1084 | A QUIC stream cannot half-close, which breaks extended CONNECT tunnels over real QUIC |
| #1378 | A/D | #1080, #1340 | #1340's client-fault report on HTTP/2 and HTTP/3: each control's `ClientFaultStatusCode` from the latch it already has (the HTTP/2 stream's transport status, `Http3RequestBodyStream.RejectedStatusCode`), and a body signal for the Web interceptor's gate, since an HTTP/2 or HTTP/3 request need not declare its body. Until then a body those transports reject while it is read can still be logged at `Error` and run `OnException`. The HTTP/2 `408` was also recorded as a `500` server error; that was fixed when wave 2 was integrated |
| #1334 | A/B | #1073 | Classic CONNECT and asterisk-form targets are unvalidated on HTTP/2 and HTTP/3 |
| #1153 → #1154 | B | #1183 | Cookie `Path`/`Domain` grammar. One `Set-Cookie` line per value on a 101 |
| #1185, #1204 | B | — | Exact `If-Range` date match. RFC 9110 reason phrases |
| #1298 → #1299 | D | — | A redacted `url.query` on server spans. Traces continue from a future-version `traceparent` |
| #1324 | E | — | Redirect `Location` paths are percent-encoded in HttpsPolicy and StaticFiles |
| #1383 | A/B | — | A request can change or disable its minimum request-body data rate on every protocol (P002; HTTP/2 and HTTP/3 began enforcing the rate in #1085) |
| #1384 | A | — | A minimum response data rate so a client that withholds flow-control credit or stops reading cannot hold a response writer (P002, advisory triage) |
| #1385, #1387 | A | — | `100-continue` on HTTP/2 and HTTP/3; HPACK and QPACK decode values as Latin-1 like HTTP/1.1 |

### Stage 13 — Web correctness

**Status:** lined up. Gate: Stage 12 reviewed.
- #1079 → #1078: a failed host start, and late middleware throws.
- #1156: the response-starting hook.
- #1335 → #1211: repeated query and form values; a single read returns the first value (decision 31).
- #1208 → #1209: validation errors kept and keyed by JSON name and index.
- #1297 → #1325: HTTP logging trace ids, and body capture behind a request view.
- #1388: a streamed response is reported as unstarted when the transport refuses its head.

### Stage 14 — Performance, operations and DX

**Status:** lined up. Gate: Stage 13 reviewed.
- #1338 → #1300: no HTTP/3 thread hop; transport rejections reported through a Meter and an EventSource (decision 31).
- #1337: allocation-free feature lookup.
- #1311: the QUIC handshake timeout option.
- #1092: the `cohesion-spa` fallback route.
- #1303 → #1305: the missing OVERVIEW files; certificate authentication in a new `Web.Authentication.Certificate` (decision 31).
- #1389: resolve a single per-exchange response interceptor without intermediate lists.
- #1202 and #1217's Web.OpenApi follow-up, once the OpenApi program fixes its model.

**Other programs**, not scheduled here:
- **FileSystem:** #1181, #1182, #1212–#1216.
- **ObjectValidation:** #1207, #1222–#1224, #1294, #1295, and #1293 without its Email half.
- **OpenApi:** #1170, #1171, #1178, #1179, #1201, #1203, #1217's model change.
- **Other:** DependencyInjection #1177, Configuration #1220, Database #1219, release #1290, build #1302.

#1290 matters to this program: every pending advisory publishes with the next preview promotion, and that promotion needs #1290's batching.

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
- **Stage 5 delivered and approved (2026-09-30).** One commit per issue on the Phase 2 branch, published at `b11b6927`:
  - #1046 `cd47f0d0`, #1045 `656233d3`, #1047 `5da6d7dc`, #1053 `8cfffc11`, #1050 `23a2874c`
  - #1052 `e190ca7b` (DI call-site factory made AOT-clean) and `f4b509d5` (guard and CI job)
  - #1048 `10def15e`, #937 `fdd4f232` (HTTP/2) and `7667fb7e` (HTTP/3), #1051 `a4401268`, #1049 `db758492`
  - #1066 `f45b4e30`, plus `2a2e0afc` (Content-Digest verified lazily on HTTP/3)
  - #699 `0ebbdc1d`
  - `2ac5c565` regenerated `docs/DEPENDENCIES.md`; `70f2ad34` fixed the §7.5 docs drift.

  Verification: every touched suite passes, including Http.Connections 520, Http 1231, Web.Hosting 136 and Web.Testing 19. The Web NativeAOT guard publishes and passes 7/7 smoke checks. The `cohesion-web` and `cohesion-spa` template run tests pass against a locally packed SDK.

  CI on `b11b6927`: every workflow passed except Web. There, `Web.Hosting.Resources` timed out on Windows in the control-plane stop handshake (tracked in #1093), and fail-fast cancelled the remaining matrix jobs. The Linux NativeAOT guard job passed.

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
- **Stage 6 delivered (2026-09-30).** On the Phase 2 branch:
  - #1054 `523049c6` (match/dispatch split, preflight candidates, fail-closed endpoint middleware); consumers moved onto the published endpoint in `99732edf` (RateLimiting), `71e11b7e` (RequestTimeouts), `2dce79d5` (Caching) and `2811f201` (Diagnostics); ordering docs `0cf65e02`; last-wins requirement check `e7818dd2`
  - #1055 `08619d7d` (convention builders, group metadata composed at build, route-or-query binding) and `7b80e2ac` (`RequireRateLimiting`, `WithRequestTimeout`, `CacheOutput`, `WithHttpLogging` and their opt-outs)
  - #1056 `9ba64fc9`
  - `85d71c2e` extends the NativeAOT guard to groups, policy verbs, branching and the fallback

  Verification: 27 Web suites and the generator suite pass, 1,236 tests in all (Routing 344, Web.Hosting 136, StaticFiles 94). The Authorization, CookiePolicy and Cors suites are still empty placeholders. The guard publishes NativeAOT locally for win-arm64 with no trim or AOT diagnostics and passes 11/11 smoke checks.

  Behavior changes for the review:
  - `UseRouting()` publishes the endpoint and calls `next`; the pipeline terminal runs it and answers 405. Middleware after `UseRouting()` now runs for matched requests and for 405s (owner decision 2).
  - An endpoint whose rate limit or timeout no middleware applied (missing, or registered ahead of `UseRouting()`) fails at dispatch with `InvalidOperationException` instead of running unprotected.
  - Policy middleware goes after `UseRouting()`: rate limiting, request timeouts and output caching, and response compression in an application that caches. Endpoint rate-limit policies can now queue, and an endpoint timeout starts when `UseRequestTimeouts` runs.
  - `Map*` returns `IRouterRouteBuilder` instead of the pipeline builder, so `app.MapGet(...).MapGet(...)` chains no longer compile.
  - Group metadata composes at route-table build in any call order; parameter policies still freeze.
  - A typed endpoint in a group, or with a non-literal pattern, binds a scalar the pattern does not place from the route, then the query.
  - An omitted `{**catchAll}` segment now matches.
  - `Map(path)` publishes an effective path and path base and leaves `IHttpRequest.Path` alone; rewriting waits on #782. Branches hold middleware only, which the compiler enforces.
  - A CORS preflight publishes its candidate endpoint (`IsPreflight`) without running it; access logging records it with the configured fields.

  Scope-creep filed:
  - #1092: the `cohesion-spa` template should map `MapFallbackToFile`. Deferred because it needs the packed-SDK template run tests.
  - #1093 (P001): a pre-existing process crash. A pipe continuation runs with the wrong state on `Connections.Tcp`'s `SocketPipeScheduler`. It aborted `Web.Hosting.Resources` in CI on two branches, and the Web matrix's default `fail-fast` then cancelled every other Web job.
- **Between Stages 6 and 7 (2026-09-30 to 2026-10-01).** The owner published the branch and opened it for review as [PR #1094](https://github.com/assimalign/cohesion/pull/1094).
  - `d001aa9e` sets `fail-fast: false` on the Web build and HTTPS-sample matrices, so one failing leg no longer cancels the rest.
  - `0cdbe27f` merges `main` and re-applies the Stage 5 AOT checks onto #1044's restructured DI `CallSiteFactory`. `01822ceb` corrects two Web.Hosting design statements that Stage 5 made stale.
  - #1093 is fixed in `6ca7ae2a`. It had two root causes in `Connections.Tcp`. `SocketPipeAsyncArgs` could run a continuation before its state was published, which crashed the process ("An unexpected state object was encountered"). On Windows, a cancelled accept leaked the socket AcceptEx had already connected, so the client hung. Each cause has a regression test that failed before the fix.
- **Stage 7 delivered (2026-10-01), awaiting owner review.** Five agent sessions built it in parallel; it was integrated on the Phase 2 branch:
  - #155 `f72af3eb`: Web.Authorization, with policies over `ClaimsPrincipal`, `RequireAuthorization`/`AllowAnonymous`, `UseAuthorization` and per-endpoint schemes.
  - #1057 `d9d23980`: the new Web.Antiforgery, with `AddAntiforgery` (including a data-protection overload), `UseAntiforgery`, and a requirement the generator adds to `[FromForm]` endpoints.
  - #3 with #109–#111 `707ee50a`: Web.Cors, with a policy engine, preflights answered from the candidate endpoint, and `RequireCors`/`DisableCors`.
  - #156:
    - `51eab54b` makes the Cookie authentication ticket essential by default.
    - `af650441` makes Web.CookiePolicy enforce consent, attribute floors, cookie prefixes and the 400-day cap.
    - `6aa7dc7c` corrects the Http.Cookies docs.
  - #1058:
    - `08ef6913` adds the header names.
    - `78c7544a` adds the new Web.SecurityHeaders.
    - `8fc2e436` makes IdentityHub take its headers from it.
  - `6c36109e` makes the antiforgery cookie essential by default and lets sessions opt in, so a consent requirement no longer breaks form posts.
  - NativeAOT guard: `30054f1a` covers authorization, `15cd4e29` CORS, and `34d78dd6` antiforgery, cookie policy and security headers.
  - `5908d834` adds the area's middleware order (`docs/resources/Web/MIDDLEWARE_ORDER.md`) and fixes six packages whose docs each told the reader to register them first.

  Verification:
  - The 32 Web suites pass (1,758 tests).
  - So do nine outside suites (1,491 tests): Http (1,241), Http.Cookies, Http.Antiforgery, Http.Forwarded, Http.Sessions, IdentityHub.Hosting, the endpoint generator, App.Runtime's framework closure tests and Connections.Tcp.
  - Both App.Web producers pack.
  - The guard publishes NativeAOT for win-arm64 and passes 20/20 smoke checks.
  - `CodeFixes.WebTests` does not build, because the project resolves to `netstandard2.0`. That predates the program, and no workflow runs it.

  Behavior changes for the review:
  - An endpoint that requires authorization, CORS or antiforgery fails at dispatch when that middleware is missing or registered ahead of `UseRouting()`. That includes `DisableCors()`: a `UseCors` registered ahead of routing would apply the default policy to an endpoint that opted out.
  - Every authorization item on an endpoint applies, outer group first, except the ones declared before the most specific `AllowAnonymous`, which clears them (§7.4, decision 7). Unlike ASP.NET Core, a route that requires authorization inside an `AllowAnonymous` group stays protected. The fallback policy also covers requests that match no endpoint, so an anonymous caller is challenged on an unknown path.
  - The antiforgery cookie token is `Secure` whenever the request's effective scheme is HTTPS, as the session and authentication cookies already were.
  - `[FromForm]` typed endpoints require antiforgery. An application with them calls `AddAntiforgery()` and `UseAntiforgery()` after `UseRouting()`, or opts each such endpoint out with `DisableAntiforgery()`.
  - Http.Antiforgery joins App.Web. Without a data-protection provider its tokens still die on restart.
  - `UseCookiePolicy` now enforces its policy instead of passing every cookie through, and its namespace is now `Assimalign.Cohesion.Web.CookiePolicy`. The Cookie authentication ticket and the antiforgery cookie are essential by default; the session cookie is not. Under a consent requirement a session therefore starts only after consent, unless `HttpSessionOptions.CookieIsEssential` is set.
  - IdentityHub sends its strict CSP, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY` and `nosniff` on every response, not only the approval page.

  Review decisions applied (2026-10-01):
  - `e451032b`: the most specific `AllowAnonymous` wins (decision 7). Web.Authorization has 69 tests.
  - `d086cda1`: the antiforgery cookie token is `Secure` whenever the effective scheme is HTTPS, through the same floor Cookie authentication uses. `CookieSecure` still forces the flag on plaintext requests. Http.Antiforgery now references Http.Forwarded and has 31 tests.
  - The five Stage 7 agent worktrees and their branches were removed.

  Scope-creep filed:
  - #1153: cookie `Path` and `Domain` values are not validated.
  - #1154: a `101` upgrade response sends each `Set-Cookie` twice.
  - #1155: an unknown key id reloads the whole key ring, with no throttle.
  - #1156: a response-starting hook for the Web pipeline, which would remove the CORS trade-off.
- **Stage 8 delivered (2026-10-01 to 2026-10-02) and approved (2026-10-06); every recommendation in "Questions for the review" was adopted (§7.4, decisions 8–13).** Agent sessions built the items in their own worktrees. Each was reviewed, integrated on the Phase 2 branch, verified and pushed to PR #1094 item by item. The integrator's own commits are the guard coverage, the OpenAPI upload mapping, the #1180 thrower move and the doc corrections:
  - #1062 `4e0fb6da`, `509681a6`: the five remaining OpenApi packages are built, tested and released, and the OpenApi source generator ships inside `OpenApi.Attributes`. Shipping it made a generated-code collision reachable; #1169 `e03cd9f9` composes generated metadata across assemblies through `IOpenApiMetadataProvider`.
  - #1059 `079e3ef0`, `b62485e8`: typed handlers return `T`, `Task<T>` or `ValueTask<T>`, written with content negotiation (`string` as `text/plain`, `null` as 204, 406 when nothing is acceptable, a 500 fault when no contract covers `T`), and unsupported handler shapes fail the build with `COHWEB0001`–`COHWEB0007`. Every typed endpoint carries `EndpointParameterMetadata` and `EndpointResponseMetadata`.
  - #1172–#1176 `d0952645`, `439e5197`, `c05248a0`, `1a563bb4`, `dfc81374`: generator defects found during #1059 (escaped binding names, a body with no contract is a fault rather than a 415, generic receivers, conditional-access and static-form calls, injected `IHttpRequest`/`IHttpResponse`).
  - #1060 `650b2b83`: the new Web.Validation runs a registered validator on a bound body model before the handler and answers 400 problem+json keyed by member path. ObjectValidation composes nested member paths. #1206 `227f49e6`: under default options a member's rule chain stops only on its own failure, so every failing member is reported; it used to stop every later member once any member had failed.
  - #1061: `60747509` adds `SendFileAsync`/`WriteStreamAsync` with conditional and single-range support on the StaticFiles engine; `6125b9c2` binds uploaded files in typed handlers and answers an over-limit form 413 and a malformed one 400. #1187 `cb2e5903` opens served files for shared reading.
  - #152 `821168fb`, `b8a12a91`, `2fdf64ae`, `3fd06146`: the new Web.OpenApi, NuGet-only, generates OpenAPI 3.0/3.1/3.2 documents from endpoint metadata and the app's source-generated JSON contracts. #1205 `bb476a67` takes each operation's security from the endpoint's effective authorization policy through a read-only Web.Authorization seam; `b22c4fb1` describes uploads as multipart binary parts and lists a form's 413; `c029accd` pins the missing-serializer error.
  - #1180 (P001, security) `efda629b`, `e76b4bd4`: `PhysicalFileSystem` and the in-memory provider refuse any path outside their root with `PathOutsideRoot` before touching storage. `../` escapes and sibling-prefix roots were open; StaticFiles had been protected only by its own request-path gate.
  - NativeAOT guard: `3feae793` return values, `2ba14611` the OpenAPI document, `c9ad011c` validation and uploads, 28 smoke checks.
  - Housekeeping: `c571c739` and `b8d5a5db` correct statements Stage 7 left stale, `f949d019` an Http cref, `9c774357` lists Web.HttpsPolicy in the resources solution, and `934cdbd2` documents the Web root's application and builder contracts.
  - Docs site ([cohesion-docs#1](https://github.com/assimalign/cohesion-docs/pull/1)): `09b697a` through `6da9f2b` add the Web.Validation and Web.OpenApi pages and an OpenAPI guide, sync the pages Stage 8 changed, and list DependencyInjection on the 16 hosting pages that omitted it. `405b072f` corrects five cohesion statements the sync found stale.

  Verification, run as each change was integrated and again on the suites the later fixes touched (final tip `227f49e6`):
  - Web: the endpoint generator (67), Web.Api (71), Web.Validation (35), Web.Antiforgery (63), Web.Forms (2), Web.Serialization (48), Web.Routing (344), Web.OpenApi (50), Web.Authorization (87), Web.StaticFiles (175) and Web.Hosting (136).
  - Outside the area: ObjectValidation (230), Http.Forms (35), IdentityHub.Hosting (27), App.Runtime (3), Core (232), FileSystem (41), its Physical (209), InMemory (223), Aggregate (96), Globbing (39) and IsolatedStorage (102) providers, Configuration.FileSystem (4), Configuration.Json (3), Database.Storage (96) and Database.Hosting (50).
  - Both App.Web producers pack, and the release-inventory and dependency-graph checks pass.
  - The guard publishes NativeAOT for win-arm64 at `227f49e6` with no trim or AOT warnings and passes 28/28 smoke checks.

  Behavior changes for the review:
  - A typed handler's return value is written instead of throwing at mapping time, and a shape the generator cannot bind fails the build.
  - A body type no registered serializer covers, or a body read with no serialization registered, is a 500 through the exception boundary instead of a 415 (#1173).
  - With Web.Validation referenced, an invalid body model is answered 400 before the handler runs. App.Web ships Web.Validation and ObjectValidation.
  - An ObjectValidation validator with default options reports every failing member, one rule's errors each, instead of the first failing member only.
  - Uploads bind; an over-limit or malformed form on a typed endpoint is 413 or 400 instead of 500, and an over-limit form read by `UseAntiforgery` is 413 instead of a 400 token rejection. `HttpFormFileCollection` keeps every file of a repeated field.
  - `PhysicalFileSystem` throws `PathOutsideRoot` for a path outside its root, `Exists` included; `RootDirectory.Parent` is `null`; `FileSystemPath.Merge` throws when `..` climbs past the root and matches prefixes on segment boundaries.
  - The OpenAPI document lists security for fallback-protected endpoints and named policies' schemes, and an unregistered policy name fails the document as it fails every request to that endpoint.

  Questions for the review:
  - #1180 was a path-traversal hole in the released 10.0.0-preview.1. Should a security advisory go out for it?
  - Should validation stay its own package (Web.Validation)? The owner descoped validation from Web.Api on 2026-07-20; the separate package was the integrator's placement.
  - The OpenAPI document fails on an unregistered authorization policy name, as every request to that endpoint does. The alternative is to describe the endpoint and degrade.
  - #1221 (P002): putting ObjectValidation's evaluation order right changes which message every chained rule reports.
  - `IOpenApiMetadataProvider` (#1169) and `IOpenApiEndpointSource` are two seams for contributing operations. Keep both, or fold one into the other?
  - Two program-level items Stage 8 made visible:
    - Every Cohesion assembly carries `RequiresPreviewFeatures`, so a consumer on the plain .NET SDK gets CA2252 until it enables preview features.
    - The release is about 389 packages, above nuget.org's 350-per-hour push ceiling, which `VERSIONING_RELEASE_POLICY.md` says must not be exceeded by one promotion.

  Scope-creep filed:
  - #1206–#1209: ObjectValidation reporting, shared state, exception errors and error keys (#1206 is fixed in this stage).
  - #1221 (P002): ObjectValidation evaluates chained rules and members in reverse declaration order, against its FIFO contract. #1222–#1224: README samples, `Stop` with a custom context, `ValidateAsync` cancellation.
  - #1210, #1211: `UseForms()` failures and repeated multipart fields.
  - #1212–#1216, #1218: root deletion, aggregate mounts, file watching, an IsolatedStorage flake, `Parse` of `/..`, and isolated-storage containment.
  - #1217: `security: []` on an operation. #1219: database names escape the data root. #1220: Configuration's trim and AOT warnings.
- **Stage 9 delivered (2026-10-06) and approved (2026-10-07); both recommendations in "Questions for the review" were adopted (§7.4, decisions 14–15).** Agent sessions built it in parallel worktrees: one per lane, and one more for #1304. Each item was reviewed, integrated on the Phase 2 branch, verified and pushed to PR #1094 as it landed:
  - **Validation order.** #1221 `b6354464` (decision 11): ObjectValidation evaluates chained rules and members in declaration order. The validation context also listed errors on a stack, so fixing only the queues would have left nested errors reversed.
  - **Validation defects.** #1221's session found both, and the integrator fixed them:
    - #1291 `bec8dbd0` removes an unsynchronized static object pool that could hand a concurrent validation `null`, and reports timings in `TimeSpan` ticks.
    - #1292 `ff198072` makes a nested profile's rule, or a custom rule, that throws fault the validation instead of letting the value pass.
  - **Telemetry.**
    - #1064 `fb98d10f`: a server span and the HTTP server metrics from the `ActivitySource` and `Meter` named `Assimalign.Cohesion.Web.Hosting`, W3C trace context, `IWebRequestIdFeature`, and `http.route` through `IWebEndpointFeature.RouteTemplate`.
    - #1039 `82b4b65b` deletes the empty `HttpTransportEventSource`.
  - **TLS.**
    - #1063 `d8e7b9a6`: HTTP/1.1 and HTTP/2 on one TLS endpoint, chosen per connection from ALPN, with configuration for HTTP/3, certificate files, the connection cap and the HTTP/2 limits.
    - #1065 `66b32828`: client certificates, and the connection's TLS details through `IHttpTlsConnectionFeature` and `ITlsConnectionInfo`.
  - **Shutdown and diagnostics.**
    - #146 `16ab03e4`: lame-duck drain. Every connection begins a graceful close (`Connection: close`, or GOAWAY on HTTP/2 and HTTP/3), and in-flight exchanges are cancelled only when the budget expires.
    - #147 `f03ddafe`: bind failures, accept-loop faults, connection faults and cut-short drains are logged through `builder.Logging`.
  - **#1304 (P001, security) `17e2f652`, found by the TLS lane.** A failed or stalled TLS handshake stopped an HTTPS listener: handshakes ran one at a time inside `AcceptAsync`, and the accept loop treated their failure as fatal. Now:
    - A TLS-layered listener runs each handshake on its own task, at most `TlsServerOptions.MaxConcurrentHandshakes` (512) at once.
    - A failed handshake closes only its connection and is reported by the new internal `Assimalign.Cohesion.Connections` event source.
    - The QUIC driver drops a failed inbound handshake instead of ending its accept.
    - `IConnectionListener.AcceptAsync` now documents the rule every driver follows: a listener contains each connection's failure.
  - **#1308 (P001, security) `cdb81ed6`, found while reviewing #1304.** A client that connected and then reset before the accept made `TcpConnectionListener.AcceptAsync` throw on Windows, and the HTTP accept loop stopped the endpoint. One reset was enough, on plain and TLS endpoints alike. The listener now skips such a connection and reports `AcceptSkipped`.
  - **Two more gaps behind #1304, fixed by the integrator.**
    - #1309 `20a924b8`: a layered factory leaked the dialed connection when its TLS client handshake failed.
    - #1310 `28f1c679`: the Web server's accept loop read a cancellation it had not requested as its own drain, so a transport fault surfacing as one ended the endpoint without the #147 log.
  - **Advisories.** Both #1304 and #1308 are in the released 10.0.0-preview.1. Following decision 8's practice, they are drafted privately: GHSA-r9cf-3952-rg7f (#1304) and GHSA-r66x-xgrx-gh8m (#1308).
  - **Housekeeping.**
    - `38967a3a` documents the Web root's `Use` verb.
    - `31f06877` corrects stale Http.Connections and Http contract docs.
    - `a13f8046` corrects the test factory's stop docs and records why fault logs keep the peer address.
  - **Docs site** ([cohesion-docs#1](https://github.com/assimalign/cohesion-docs/pull/1)):
    - `6418907`, `dca7fab`, `89c5efb` and `53ce31e` sync the pages Stage 9 changed and add an observability guide.
    - `bfbcf28` covers where handshakes run, resets before accept, and the fault-log rules.

  Verification (final code tip `28f1c679`):
  - **Full run at `cdb81ed6`, 33 suites:**
    - Connections family: Connections (48), Tcp (46), Security (41), Quic (27), NamedPipes (30), Udp (19).
    - Http: Http (1,244), Http.Connections (547).
    - Web: Web (20), Web.Hosting (210), Web.Hosting.Resources (19), Web.Routing (352), Web.Testing (19), Web.Validation (37).
    - Hosting.Telemetry (16) and ObjectValidation (250).
    - The 17 area Hosting suites: ApiManager (11), ConfigurationStore (18), Database (50), EmailHub (11), EventHub (11), IdentityHub (27), IoTHub (11), LoadBalancer (11), LogSpace (39), MediaHub (11), MessageHub (11), NatGateway (11), NotificationHub (11), Rezolvr (12), Scheduler (20), SecretStore (23), VpnGateway (11).
  - **After #1309 and #1310, at `28f1c679`:** Connections (49), Security (41), Http.Connections (547), Web.Hosting (211), Web.Hosting.Resources (19), Web.Testing (19).
  - **Each regression test fails without its fix.**
    - #1308: the Tcp test failed on its first run, and the Web.Hosting end-to-end tests failed in 9 of 10 runs.
    - #1309 and #1310: each test fails with its fix reverted.
  - **Checks.** The guard passes 29/29 smoke checks under JIT, and the release-inventory and dependency-graph checks pass.
  - **NativeAOT.** The guard publishes for win-arm64 at `28f1c679` with no trim or AOT warnings, and the native binary passes 29/29 smoke checks.

  Behavior changes for the review:
  - **ObjectValidation reports in declaration order.**
    - A chain's message is its first failing rule's.
    - `Stop` reports the first failing member.
    - The `errors` map follows declaration order.
    - A rule that throws, in a nested profile or as a custom rule, faults the validation.
  - **TLS endpoints.**
    - `Protocol: Https` endpoints and the ambient `https` endpoint serve HTTP/2 as well as HTTP/1.1.
    - Every TLS exchange carries `IHttpTlsConnectionFeature`. Superseded by decision 20 (#1367): the feature lives in Http.Tls, and `context.TlsConnection` builds it on first read from the transport's `ITlsConnectionInfo` facet.
  - **Graceful close.**
    - `IHttpConnectionContext` gains `BeginGracefulClose()`, a source break for an outside implementer.
    - `StopAsync` no longer throws when its budget runs out.
    - `WebApplicationTestFactory.DisposeAsync` cancels in-flight requests.
  - **Request id.** Every exchange carries `IWebRequestIdFeature`.
  - **Layered listeners.**
    - A layered listener (`Use(layer)`, `UseTls`) returns connections in the order their upgrades complete and holds at most 512 at once. A new overload `Use(layer, maxConcurrentUpgrades)` and `TlsServerOptions.MaxConcurrentHandshakes` set the bound.
    - A failed upgrade no longer comes out of `AcceptAsync`.
  - **Accept failures.**
    - A TCP accept that fails with `ConnectionReset` or `ConnectionAborted` is skipped.
    - A layered factory disposes the connection it dialed when the upgrade fails.

  Questions for the review:
  - **Advisories.** #1304 and #1308 are denial-of-service holes in the released 10.0.0-preview.1. Publish GHSA-r9cf-3952-rg7f and GHSA-r66x-xgrx-gh8m with the first preview that ships the fixes, as decision 8 does for GHSA-5jrr-79fc-fc98?
  - **Source break.** `IHttpConnectionContext.BeginGracefulClose()` is a required member, not a default one. Accept the source break during the previews?

  Scope-creep filed:
  - **ObjectValidation (#1293–#1296).** The built-in rules' catches, a test flake, an indexer bound, and `AddProfile(IValidationProfile)` registering no rules.
  - **Telemetry and transport (#1297–#1303):**
    - logging the server span's ids;
    - a redacted `url.query`;
    - future-version `traceparent`;
    - transport rejection diagnostics;
    - case-sensitive methods;
    - `Shared/` folders dropped from resource builds;
    - missing `OVERVIEW.md` files.
  - **TLS (#1305–#1307).** Client-certificate authentication, HTTP/3 requests after GOAWAY, and an undisposed HTTP/2 token source.
  - **Connections.**
    - #1309 and #1310 are fixed in this stage.
    - #1311: a QUIC handshake-timeout option.
    - #1312: back-off when an accept fails for want of descriptors or buffers.
  - **Release.** #1290: nuget.org promotion in hourly batches (decision 13).
- **Stage 10 delivered (2026-10-07), approved 2026-10-09.** Stage 10 was the gated stage. Three of its gates were decisions, and the integrator made them as ADRs under the owner's standing delegation (§7.4, decisions 16–18). The two others stay closed, for reasons outside this program: #829 and #830 belong to the IdentityModel program, and #806–#808 are post-v1. Agent sessions built the cleared items in parallel worktrees, and each item was reviewed, integrated, verified and pushed as it landed.
  - **Decisions** (`1cadb215`): [Http ADR 1](../libraries/Http/DECISIONS.md#adr-1-server-websockets) covers server WebSockets, [Http ADR 2](../libraries/Http/DECISIONS.md#adr-2-trailers-decided-apart-from-grpc) covers trailers apart from gRPC, and [Web ADR 1](../resources/Web/DECISIONS.md#adr-1-how-a-rewrite-changes-the-request-for-the-rest-of-the-pipeline) covers the request view behind rewrite.
  - **#782 `20679f05`: Web.Rewrite.**
    - `UseRewrite(rules => ...)` hands the rest of the pipeline a request view. `IWebRewriteFeature` keeps the client's original path and query.
    - Rules: internal rewrites, 301/302/307/308 redirects, regex rules (interpreted, `NonBacktracking` or source-generated, with a 1 s timeout) and predicate rules.
    - Canonicalization helpers cover HTTPS, `www`/non-`www`, trailing slash and lowercase. The number of rule passes is bounded.
    - Captures are percent-encoded for the part of the URL they land in, so `/go//evil.example` cannot redirect off-site.
  - **#765: WebSockets on HTTP/1.1, HTTP/2 and HTTP/3**, with framing from the BCL.
    - `312254a3`, `bcffc6ec`: `Http.WebSockets` (handshakes, subprotocols, permessage-deflate) and `Web.WebSockets` (`UseWebSockets`).
      - Cross-origin handshakes are refused unless their origin is allowed.
      - The keep-alive interval is configurable.
      - A drain closes open sockets with `1001` through the new Web-root `IWebServerDrainFeature`.
      - Web.Hosting installs the HTTP/1.1 upgrade interceptor by default.
    - `f30e4b42`: the same sockets over extended CONNECT (RFC 8441, RFC 9220).
    - #1316 `127c68bd`: the tunnel. `IHttpExtendedConnectFeature.AcceptAsync` turns an extended CONNECT into a duplex stream on HTTP/2 and HTTP/3. The feature contract moved from Http.ExtendedConnect to core Http; decision 20 returned it (#1368).
    - `8e4bb5ec`: the default upgrade interceptor had put a response sink on every request on every protocol. A new per-exchange response hook (`AddResponseInterceptor`) restores the fast path, and a plain GET is back to the allocation baseline.
    - `586cc318`: the output cache never serves or stores a protocol switch.
    - #1336 `fc87ae02`: `MapWebSocket` maps one endpoint for both handshake shapes. A `MapGet` endpoint works over HTTP/1.1, so in local testing, and fails for every browser on a `UseHttps` endpoint, because those browsers handshake over HTTP/2.
  - **Trailers.**
    - #1314 `2e5a34a2`: HTTP/2 decodes request trailers, which keeps HPACK in step. The same work ended a phantom `GET /` that a late trailer section could cause.
    - #1315 `99ee89fb`: response trailers on HTTP/2 and HTTP/3.
  - **Transport conformance and integrity, found while building the stage:**
    - `a15c89a0`–`32ab6117`, HTTP/2 and HTTP/1.1:
      - #1317: a refused stream's header block is decoded.
      - #1318: frames on a stream the server reset are ignored, with their flow control credited back.
      - #1320: HEADERS padding is stripped.
      - #1321: a request missing a required pseudo-header is reset instead of being served as `GET /`.
      - #1322: HPACK decoding failures are `COMPRESSION_ERROR`. The work also found three HPACK errors the decoder had never detected.
      - #1319: HTTP/1.1 trailers follow the shared trailer rule set.
      - #1323: an unnamed query parameter is skipped instead of failing the connection, which had also let any client write Error-level logs.
      - #1307: dead linked-token code is removed.
      - #1332: a malformed HTTP/2 head resets only its stream.
      - #1333: whitespace before an HTTP/1.1 field's colon is answered `400`.
    - **#1333 closed an HTTP/1.1 request-smuggling desync that is in 10.0.0-preview.1.** After an application read a chunked body into a framing error, the keep-alive drain resumed past the bad line and served whatever followed as a new request. The advisory is drafted privately as GHSA-m7g7-r8qf-qxxw, following decision 14's practice.
    - #1326–#1329 `c034fa15`, `ea826d0e`, `1be500ec`, `a7347f20`:
      - HTTP/2 frames are written atomically.
      - A reset request body faults instead of ending cleanly.
      - Connection-specific fields are dropped from HTTP/2 and HTTP/3 response heads.
      - An HTTP/3 client reset fires `RequestCancelled`. A QUIC or in-memory stream's `ConnectionClosed` now fires when the peer abandons the stream.
  - **Housekeeping.** `c1c36c4c` removes 258 stale `Web.Results` lines from two solution files, and `a122a953` fixes a drain test that passed on a hang.
  - **Docs site** ([cohesion-docs#1](https://github.com/assimalign/cohesion-docs/pull/1)): `455b95b` through `0109cfe` add the Http.WebSockets, Web.WebSockets and Web.Rewrite pages and the WebSockets and rewrite guides. They also sync every page Stage 10 changed, including the 17 area framework tables that now list Http.Cookies and Http.ProtocolUpgrade.

  Verification (final code tip `32ab6117`):
  - **Final run, 77 suites, 0 failures.**
    - **Http family (16):** Http (1,253), Http.Connections (757), Http.WebSockets (101), Http.Cookies (79), Http.DigestFields (66), Http.Sessions (50), Http.Forms (35), Http.Antiforgery (31), Http.ClientFactory (30), Http.ProtocolUpgrade (22), Http.ServerSentEvents (12), Http.InterimResponses (8), Http.Streaming (8), Http.ExtendedConnect (5), Http.RequestLimits (5), Http.Forwarded (5).
    - **Connections (7):** Connections (49), Tcp (46), Security (41), InMemory (30), NamedPipes (30), Quic (28), Udp (19).
    - **All 36 Web suites,** among them Web.Routing (352), Web.Hosting (216), Web.StaticFiles (175), Web.Cors (153), Web.Rewrite (147), Web.SecurityHeaders (143), Web.WebSockets (106) and Web.CookiePolicy (106).
    - **App.Runtime and the 17 area Hosting suites.**
  - **Per item.** Each item was also verified when it was integrated. Every new regression test failed before its fix.
  - **Checks.** The guard passes 36/36 smoke checks under JIT. The release inventory (219 libraries and resources) and the dependency graph check pass.
  - **NativeAOT.** The guard publishes for win-arm64 at `32ab6117` with no trim or AOT warnings, and the native binary passes 36/36 smoke checks.

  Behavior changes for the review:
  - **WebSockets.**
    - Web.Hosting installs the HTTP/1.1 upgrade interceptor on every listener. A request that no application accepts is served as before.
    - A cross-origin WebSocket handshake is refused by default (`AllowedOrigins`, `AllowAnyOrigin`).
    - A `MapGet` WebSocket endpoint misses HTTP/2 and HTTP/3 handshakes; use `MapWebSocket`.
  - **Request trailers.** HTTP/3 request trailers follow the HTTP/2 rule set (`IsProhibitedInTrailers`), and HTTP/1.1 chunked trailers follow it too. A violation resets the stream, or fails the body read on HTTP/1.1.
  - **Superseded contract move.** Stage 10 first moved `IHttpExtendedConnectFeature` from `Assimalign.Cohesion.Http.ExtendedConnect` to core `Assimalign.Cohesion.Http`. Decision 20 (#1368) returned it to the assembly preview.1 shipped it in. The breaks that replace this one are listed under decision 20.
  - **Connections:** `IConnection.ConnectionClosed` on a multiplexed stream also fires when the peer abandons the stream (QUIC `RESET_STREAM`/`STOP_SENDING`).
  - **HTTP/2 conformance.**
    - A request missing `:method`, `:scheme` or `:path` is reset instead of being served as `GET /`.
    - Padded HEADERS are accepted.
    - HPACK decoding failures are `COMPRESSION_ERROR`.
    - A malformed request head resets only its stream (#1332).
  - **Queries:** a query parameter with an empty name is skipped instead of failing the connection. Web.Rewrite still refuses one in a rule target.
  - **HTTP/1.1:** whitespace between a field name and its colon is answered `400` (#1333).

  Questions for the review:
  - **Decisions.** Decisions 16–18 were made under the standing delegation. Confirm them, or reopen any.
  - **Breaking contract move.** Accept moving `IHttpExtendedConnectFeature` to core Http during the previews? The alternative was a reference from Http.Connections to Http.ExtendedConnect, which would have pulled Http.ExtendedConnect into all 18 area frameworks. Answered by decision 20: neither. An interceptor in Http.ExtendedConnect installs the feature over generic core seams.
  - **Connections contract.** Accept the `ConnectionClosed` change (the in-memory driver documented it as local-only before)?
  - **Smuggling advisory.** Publish GHSA-m7g7-r8qf-qxxw (#1333, HTTP/1.1 request smuggling, medium) with the first preview that ships the fix, as decision 14 does for #1304 and #1308?

  Scope-creep filed:
  - **Web.** #1324 (redirect `Location` encoding in HttpsPolicy and StaticFiles), #1325 (HTTP logging downcasts the request).
  - **HTTP/2 and HTTP/3 conformance.** #1334 (classic CONNECT and asterisk-form validation), #1335 (repeated query keys).
  - **Performance.** #1337 (allocating feature lookups), #1338 (an HTTP/3 thread-pool hop per request).
  - **QUIC.** #1330 (half-close).
  - **HTTP/1.1 and Web.** #1339 (413 and 408 after dispatch are answered as 500), #1340 (a malformed body is logged as an application defect), #1341 (field values trimmed of Unicode whitespace).

  The owner's review (2026-10-09) confirmed decisions 16–18 and adopted 19 and 20 (§7.4). Decision 20 produced these follow-ups, delivered and approved the same day (decision 21):
  - **#1367 `d7f271bc`: Http.Tls.** `IHttpTlsConnectionFeature` and `context.TlsConnection` move to a new `Assimalign.Cohesion.Http.Tls`. The transport publishes an `ITlsConnectionInfo` facet on its connection info, which is a snapshot that never holds the live connection. The accessor builds the feature on first read.
  - **#1368 `6a453c25`: the extended CONNECT feature goes back to Http.ExtendedConnect.**
    - `HttpExtendedConnect.CreateInterceptor()` installs it over two generic core seams: `HttpExchangeInterceptorRequestContext.Protocol`, and `IHttpExchangeControl.CanAcceptTunnel` with `AcceptTunnelAsync`.
    - The transport's accept keeps its guard order.
    - Web.Hosting registers the interceptor by default.
    - Http.WebSockets references Http.ExtendedConnect.
  - **#1369 `a8435d7d`: an empty `:protocol` is malformed.** Found by the review of #1368. Every HTTP/2 extended CONNECT violation now resets the stream instead of sending GOAWAY (RFC 9113 §8.1.1).
  - **Review corrections** (`626d046c`, `adf6b5c5`, `d244311a`):
    - the TLS override rule;
    - the accept-attempt latch;
    - the interceptor's guard order;
    - the 12-node Http map;
    - a preview.1 behavior-change section, checked against the tag.
  - **Verification at `d244311a`: 78 suites, 0 failures.**
    - Http 1,262; Http.Connections 778; Http.Tls 10; Http.ExtendedConnect 21; Http.WebSockets 101.
    - Web.Hosting 217, Web.WebSockets 106.
    - The rest of the Http, Connections and Web families, App.Runtime and the 17 area Hosting suites.
    - The guard passes 36/36 checks under JIT.
    - The release inventory (220) and the dependency graph checks pass.
    - Each new #1369 test fails with the old validator.
    - The guard, published NativeAOT for win-arm64 from a clean worktree at `d244311a`, has no trim or AOT warnings. The native binary passes 36/36 smoke checks, including a WebSocket over HTTP/2 extended CONNECT through the new interceptor.
  - **Docs site** ([cohesion-docs#1](https://github.com/assimalign/cohesion-docs/pull/1)): `0dac06b` adds the Http.Tls pages. `1deb713`, `42d29fc` and `86e7148` sync the Http, Web and Connections pages, the server and WebSockets guides, and the 17 area framework tables. Those tables now list Http.ExtendedConnect and cite the Runtime producers' member lists instead of a `frameworks/` folder that no longer exists.
  - **Source corrections** (`35ad42f0`): the Http.WebSockets bare-listener sample now registers the extended CONNECT interceptor on a listener that serves HTTP/2. The docs-site check found the problem.
- **Stage 11 delivered (2026-10-10), awaiting owner review.** Stage 11 closed the remote-triggerable holes in the Phase 2 follow-up backlog (decisions 22–31), and the owner's Web restructure landed between its two waves (decisions 32–36). Agent sessions built each session in an isolated worktree. Every session was reviewed, its serious findings were challenged by two skeptics, and confirmed ones were fixed. An integrator then cherry-picked each wave and an auditor three-way-checked every session-touched file. Every new regression test was shown to fail before its fix.
  - **Wave 1** (15 issues):
    - #1072 `99323261`, `85cabef1`: an HTTP/2 reset no longer frees a stream slot while its exchange runs. Server-provoked resets count toward the flood budget (Rapid Reset / MadeYouReset shape).
    - #1075 `7fb8f62e`, `79718e8b`: a cancelled send resets the stream.
    - #1074 `3e4afd2f`: WINDOW_UPDATE on any closed stream is ignored, and every server reset is remembered.
    - #1080 `83dd297e`, `948bd43b`: QUIC stream and connection aborts carry their HTTP/3 codes, and the 64 KiB / 5 s request-body drain is gone.
    - #1082 `c1a8de87`, `376483d9`, `2be5d72e`: HTTP/3 advertises and enforces `SETTINGS_MAX_FIELD_SECTION_SIZE`, and repeated fields combine in linear time.
    - #1339 `a8419185`, `64eac3fc`: HTTP/1.1 body limits found after dispatch answer 413 or 408.
    - #1375 `da158661`, `2dc6850e`, `17f4425b`: chunk framing lines, the framing total and trailer sections are capped. A bare CR or LF in a framing line is rejected.
    - #1341 `bd60d7b4`, `e4965ae3`, `bff5a3dd`: HTTP/1.1 fields reject control characters and trim only SP and HTAB. Lines decode as Latin-1, and a request line with an octet outside VCHAR/SP gets 400.
    - #1301 `77a0bef2`, `c83ebf50`: HTTP methods are case-sensitive (decision 26).
    - #1186 `29a462cf`, `5ca7194d`: a name without an extension maps to no content type, and 8.3 short names are refused at the static-files gate (decision 29).
    - #1155 `40990cb5`, `6b89c58d`: key-ring reloads for unknown key ids are throttled and single-flight.
    - #1077 `41b75933`, `1c7e9996`: `RequireHost` matches the effective host. It selects routes and does not gate access.
    - #1377 `01cd174b`, `ecc5cbd2`: the Email rule runs in linear time, and the `Matches` rule is bounded.
    - #1312 `53aac246`, `7dcbda10`: a TCP accept that fails for want of descriptors or buffers backs off and retries.
    - #1210 `9c01ece1`, `4cf6e27b`: `UseForms` answers an oversized or malformed form with 413 or 400.
  - **Web restructure** (decisions 32–36), recorded in §4:
    - #1382 (`f71ab0af`, `57e1f049`);
    - #1379;
    - #1380;
    - #1381.
  - **Wave 2** (5 issues):
    - #1085 `c1c9cfed`, `dd355e15`, `6cb5e8aa`, `4bf517f2`: HTTP/2 and HTTP/3 enforce keep-alive, request-headers timeouts and the minimum request-body data rate. The HTTP/2 window exemption is bounded, and the transport's 408 or 413 is the exchange's status.
    - #1084 `40f37e76`: an HTTP/3 streamed response whose body was rejected resets with `H3_REQUEST_CANCELLED`.
    - #1183 `feb42aad` … `5f4f7b2d`, `d8cd2482`: every head writer refuses CR, LF and NUL in response fields before writing a byte, the 101 writer included. HTTP/2 and HTTP/3 send values without edge whitespace (which also resolves #1386).
    - #1376 `e2581ee2`, `c4115459`, `5d95b0be`: HTTP/2 and HTTP/3 validate received field names and values.
    - #1340 `4b7ed27d`, `17b74df4`: on HTTP/1.1, a malformed or limit-breaking body reports as a client fault through `IWebClientFaultFeature`. HTTP logging, the exception boundary and telemetry report the status the client got. HTTP/2 and HTTP/3 come with #1378.
  - **Integration fixes:**
    - `bff5a3dd` runs the bare-CR/LF framing check once.
    - `5f4f7b2d` keeps an HTTP/3 exchange with a refused head running until it is replaced.
    - `e0c65931` is an XML doc fix.
  - **Advisories** (decision 24): eleven private drafts for holes in 10.0.0-preview.1.
    - Wave 1: GHSA-chg6-g87v-34gw (#1072), GHSA-w482-4pxj-5xrr (#1082), GHSA-chxw-jff3-f567 (#1375), GHSA-89wj-3jhm-r79h (#1377), GHSA-4f5r-vpqq-ww6g (#1312), GHSA-f98r-9r57-cmm3 (#1341), GHSA-2jv5-8mqr-x6x3 (#1301), GHSA-4m82-rch3-cgf5 (#1155).
    - Wave 2: GHSA-7mfj-qc8j-85hf (#1085), GHSA-r5v5-r9xj-cwgv (#1183), GHSA-p4ph-jm8r-hh36 (#1376).
    - Each draft was fact-checked against the tag. They publish with the first preview that ships the fixes.
  - **Verification at `d8cd2482`:** 80 suites and 6,423 tests, 0 failures (Http 1,377, Http.Connections 1,126, Web.Routing 386, Web.Hosting 284, ObjectValidation 283, the whole Http, Connections and Web families, App.Runtime and the 17 area Hosting suites). The guard passes 36/36 under JIT. The release inventory (221) and the dependency graph checks pass. The guard, published NativeAOT for win-arm64 from a clean worktree, has no trim or AOT warnings and passes every native smoke check.
  - **Follow-ups filed:**
    - Stage 12: #1383 (per-request body-rate opt-out on every protocol, P002) and #1384 (a minimum response data rate against slow readers, P002, triage for an advisory).
    - Conformance: #1385 (`100-continue` on HTTP/2 and HTTP/3) and #1387 (HPACK and QPACK decode Latin-1).
    - Stage 13: #1388 (a streamed response reported as started after a refused head).
    - Stage 14: #1389 (single response-interceptor allocations).
    - #1378 gained the 408 case and its design question. #1324 gained HttpsPolicy's raw-host fallback.

  Behavior changes for the review:
  - **Methods:** `get` and `post` are unknown methods on every protocol. `HttpMethod` keeps the token as received and compares ordinally (#1301).
  - **HTTP/1.1:**
    - A field value with a control character (other than HTAB) is answered 400, and so is a request line with an octet above 0x7E.
    - Values are trimmed of SP and HTAB only (#1341).
    - Chunk framing lines over `MaxChunkFramingLineSize` (8 KB) are malformed (#1375).
  - **HTTP/2 and HTTP/3:**
    - A connection idle for `KeepAliveTimeout` (130 s by default) closes, a head slower than `RequestHeadersTimeout` (30 s) is rejected, and a body below 240 B/s after a 5 s grace is answered 408 (#1085). There is no per-request opt-out until #1383.
    - Received fields with CR, LF, NUL or other invalid characters are malformed requests (#1376).
  - **Responses:** a header or trailer with CR, LF or NUL throws `InvalidResponseField` before any byte is written. On HTTP/2 and HTTP/3, edge whitespace is trimmed (#1183).
  - **Web restructure:** see #1379–#1381 in §4.
    - `using Assimalign.Cohesion.Web.Routing;` is needed for `Map`, `MapWhen`, the endpoint and path-base features.
    - Registration verbs are `builder.Services.AddX(...)`.
    - Feature registrations must be singleton and not disposable.
  - **Static files:** a file without an extension gets no content type, so it is not served unless `ServeUnknownContentTypes` is on (#1186).
  - **Validation:** an email address over the RFC 5321 size limits fails. An invalid `Matches` pattern throws when the rule is declared (#1377).
  - **Data protection:** `UnknownKeyReloadInterval` (30 s) bounds reloads for unknown key ids (#1155).

  Questions for the review:
  - **#1380's shape:**
    - Six verbs use static factories, through hidden `*Components` types, because the builder template always requires a callback. Making the callback optional is a generator change. Should it be made?
    - The Authentication reshape (`auth.UseDataProtection(...)`) and the snapshot semantics of the error-handling and serialization builders: confirm them.
  - **Rate policy:** HTTP/2 and HTTP/3 now apply the listener-wide minimum body rate, as HTTP/1.1 always has, so streaming request bodies that pause get 408 until #1383. Is that acceptable for the previews, or should #1383 move into Stage 12's first wave?
  - **A possibly missing advisory:**
    - In 10.0.0-preview.1, HTTP/3 buffered each whole request stream in memory with no size cap. That is an unauthenticated memory exhaustion on any `UseHttp3` endpoint.
    - Stage 5 fixed it (#1066), before decision 8 began the advisory practice, so no advisory was drafted.
    - Draft one now, on decision 24's terms?
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

**After Stage 7 (2026-10-01):**
- Fixed: D11's placeholder half. `Web.Cors`, `Web.Authorization` and `Web.CookiePolicy` are real.
- D12, in part: `AddAntiforgery(dataProtectionProvider)` makes antiforgery tokens survive restarts and validate on every instance that shares the key repository. Without a provider the per-process key is still the default, now documented as development-only, and the key ring's default location is unchanged (#806–#808).

**After Stage 9 (2026-10-06):**
- Fixed: D9 (#1063). `UseHttps` serves HTTP/2 and HTTP/1.1 per connection from ALPN, and so does the ambient orchestration endpoint.
- Found and fixed: two denial-of-service holes the audit missed, both in the released 10.0.0-preview.1.
  - #1304: a failed or stalled TLS handshake stopped an HTTPS listener.
  - #1308: a client that reset before the accept stopped any TCP listener.

  Their advisories are drafted (GHSA-r9cf-3952-rg7f, GHSA-r66x-xgrx-gh8m). Every connection driver now follows one rule, documented on `IConnectionListener.AcceptAsync`: a listener contains each connection's failure.
- Still open: D12 (#806–#808).

**After Stage 10 (2026-10-07):**
- Found and fixed: an HTTP/1.1 request-smuggling desync in 10.0.0-preview.1 (#1333). After an application read a chunked body into a framing error, the keep-alive drain resumed past the bad line and served what followed as a new request. Its advisory is drafted privately as GHSA-m7g7-r8qf-qxxw.
- Still open: D12 (#806–#808).

**After the Stage 10 review (2026-10-09):**
- Core Http holds base contracts and generic seams only (decision 20). The TLS feature moved to Http.Tls (#1367), and the extended CONNECT feature went back to Http.ExtendedConnect behind an interceptor (#1368).
- Found and fixed: an empty `:protocol` passed validation on HTTP/2 and HTTP/3 (#1369), so a GET could carry `Protocol = ""` and report a tunnel it could accept. The gap is in preview.1's validator, but preview.1 had no tunnel accept.

### 7.3 Missing capabilities

| Capability | Status | Tracking |
|---|---|---|
| Endpoint-aware pipeline (middleware between route match and handler) | Delivered in Stage 6. `UseRouting` publishes the endpoint and the pipeline terminal runs it; RateLimiting, RequestTimeouts, Caching and Diagnostics read the published endpoint, and their workarounds are gone. Every `Map*`, typed ones included, returns a convention builder, and groups compose metadata at route-table build. A CORS preflight publishes its candidate endpoint. | #1054, #1055; unblocks CORS, authorization, antiforgery and OpenAPI |
| CORS | Delivered in Stage 7 (`Web.Cors`): policies validated at startup, preflights answered from the candidate endpoint's policy, `RequireCors`/`DisableCors` on routes and groups | #3, #109–#111 |
| Authorization (policies, `RequireAuthorization`, `UseAuthorization`) | Delivered in Stage 7 (`Web.Authorization`): policies over `ClaimsPrincipal`, `RequireAuthorization`/`AllowAnonymous`, a fallback policy, per-endpoint schemes | #155; the #828 adapter later |
| Cookie-policy enforcement | Delivered in Stage 7 (`Web.CookiePolicy`): consent, `Secure`/`HttpOnly`/`SameSite` floors, the `__Host-` and `__Secure-` prefixes, the 400-day cap | #156 |
| Antiforgery in the pipeline | Delivered in Stage 7 (`Web.Antiforgery`): enforced per endpoint and automatically on `[FromForm]` endpoints; tokens are sealed with data protection when a provider is passed | #1057 |
| OpenAPI for Web endpoints | Delivered in Stage 8 (`Web.OpenApi`, NuGet-only): OpenAPI 3.0, 3.1 and 3.2 documents from endpoint metadata and the app's source-generated JSON contracts, with security from each endpoint's authorization policy. The five OpenApi packages are built, tested and released | #152, #1062 |
| Handler return values (`Task<T>`); generator diagnostics | Delivered in Stage 8: typed handlers return `T`, `Task<T>` or `ValueTask<T>`, written with content negotiation, and unsupported shapes fail the build (`COHWEB0001`–`COHWEB0007`) | #1059 |
| Validation problem responses | Delivered in Stage 8 (`Web.Validation`): a registered validator runs on a bound body model before the handler, and an invalid body is answered 400 problem+json keyed by member path | #1060 |
| File binding (`IHttpFormFile`); file and stream results | Delivered in Stage 8: uploaded files bind in typed handlers (413 over the limit, 400 when malformed), and `SendFileAsync`/`WriteStreamAsync` answer conditional and single-range requests | #1061 |
| Pipeline branching (`Map(path)`, `MapWhen`, `UseWhen`, `Run`); fallback routes (`MapFallbackToFile`) | Delivered in Stage 6. A path branch publishes an effective path and path base instead of rewriting the request, which stays gated on #782 | #1056 |
| Server telemetry (`ActivitySource`, `Meter`, `traceparent`, request ID) | Delivered in Stage 9: a server span and the HTTP server metrics from `Assimalign.Cohesion.Web.Hosting`, W3C trace context, a request id (`IWebRequestIdFeature`) and `http.route` | #1064 |
| Hosting diagnostics; lame-duck drain | Delivered in Stage 9: bind failures, accept-loop faults, connection faults and cut-short drains are logged through `builder.Logging`, and a stop drains lame-duck style, cancelling only what outlives its budget | #147; #146 |
| mTLS (client certificates visible to handlers); multi-protocol ALPN endpoints; config for HTTP/3, limits and the connection cap | Delivered in Stage 9: client certificates and the session's TLS details reach handlers (`context.TlsConnection`, in Http.Tls since decision 20), `UseHttps` serves HTTP/2 and HTTP/1.1 per connection, and configuration binds HTTP/3, certificate files, the connection cap and the HTTP/2 limits. Authenticating a user from a client certificate is #1305 | #1065; #1063 |
| HTTP/2 and HTTP/3 request-body cap (413); HTTP/2 timeouts; trailers | Body cap delivered in Stage 5 on both protocols (#1048, #1066). Trailers delivered in Stage 10: HTTP/2 decodes and exposes request trailers like HTTP/1.1 and HTTP/3, and HTTP/2 and HTTP/3 send response trailers. HTTP/2 and HTTP/3 timeouts and request-body data rates delivered in Stage 11 (#1085); a minimum response data rate is #1384. | #1085, #1384; #1314, #1315; gRPC hosting stays outside the program |
| Security headers (CSP, nosniff, Referrer-Policy, frame-ancestors) | Delivered in Stage 7 (`Web.SecurityHeaders`): safe defaults on every response; opt-in CSP with per-request nonces, Permissions-Policy and the cross-origin isolation fields; per-endpoint overrides | #1058 |
| A representative Web app AOT-published in CI | Delivered in Stage 5: `Web.AotGuard` is published NativeAOT and smoke-tested by the `resource-web.yml` `aot-guard` job | #1052 |
| OIDC handler; JWT Bearer authority/JWKS discovery | Absent | blocked on IdentityModel #829/#830 |
| WebSockets | Delivered in Stage 10. `Http.WebSockets` and `Web.WebSockets` (`UseWebSockets`, `MapWebSocket`) run over HTTP/1.1 Upgrade and over HTTP/2 and HTTP/3 extended CONNECT, with framing from the BCL. Cross-origin handshakes are refused by default, and a drain closes open sockets with `1001` | #765, #1316, #1336; a hub framework stays a separate decision |
| URL rewrite | Delivered in Stage 10 (`Web.Rewrite`): rewrites hand the rest of the pipeline a request view; redirects, regex and predicate rules, canonicalization helpers | #782 |

### 7.4 Owner decisions

Decisions 1–4 were adopted with the lineup on 2026-09-30: the owner approved the suggested stages, which rest on these recommendations. Decision 7 was adopted in the Stage 7 review on 2026-10-01. Decisions 8–13 were adopted in the Stage 8 review on 2026-10-06, and decisions 14–15 in the Stage 9 review on 2026-10-07. In both reviews the owner adopted every recommendation. The integrator made decisions 16–18 on 2026-10-07 to clear Stage 10's gates, under the owner's standing delegation. The owner confirmed them in the Stage 10 review on 2026-10-09, together with decisions 19–20, and adopted decision 21 in the review of decision 20's follow-ups the same day. Decision 31 proposes an answer to decision 5, pending the owner's date. The owner made decisions 32–36 on 2026-10-09 while Stage 11 ran, directing a Web restructure (#1379–#1382) that lands between its two waves.

1. **Which claim model authorization runs on.**
   - Web: authenticates onto BCL `ClaimsPrincipal` by a recorded decision (`Web.Authentication/docs/DESIGN.md:157-167`).
   - IdentityModel: its DESIGN makes canonical claims the authorization input and calls an authorization model a non-goal (`IdentityModel/docs/DESIGN.md:1004-1009, 1030-1032`). #828 (an authorization model over canonical claims) is still open.
   - Recommendation: `Web.Authorization` evaluates over `ClaimsPrincipal`, and #828 later contributes an adapter instead of being a prerequisite.
2. **How to split routing.** Recommendation: `UseRouting()` becomes non-terminal (match, publish the endpoint, call `next`), and the pipeline's terminal runs the matched endpoint. This is a breaking change: middleware registered after `UseRouting()` would start running for matched requests.
3. **Forwarded headers: read the effective values, or rewrite the request.** Recommendation: keep the documented model where middleware read the effective values, and make every consumer read them (D7).
4. **Security-headers middleware in v1?** Recommendation: yes; it is small, P3.
5. **The Web v1 date.** `DELIVERY_ROADMAP.md` ends L3.1 on 2026-10-15, and Stages 5–7 alone are about 20 items. Either move the date or cut v1 at the end of Stage 7. *(Decision 31, 2026-10-09: gate v1 on Stages 11 and 12; the owner sets the date at the Stage 12 review.)*
6. **Standing gates, open since July:** the WebSockets ADR (#765) and the request-mutation seam for rewrite (#782). Resolved 2026-10-07 by decisions 16 and 17.
7. **How authorization combines `AllowAnonymous` with requirements (raised by Stage 7, adopted 2026-10-01).**
   - Stage 7 first shipped ASP.NET Core's rule: `AllowAnonymous` anywhere on an endpoint wins. A route that required authorization inside an anonymous group therefore ran anonymously, while routing's last-wins dispatch check still treated it as protected and demanded `UseAuthorization`.
   - Adopted: the most specific item wins, so `AllowAnonymous` clears only the requirements declared before it. That fails closed for the inner requirement and matches the dispatch check (`e451032b`).
8. **A security advisory for #1180 (raised by Stage 8, adopted 2026-10-06).** `PhysicalFileSystem` let paths escape its root in the released 10.0.0-preview.1. Adopted: an advisory names `Assimalign.Cohesion.FileSystem.Physical` and `.InMemory` at 10.0.0-preview.1 and earlier as affected. It is drafted privately as [GHSA-5jrr-79fc-fc98](https://github.com/assimalign/cohesion/security/advisories/GHSA-5jrr-79fc-fc98) and published with the first preview that ships the fix, so it never discloses an unpatched hole.
9. **Where request validation lives (raised by Stage 8, adopted 2026-10-06).** Adopted: `Web.Validation` stays its own package and `Web.Api` takes no ObjectValidation dependency, which keeps the 2026-07-20 descoping. App.Web ships both.
10. **An unregistered policy name in the OpenAPI document (raised by Stage 8, adopted 2026-10-06).** Adopted: the document fails, naming the endpoint, as every request to that endpoint does. A document that describes a broken endpoint as open would be worse.
11. **ObjectValidation's evaluation order (#1221, raised by Stage 8, adopted 2026-10-06).** Adopted: rules and members run in declaration order, as the queue contract says, fixed in Stage 9. The behavior change is accepted: a chain reports its first failing rule's message, `Stop` stops at the first failing member in declaration order, and the `errors` map follows declaration order.
12. **Two OpenApi seams for contributing operations (raised by Stage 8, adopted 2026-10-06).** Adopted: keep `IOpenApiMetadataProvider` (generated metadata composed at compile time) and `IOpenApiEndpointSource` (sources consulted when a document is built) until OpenApi v1, and revisit them then.
13. **Two program-level items (raised by Stage 8, adopted 2026-10-06).**
    - Every Cohesion assembly carries `RequiresPreviewFeatures`, so a consumer on the plain .NET SDK gets CA2252 until it enables preview features. Adopted: accept it through the previews and decide before GA.
    - The release is about 389 packages, above nuget.org's 350-per-hour push ceiling. Adopted: promote in hourly batches (#1290).
14. **Publishing the #1304 and #1308 advisories (raised by Stage 9, adopted 2026-10-07).** Both holes are in 10.0.0-preview.1 and earlier. Adopted: as decision 8 does for #1180, both advisories stay private drafts and are published with the first preview that ships the fixes:
    - [GHSA-r9cf-3952-rg7f](https://github.com/assimalign/cohesion/security/advisories/GHSA-r9cf-3952-rg7f) for #1304 names `Assimalign.Cohesion.Http.Connections`, `.Connections` and `.Connections.Quic`.
    - [GHSA-r66x-xgrx-gh8m](https://github.com/assimalign/cohesion/security/advisories/GHSA-r66x-xgrx-gh8m) for #1308 names `Assimalign.Cohesion.Connections.Tcp`.
15. **`IHttpConnectionContext.BeginGracefulClose()` as a required member (raised by Stage 9, adopted 2026-10-07).** Adopted: the member stays abstract, with no default implementation, and the source break for an implementer outside this repository is accepted during the previews.
16. **Server WebSockets (#765, decided 2026-10-07; [Http ADR 1](../libraries/Http/DECISIONS.md#adr-1-server-websockets)).** Adopted: WebSockets on HTTP/1.1, HTTP/2 and HTTP/3.
    - **Framing:** the BCL's RFC 6455 implementation (`WebSocket.CreateFromStream`). Cohesion writes no codec, which supersedes #765's codec criteria.
    - **Packages:** `Http.WebSockets` owns the handshakes and negotiation, and `Web.WebSockets` owns the origin policy and the drain.
    - **Transport:** an extended CONNECT tunnel on HTTP/2 and HTTP/3. Browsers use RFC 8441 on the HTTP/2 connections `UseHttps` negotiates.
    - **Defaults:** HTTP/1.1 upgrade is on by default in Web.Hosting. A cross-origin handshake is refused unless its origin is allowed, and a drain closes open sockets with `1001`.
17. **The request seam for rewrite (#782, decided 2026-10-07; [Web ADR 1](../resources/Web/DECISIONS.md#adr-1-how-a-rewrite-changes-the-request-for-the-rest-of-the-pipeline)).** Adopted: a rewrite hands the rest of the pipeline a request view whose path and query are rewritten, and the original values stay readable through `IWebRewriteFeature`.
    - This follows Web.Compression and Web.RequestTimeouts.
    - An effective-path feature was rejected: 20 readers and the endpoint generator would have to migrate, and a reader that was missed would silently ignore the rewrite.
18. **Trailers, decided apart from gRPC (decided 2026-10-07; [Http ADR 2](../libraries/Http/DECISIONS.md#adr-2-trailers-decided-apart-from-grpc)).** Adopted:
    - **Request trailers:** HTTP/2 decodes them, which fixes an HPACK desynchronization, and exposes them.
    - **Response trailers:** HTTP/2 and HTTP/3 send them through `IHttpResponse.Trailers`. HTTP/1.1 does not.
    - **gRPC hosting** stays outside this program and needs its own ADR on serialization.
19. **The Stage 10 review (adopted 2026-10-09).**
    - Decisions 16–18 are confirmed.
    - `IConnection.ConnectionClosed` now fires when the peer abandons a multiplexed stream (#1329). This change is accepted.
    - [GHSA-m7g7-r8qf-qxxw](https://github.com/assimalign/cohesion/security/advisories/GHSA-m7g7-r8qf-qxxw), for #1333, will be published with the first preview that ships the fix, as decision 14 does for #1304 and #1308.
20. **Feature contracts leave core Http (raised by the owner in the Stage 10 review, adopted 2026-10-09).** Core Http keeps only base contracts and generic seams.
    - **The TLS feature (#1367).** `IHttpTlsConnectionFeature` and `context.TlsConnection` move to a new `Assimalign.Cohesion.Http.Tls`. The transport publishes the handshake facts as an `ITlsConnectionInfo` facet on its connection info, and the accessor builds the feature on first use. It costs one App.Web entry and no new transport reference.
    - **The extended CONNECT feature (#1368).** `IHttpExtendedConnectFeature` returns to Http.ExtendedConnect, where preview.1 shipped it. An interceptor installs it, as Http.ProtocolUpgrade does. Core gains two generic seam members: `HttpExchangeInterceptorRequestContext.Protocol`, and `IHttpExchangeControl.CanAcceptTunnel` with `AcceptTunnelAsync`. This supersedes the Stage 10 move, which had followed the TLS feature into core.
    - **`ITlsConnectionInfo` stays in the Connections contracts library.** The TLS layer and the QUIC driver both implement it, and HTTP reads it to choose the protocol through ALPN. Moving it into Connections.Security would make all three depend on the SslStream layer.
    - **Consequences, accepted with the decision:**
      - `IHttpExchangeControl`, which shipped in 10.0.0-preview.1, gains `CanAcceptTunnel` and `AcceptTunnelAsync`. That is a source break for an implementer outside this repository, accepted on the terms of decision 15.
      - In preview.1 the transport installed the extended CONNECT feature itself. A listener now has to register `HttpExtendedConnect.CreateInterceptor()` for `context.ExtendedConnect` to appear. Web.Hosting registers it by default, after the upgrade interceptor. A host that clears `options.Interceptors` loses WebSockets on all three protocols.
      - `Features.Get<IHttpTlsConnectionFeature>()` is null until something reads `context.TlsConnection`.
      - Http.ExtendedConnect becomes a private member of the 17 non-Web area frameworks, and Http.Tls a public member of App.Web.
      - An ordinary request pays 8 bytes for the new `Protocol` field. The interceptor itself allocates nothing, and an ordinary exchange still gets no response sink.
21. **The extended CONNECT advertisement stays unconditional (raised by the review of #1367–#1369, adopted 2026-10-09).** The owner approved the follow-ups (#1367–#1369).
    - The HTTP/2 and HTTP/3 transports keep advertising `SETTINGS_ENABLE_CONNECT_PROTOCOL` whether or not a listener registers `HttpExtendedConnect.CreateInterceptor()`.
    - Gating the setting would need a listener option, and the only configuration it would help is a documented misconfiguration: the HTTP/1.1 upgrade interceptor registered without the extended CONNECT one.
    - Web.Hosting registers both by default. Http.ExtendedConnect's DESIGN states the consequence for a bare listener.

Decisions 22–31 line up Stages 11–14 (2026-10-09). The integrator made them under the owner's standing delegation ("go with all recommendations or make the best decision you feel is correct"), after the owner said to continue with the next work. Each is open to revision at the Stage 11 review.

22. **Reconciling the backlog.** Of the 75 open follow-ups:
    - #1076 and #1081 are fixed on this branch (`a8435d7d`; `a7347f20` under #1329), and PR #1094 closes them.
    - #1071 is closed as a duplicate of #1339.
    - #1074 is half fixed: its WINDOW_UPDATE path still draws a second reset, reproduced at runtime.
23. **Lineup and order.** Stages 11–14 (§4) take security and remote-client-triggerable defects first, then conformance, correctness, and performance and DX. Hot transport files are split into serial sessions that run in parallel with each other. FileSystem, ObjectValidation, OpenApi, DI, Configuration, Database, release and build items stay with their programs.
24. **Advisories.** #1082 rises to P001. As in decision 14, every Stage 11 defect that an unauthenticated client can reach in 10.0.0-preview.1 gets a private advisory draft, published with the first preview that ships its fix. #1290's batched promotion is on that path.
25. **The Email ReDoS ships here.** #1377 is split from ObjectValidation's #1293 and lands in Stage 11, because Web.Validation runs the rule on request bodies.
26. **HTTP methods become case-sensitive (#1301).** `get` is an unknown method on every protocol, and Web.Cors compares ordinally. The break is accepted on decision 15's terms.
27. **Response fields are checked at encode time (#1183).** Every head writer rejects CR, LF and NUL before writing a byte, the Http.ProtocolUpgrade 101 writer included. One core field rule serves #1183, #1341 and #1376. A check in the header collection alone can be bypassed by any `IHttpHeaderCollection` implementation.
28. **How a client fault reaches the Web layer (#1340).** `IHttpExchangeControl` gets a generic report-don't-throw member with a default implementation that returns null, following decision 20's seam rule. Body streams keep throwing `IOException`/`InvalidDataException`. HTTP/1.1 lands in Stage 11, and HTTP/2 and HTTP/3 are #1378.
29. **Content-type lookup (#1186).** It splits into a file-name lookup and an extension lookup that requires the leading dot. A name with no dot, and a bare dotfile, map to nothing. The source break is accepted during the previews.
30. **#1080 and #1085 land whole in Stage 11.** #1080 widens to carry the connection-close code. #1084 rides #1085's session, because #1085's HTTP/3 rejections use the streamed send path that #1084 gates.
31. **Later stages, settled now so their sessions need no gate:**
    - A single read of a repeated query or form key returns the first value, and a multi-value view replaces urlencoded's comma-join (#1335, #1211).
    - Certificate authentication goes in a new `Web.Authentication.Certificate` (#1305).
    - Transport rejections are reported through a Meter and an EventSource (#1300).
    - Decision 5 is answered the same way: Web v1 is gated on Stages 11 and 12, not on `DELIVERY_ROADMAP.md`'s 2026-10-15. The owner sets the new date at the Stage 12 review.

Decision 32 is the owner's, made on 2026-10-09 after the lineup.

32. **COHRES002 relaxed (owner decision, 2026-10-09).** The owner: "So I decided I'm going to change the build rule. I am going to allow the Hosting project tobe [sic] able reference all `Web.*` projects, or more generically all the `<Area>.*` projects. This makes more sense".
    - **The rule now:** an area's exact hosting module may reference any library in its own area. Until now it could reference only the area root and its own hosting family.
    - **Exclusions, which follow from earlier decisions.** The build still rejects a hosting-module reference to each:
      - `<Area>.Testing`. It references the hosting module, so the reverse reference would be a cycle.
      - `<Area>.ApplicationModel`. The realization-plan design keeps the runtime off it: generated code in the consumer executable joins the declarative plane to the runtime. COHAM001 bounds the other direction.
      - `<Area>.ApplicationModel.Orchestration`. R8 (the owner decisions of 2026-09-25) makes it a gateway-side, opt-in, NuGet-only package that is never an `App.<Area>` member, so a runtime reference would force it into every framework that carries the module. The Orchestration rules bound the other direction.
      - The framework producers `<Area>.Refs` and `<Area>.Runtime`. They are packaging shells that reference the hosting module.
      - Test, example, sample and fixture projects. They are harnesses.
    - **The two ApplicationModel exclusions hold by any route.** The build also rejects either package in the hosting module's resolved closure, so a library the module may now reference cannot bring one in. The other exclusions are checked on direct references.
    - COHRES001, COHRES003, COHRES004 and COHAM001 are unchanged. `CohesionHostingIsolationExemptions` still waives COHRES002, which now matters only for the excluded categories.
    - **The framework-closure consequence.** Each reference a hosting module takes ships its closure in every framework that carries the module. `Web.Hosting` is a private member of all 17 other area frameworks, so each reference it takes is a membership change in all of them. A hosting module references only what the runtime needs.
    - **What it unblocks.** The Web root's features can leave the root: the endpoint and path base to `Web.Routing`, and the request id, response completion and drain to a new `Web.Server`, both referenced by `Web.Hosting`.
    - **What it does not change.** Registering a feature never needs a hosting-module reference to it: feature verbs still ship with the feature package.
    - Recorded in `build/Targets/Build.Rules.targets`, `.claude/rules/resource-areas.md`, `.claude/rules/web-area.md` and `resources/Web/README.md`.
33. **The Web root holds no feature contracts (owner, 2026-10-09; #1379).** Decision 20's rule extends to `Assimalign.Cohesion.Web`.
    - `IWebEndpointFeature`, `IWebPathBaseFeature` and the `Map(path)` branching move to Web.Routing.
    - `IWebRequestIdFeature`, `IWebResponseCompletionFeature` and `IWebServerDrainFeature` move to a new `Assimalign.Cohesion.Web.Server`.
    - Web.Hosting references both packages under decision 32. Its telemetry reads the endpoint's route template.
    - The owner chose this over one shared features package. Placing the contracts in Web.Hosting is ruled out by COHRES001.
34. **Feature registration moves to `builder.Services` (owner, 2026-10-09; #1380).**
    - The eight `Add<Feature>` verbs leave `extension(IWebApplicationBuilder)` and become `builder.Services.AddX(...)`, as in other .NET hosting models. They are delivered by component integration (`.claude/rules/component-integration.md`): each feature package declares the integration, and the generator emits the verb into the application.
    - Web.Hosting needs no feature references for them.
    - `IWebApplicationBuilder.AddFeature` (both overloads) stays as the raw registration path.
    - Pipeline `Use*` and `Map*` verbs stay in their packages.
    - `AddOpenApi` moves too.
    - **Rejected alternatives:**
      - moving the verbs into Web.Hosting, which would land every feature's closure in the 17 area frameworks;
      - a factory-only `AddFeature<TFeature>` seam, prototyped on `proto/web-feature-factory-seam` and rejected because it removed raw `AddFeature`.
35. **`IHttpFeature` registrations are singletons (owner, 2026-10-09; #1380).** A measured analysis found no performance or functional benefit in scoped or transient features. At 8 features, a scope per exchange costs +495 ns and +1,536 B, and transient through the container costs +235 ns and +280 B.
    - Scoped breaks every composition-time reader, because one scoped item makes the whole `IEnumerable<IHttpFeature>` scoped.
    - Transient sends `Map*` routes to a throwaway router.
    - At `Build`, Web.Hosting rejects:
      - a non-singleton `IHttpFeature` registration;
      - a registration under a narrower type than `IHttpFeature`, which would never be stamped;
      - a disposable `IHttpFeature` registered as an instance or an implementation type.
    - A disposable feature that a factory produces does not exist until the pipeline is built, so it is rejected there, before the host starts. That covers the builder-template verbs and `AddFeature(factory)`.
    - Request-scoped services for handlers stay a separate future decision: a lazily created scope owned by the server, for a separate service type.
36. **Presize each exchange's feature collection (owner, 2026-10-09; #1381).** Dictionary resizes are almost all of today's stamping cost: 560 B at 4 features and 1,744 B at 16. The capacity constructor already exists. The other per-request lever, an allocation-free `Get<T>()` (#1337), stays in Stage 14.

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
