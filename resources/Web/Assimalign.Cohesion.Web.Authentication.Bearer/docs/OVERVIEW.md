# Bearer authentication

`Assimalign.Cohesion.Web.Authentication.Bearer` validates JWT credentials through
IdentityModel and implements the Web.Authentication scheme contract. Configure
issuer, audience and signing keys through `auth.AddJwtBearer(options => ...)`;
the [project README](../README.md) shows registration.

`JwtBearerOptions.Realm` is an optional `HttpFieldValue`. String assignments are
checked immediately: every control character and leading/trailing SP or HTAB is
refused. A null string or empty value omits the realm parameter. Interior spaces
remain valid, and quotes and backslashes are escaped when emitted:

```csharp
options.Realm = "Example API";
string? configuredRealm = options.Realm?.Value;
```

The property changed from `string?` in 10.0.0-preview.1 and requires rebuilding
consumers. Invalid configuration now fails before a request, and quoted realms
retain their original characters. See [DESIGN.md](DESIGN.md) for the validation
order, challenge rules and compatibility details.
