# HTTP / Web Program Plan

**Status:** active · **Created:** 2026-07-03 · **Owner:** Chase Crawford · **Scope:** the HTTP protocol stack (`libraries/Http*`), its cross-area foundations (`libraries/Connections`, `libraries/Security`, `libraries/Hosting`), and the Web resource (`resources/Web/*`, GitHub Web Platform epic **#6 / L03.01**).

> **Why this file exists.** This program spans ~50 GitHub work items across 8 epics and will be implemented by many separate AI coding sessions. No single session holds the whole picture in context. This document is the **durable sequencing index**: it records what depends on what, what is safe to do in parallel, and the protocol each session follows so the work scales out **without losing the order things must happen in**. GitHub issues hold the *what* and *acceptance criteria*; this file holds the *when* and *in-what-order*. It is a living doc — update the Progress Log and check items off as PRs merge.

This file is temporary scaffolding for the duration of the program. When the Web resource is assembled and this backlog is drained, fold anything durable into the relevant `docs/DESIGN.md` files and delete this doc.

---

## 1. How to run this across multiple sessions (read first)

The safe unit of work is **one GitHub issue = one session = one branch = one PR**. Do not batch unrelated issues into a session; they will collide and the sequencing breaks.

**The session protocol (every session follows this):**

1. **Pick an issue that is unblocked.** An issue is workable only if every entry in its *Blocked by* column (§4) is merged. Never start a blocked issue — its prerequisites define types/seams you would otherwise invent and later fight.
2. **Read three things before coding:** (a) the issue body and its acceptance criteria; (b) this plan's row for the issue in §4 and the lane guardrails in §3; (c) `AGENTS.md` + the area's `docs/DESIGN.md`. Invoke the `cohesion-coding-rules` skill at the start.
3. **Branch:** `feature/<wbs>-<slug>` naming the issue's WBS (e.g. `feature/L03.01.01.05-problem-details`). The `cohesion-work-items` skill infers scope-creep placement from this branch.
4. **Implement to the acceptance criteria.** If you discover out-of-scope work, file it with the `cohesion-work-items` skill (don't expand the current issue) and add a row to §4 here if it changes sequencing.
5. **Open a PR** with the `Closes #NNNN` block (use `New-CohesionWorkItem.ps1 -EmitClosesBlock` from the same worktree). Close the parent feature manually only when all its children are done.
6. **Update this file:** move the issue to Done in the §5 Progress Log with the PR link and date. This is how the *next* session knows it's unblocked. Commit that doc change with the PR.

**Golden rule for parallelism:** issues in different **lanes** (§3) at the same **stage** (§2) can run concurrently in separate sessions with no coordination. Two sessions in the *same* lane touching the same project should be serialized — check the Progress Log for an in-flight sibling before starting.

### How to reference this plan when you prompt a session (avoiding confusion)

Reference **by issue number + this file path**, and let the plan tell the session what to do. Do **not** paste the whole plan into the prompt or say "work on the HTTP stuff" — that reintroduces the ambiguity this file removes.

**Recommended prompt template (copy/paste, fill the number):**

```
Work GitHub issue #NNNN in assimalign/cohesion.

Before coding, read docs/HTTP_WEB_PROGRAM_PLAN.md — follow the Session Protocol
in §1, confirm the issue is unblocked per §4, and honor the lane guardrails in §3.
Invoke the cohesion-coding-rules skill. Branch, implement to the issue's acceptance
criteria, open a PR that closes it, and update the Progress Log (§5) in the same PR.

Do not start any work its "Blocked by" prerequisites haven't merged; if it's blocked,
stop and tell me which prerequisite is outstanding.
```

**Variations:**
- *Let the session choose:* replace the first line with `Pick the highest-priority unblocked issue from Stage <N>, Lane <X> in docs/HTTP_WEB_PROGRAM_PLAN.md and work it.` Good when you don't want to micromanage ordering.
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
| **F — Routing & API surface** | endpoints, binding, results | `...Web.Routing`, `...Web.Api`, `...Web.Functions`, `...Web.Results`, `analyzers/...SourceGeneration.Web` | Endpoint **metadata bag (#150)** is the seam auth/CORS/OpenAPI/docs consume — get it right early; AOT mandates source-gen for binding, never reflection. |

Cross-cutting rules (all lanes): file-scoped namespaces; `CohesionProjectReference`/`CohesionPackageReference`; **no `Microsoft.Extensions.*`**; `IsAotCompatible=true`, no reflection; interface-first with internal impls; XML docs on public APIs; Shouldly tests co-located; create/update `docs/DESIGN.md` in the same change. `AGENTS.md` is canonical; the `cohesion-coding-rules` skill re-anchors it.

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
| #753 | A/B | RFC 9218 extensible priorities | #747 |
| #756 | B | RFC 9530 Digest Fields | #747 |
| #746 | B | RFC 10008 HTTP QUERY method semantics | #747, #755 |
| #754 | A | Alt-Svc advertisement (RFC 7838) | — |
| #776 | E | Pipeline exception boundary + RFC 9457 ProblemDetails | #762 |
| #777 | E | `Web.StaticFiles` over the FileSystem library | #762, #792, #771 |
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
| #149 | F | Result writers + content negotiation (existing) | #771 |
| #789 | F | Typed route values, constraints, per-app router state | #148 |

### Stage 3 — Composition

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| #767 | D | HTTP/3 (QUIC) registration surface on the Web builder | #762, #763, #748, #749 |
| #795 | E | `Web.Caching` (server-owned output caching) | #762, #755, #792 |
| #782 | E | `Web.Rewrite` (URL rewriting/redirects) | #762 + request-mutation seam decision (#24/#25) |
| #775 | C/E | Health-check framework + `/healthz` endpoint | #762 (endpoint half), host-lifecycle epics |
| #786 | F | Route groups (MapGroup) | #148, #150 |
| #787 | F | Named routes + LinkGenerator | #148 |
| #788 | F | Host-based route matching (RequireHost) | #150 |
| #765 | A/E | WebSockets decision + RFC 6455 (if "build") | #751 (h1), #748 (h3 via #382); supersedes #380–#382 |

### Stage 4 — Surface

| Issue | Lane | Title | Blocked by |
|---|---|---|---|
| #796 | F | Source-generated endpoint binding + validation (AOT) | #150, #149, #771 |
| #790 | F | Auth scheme model + Cookie/Bearer handlers | #774, #150, IdentityModel #610 |
| #151 | F | Controller/action + function endpoint binding (existing) | routing primitives, #796 |

### Deliberately NOT in this program (ADR-gated)
Real-time hub framework (SignalR-analogue) and gRPC hosting are **decisions, not features** — each needs an ADR first (real-time gates on the #765 WebSocket outcome; gRPC gates on a protobuf-vs-code-first serialization decision). Do not start either without a recorded decision. Skipped entirely: IIS/HTTP.sys, OWIN, SPA dev-proxy, Razor/Blazor UI, request localization.

---

## 5. Progress Log (update as PRs merge)

Move an item here with its PR link + date the moment it merges — this is the signal that its dependents are unblocked.

| Date | Issue | PR | Notes |
|---|---|---|---|
| 2026-07-03 | #759 | [#798](https://github.com/assimalign/cohesion/pull/798) | Stage 0 (Lane B). Retired the `Assimalign.Cohesion.Http.Identity` skeleton — deleted the directory and removed its entries from all three solution files. Restores the commit-481a6fb layering invariant: no `System.Security.Claims`-typed public surface in the `Assimalign.Cohesion.Http` protocol core. Decision recorded in `resources/Web/Assimalign.Cohesion.Web.Authentication/docs/DESIGN.md`: no `request.User` accessor absorbed (redundant with the existing `context.User`; the skeleton's `ClaimsPrincipal.Current` fallback deliberately not carried over). |

---

## 6. Fast reference

- **Epics:** Http `#314` (L01.01.11) · Net/Connections `#324` (L01.01.14) · Security `#325` (L01.01.18) · Hosting `#313` (L01.01.10) · Web Platform `#6` → Runtime/Pipeline `#24`/`#25`/`#26`, API/Tooling `#27`, Routing `#28`, Security/Browser `#2`/`#3`/`#30`.
- **Skills:** `cohesion-coding-rules` (start of every session) · `cohesion-work-items` (file scope-creep, emit PR close blocks).
- **Canonical rules:** `AGENTS.md` (repo root). **Roadmap context:** `docs/DELIVERY_ROADMAP.md`.
- **This program's north star:** assemble the Web resource by wiring the `libraries/Http` stack into `resources/Web`; once assembled, the next major effort is pulling the new ApplicationModel design together (`libraries/ApplicationModel/DESIGN.md`).
