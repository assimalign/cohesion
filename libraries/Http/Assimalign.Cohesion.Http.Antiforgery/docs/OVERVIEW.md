# HTTP antiforgery

`Assimalign.Cohesion.Http.Antiforgery` creates and validates signed cookie and
request-token pairs for cross-site request forgery protection. Create an
`IHttpAntiforgery` service with `HttpAntiforgery.Create(options)` and share it
across the application's requests. `GetAndStoreTokens(context)` issues the
pair; `IsRequestValidAsync(context)` checks a submitted pair.

The package reads the cookie through Http.Cookies and the request token from
the already-parsed Http.Forms collection. The first occurrence of the
configured form-token field is used. If it is absent or empty, the configured
request header supplies the token. An invalid nonempty first form token fails
validation; later form values are not tried. Form parsing must happen before
validation when using the form-token path.

This changes repeated-field behavior from 10.0.0-preview.1: multipart formerly
selected the last value, and urlencoded forms comma-joined the values. Both
encodings now select the first token.

See [DESIGN](DESIGN.md) for token protection, dependency direction, and Web
pipeline integration.
