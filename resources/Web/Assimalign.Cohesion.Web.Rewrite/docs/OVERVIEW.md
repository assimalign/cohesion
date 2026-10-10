# Assimalign.Cohesion.Web.Rewrite — Overview

URL rewriting and redirect rules for the Cohesion Web pipeline (issue #782). A Cohesion application serves
its own traffic, with no IIS or nginx rewrite tier assumed in front of it, so canonical URLs, legacy-path
migration and internal rewrites have to be expressible in the application. `UseRewrite(rules => ...)`
registers code-first rules that run, in order, ahead of static files and routing.

## Scope

- **Internal rewrites.** A rewrite changes the path and query the rest of the pipeline sees, without telling
  the client. The request itself is never mutated: the rest of the pipeline receives a request view whose
  `Path` and `Query` are the rewritten values, and `IWebRewriteFeature` keeps the originals readable (Web
  ADR 1). Routing, static files and the generated endpoint binders follow the rewrite with no change.
- **Redirects.** A redirect answers `301`, `302`, `307` or `308` with a `Location` and ends the pipeline.
- **Rule kinds.** Regular-expression rules over the path, or the path and query, with `$1`/`${name}`
  substitutions; predicate rules; delegate rules; and custom `IRewriteRule` implementations. String
  patterns run interpreted with a match timeout; a caller-supplied `Regex` can be `NonBacktracking` or a
  source-generated `[GeneratedRegex]`. `RegexOptions.Compiled` is never used.
- **Canonicalization helpers.** Redirects to HTTPS, to `www.` or away from it, to a trailing slash or away
  from it, and to a lowercase path. They read the effective scheme and host, so they work behind a
  TLS-terminating proxy that `UseForwardedHeaders` trusts, and they are idempotent.
- **Flow and loop protection.** After a rewrite, evaluation continues with the next rule, skips the
  remaining rules, or restarts from the first rule. Restarts are bounded by `MaxPasses`.
- **Path branches.** Inside `Map(path)`, the rules see and rewrite the path below the branch's prefix, and
  redirect targets are relative to it.

## Dependencies

- `Assimalign.Cohesion.Web`: the pipeline builder and the middleware abstraction.
- `Assimalign.Cohesion.Web.Routing`: the path-branch view (`IWebPathBaseFeature`,
  `context.GetEffectivePath()`), which moved there with `Map(path)` (#1379). The rewrite still runs ahead of
  routing and uses none of the router's types.
- `Assimalign.Cohesion.Http`: the HTTP context, the `HttpPath`, `HttpHost` and query value objects, and the
  parsing the transports use for a request target.
- `Assimalign.Cohesion.Http.Forwarded`: the effective scheme and host the canonicalization helpers read.

It never references `Assimalign.Cohesion.Web.Hosting` or any `Assimalign.Cohesion.Hosting*` library
(`COHRES001`, `COHRES004`).

## Usage

```csharp
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web.Rewrite;

app.UseForwardedHeaders(...);   // behind a proxy, so the helpers read the client's scheme and host
app.UseHostFiltering(...);

// Ahead of UseStaticFiles and UseRouting.
app.UseRewrite(rules => rules
    // Redirects first: a redirect built after a rewrite would send the client to the rewritten URL.
    .AddRedirectToHttps()
    .AddRedirectToNonWww()
    .AddRedirect("^/blog/(\\d+)/(.*)$", "/posts/$2", HttpStatusCode.MovedPermanently)
    .AddRedirect("^/item\\.php\\?id=(\\d+)$", "/items/$1?", HttpStatusCode.MovedPermanently, RewriteMatchTarget.PathAndQuery)

    // Internal rewrites: the client keeps its URL.
    .AddRewrite("^/assets/v\\d+/(.*)$", "/$1")
    .AddRewrite("^/products/(\\d+)$", "/product?id=$1")
    .AddRewrite(context => context.HttpContext.Request.Headers.ContainsKey("X-Mobile"), "/mobile"));

app.UseStaticFiles();
app.UseRouting();
app.MapGet("/product", (int id) => ...);   // binds the rewritten query
```

Inside an endpoint, `context.Request.Path` is the rewritten path, and the URL the client sent is
`context.Features.Get<IWebRewriteFeature>()?.OriginalPath`.

See [`DESIGN.md`](DESIGN.md) for the request view and the pitfall it carries, path branches, rule evaluation
and loop protection, the target syntax and its encoding model, redirects, the canonicalization helpers,
ordering, telemetry, the error model and the non-goals.
