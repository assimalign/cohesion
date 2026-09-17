# Web developer-experience acceptance fixture

This SDK smoke project compiles the signed section 4.2.2 composition shape against the real Web,
Database client, authentication, health, routing, generated `Resource`, and hosting APIs.

Three design calls do not yet have matching runtime APIs and use the nearest available shape:

- `builder.Configuration.AddConfigurationStore(...)` is omitted because no configuration-store
  provider extension exists.
- The optional `Resource.References.IdentityHub.Https` and `JwtBearerOptions.Authority` calls use
  the resource's own `http` endpoint with `JwtBearerOptions.ValidIssuers`; no IdentityHub fixture
  or `Authority` property exists.
- `ISqlClient` has no `Get` or `List` query helpers, so the route handlers return deterministic
  placeholder responses while retaining the real SQL client composition and registration.
