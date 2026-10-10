# Web area decisions

Architecture decisions for the Web area (`resources/Web/**`) that span more than one package. Each
one records the situation, the options weighed and what the decision costs. The HTTP/Web program
plan (`docs/programs/HTTP_WEB_PROGRAM_PLAN.md`, §7.4) lists them with the owner's other decisions.

- [ADR 1: How a rewrite changes the request for the rest of the pipeline](#adr-1-how-a-rewrite-changes-the-request-for-the-rest-of-the-pipeline)

## ADR 1: How a rewrite changes the request for the rest of the pipeline

- **Status:** Accepted, 2026-10-07.
- **Decided by:** the integrator, under the owner's standing delegation for the HTTP/Web program. It is reviewed at the end of Stage 10.
- **Records:** plan §7.4 decision 17, and the request-mutation gate on #782 (decision 6).

### Context

A rewrite middleware has to change the path and query that everything after it sees. `IHttpRequest` is immutable. Only the abstract `HttpRequest` base has setters, and using them means downcasting a transport's request, which the interface-first rule exists to prevent.

The Web area has two precedents, and they point different ways.

- **Effective values.** The original request stays as it is, and consumers opt in to reading the new values.
  - Forwarded headers publish `IHttpForwardedFeature`, and consumers read `context.EffectiveScheme`, `EffectiveHost` and the rest (decision 3).
  - Path branches publish `IWebPathBaseFeature` (Stage 6).
  - Today only nested `Map` and StaticFiles read the effective path. Routing matches `Request.Path` (`Route.cs`).
- **Request views.** Web.Compression and Web.RequestTimeouts hand the rest of the pipeline a wrapped request or context (`RequestDecompressionHttpRequest`, `RequestTimeoutMiddleware`), so every later reader sees the new value with no change on its side.

An effective-path design would work only if every reader migrated. Today that is 20 reads of `Request.Path` or `Request.Query` in 10 packages, plus the `context.Request.Query` reads the endpoint generator emits.

### Decision

1. **A rewrite hands the rest of the pipeline a request view.** The view's `Path` and `Query` are the rewritten values, and everything else delegates to the original request.
2. **The original values stay readable** through an `IWebRewriteFeature` (`OriginalPath`, `OriginalQuery`).
3. **Inside a `Map` branch,** the rewrite applies to the branch's effective path and publishes a matching `IWebPathBaseFeature`.
4. **A new package, `Assimalign.Cohesion.Web.Rewrite`** (resources/Web), provides `UseRewrite(rules => ...)`, which runs before `UseRouting`. Its rules run in order:
   - an internal rewrite, which continues the pipeline;
   - a redirect, with status 301, 302, 307 or 308 and a `Location`;
   - a regex rule over the path and query, which is interpreted or uses `RegexOptions.NonBacktracking`, never `Compiled`;
   - a predicate rule.

   It also provides canonicalization helpers built on `HttpPath` and `HttpHost` (HTTPS, `www` or non-`www`, trailing slash, lowercase), and bounds the number of rule passes so that rules cannot loop.
5. **A redirect ends the pipeline. A rewrite continues it.**
   - Telemetry and access logs that ran before the rewrite keep the original values.
   - Everything after the rewrite sees the rewritten path.
   - `http.route` is the route the rewritten path matched.

### Options considered

**A. A request view (chosen).**
- **Pros:**
  - Every existing reader is correct without migration, the generated binders included.
  - It matches what Web.Compression and Web.RequestTimeouts already do.
- **Cons:** a component that holds the original context across `next` sees the original values.

**B. An effective-path feature on every request, with every reader migrated.**
- **Pros:** it matches path branches and forwarded headers.
- **Cons:**
  - More than 20 call sites and the generator have to migrate, and every future middleware has to remember to read the feature.
  - A reader that is missed silently ignores the rewrite. If routing and static files disagreed on the path, that would be a security-relevant inconsistency.

**C. A settable path and query on a transport feature,** which #782's acceptance criteria first suggested.
- **Cons:**
  - It mutates shared transport state.
  - It breaks `IHttpRequest`'s immutability for every consumer.
  - It needs work in all three transports.

### Trade-off analysis

Effective values suit forwarded headers because forwarded headers change how the same request is *interpreted*: who sent it, and over which scheme. A rewrite changes *which resource* is requested, and every component after it has to agree on that. A view guarantees the agreement. Readers that opt in do not.

### Consequences

**Easier:** a rewrite composes with routing, static files, caching and the generated binders, with no change to any of them.

**Harder:** anything that captured the context before the rewrite sees the original values. That is intended for telemetry, but it is a trap for a middleware that keeps a context across `next`, so it is documented.

**Also:**
- Path branches keep their feature. Turning branches into views later is possible, but not required.
- **To revisit:** a shared redirect helper. HttpsPolicy, StaticFiles and Rewrite each write a `Location`, and they rebuild the query differently.

### Action items

1. **Build Web.Rewrite** with the request view, the rules and the canonicalization helpers.
2. **Test it through routing and static files.**
3. **Write its docs.**
4. **Wire it** into App.Web, CI and the release inventory, and add a NativeAOT guard check.
