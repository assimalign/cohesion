# IdentityHubResourceCommandExtensions

Extension members on `IIdentityHubResourceDescriptor` attach typed commands and return the same
descriptor for fluent chaining. Each method accepts `optional = false`; optional controls gateway
reconciliation behavior, without relaxing payload validation or ownership.

| Wire kind | Descriptor verb | Ownership key |
|---|---|---|
| `identityhub.add-audience` | `AddAudience` | audience name |
| `identityhub.add-client` | `AddClient` | client id |

The typed IIdentityHubResourceDescriptor retains its command surface through DependsOn chaining.
Audience and client commands are registered on the default control plane and served by the existing
authenticated API endpoint, with POST/DELETE and JSON refusal observations. Owner must equal the
authenticated application issuer; transport and bootstrap-token verification are unchanged.

The command registry is written atomically to `registry.json` beside IdentitySigningKey under the
resource data path. Startup restores the registry and control-plane ownership before serving.
Token issuance reads the live combined registry of builder-declared and command-declared clients;
discovery and JWKS remain the same issuer surface. Audience removal is rejected while a client uses it.

Client credentialSource names a resource Secret mount, optionally prefixed with `mount:`. The
declaration stores the mount name, never credential bytes. Hosting reads and hashes the mounted
credential when initializing or updating the registry, so restart requires the mount again.
Clients must reference existing audiences; add the audience before the client. Conflicting resource
seeds or changed client declarations are rejected until the owning declaration is deleted.

Blank required strings, invalid single-segment keys and malformed argument values throw argument
exceptions naming the offending parameter. Build rejects unadvertised kinds or duplicate target
keys through ResourceCommandValidator. Serialization uses the internal generated JSON context.
