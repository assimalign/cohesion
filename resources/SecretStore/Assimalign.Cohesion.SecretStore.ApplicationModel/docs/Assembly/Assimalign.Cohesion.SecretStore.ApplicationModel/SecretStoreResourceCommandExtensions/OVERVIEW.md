# SecretStoreResourceCommandExtensions

Extension members on `ISecretStoreResourceDescriptor` attach typed commands and return the same
descriptor for fluent chaining. Each method accepts `optional = false`; optional controls gateway
reconciliation behavior, without relaxing payload validation or ownership.

| Wire kind | Descriptor verb | Ownership key |
|---|---|---|
| `secretstore.add-secret` | `AddSecret` | secret path |
| `secretstore.issue-certificate` | `IssueCertificate` | certificate name |

AddSecret declarations carry only a source reference. The gateway resolves it before delivery only
through a registered `secretstore.add-secret` input resolver, normally the one `UseSecretStore(...)`
registers (`SecretStore.ApplicationModel.Orchestration`): `parameter:<name>` from the application's
parameter bindings, `<resource>:<key>` through the source provider registered for that store, which
requires a declared dependency and an available source endpoint. `literal:<value>`
is rejected during declaration construction: literal secret material never enters the desired
model, deterministic id or manifest. Only the transient delivery envelope contains resolved bytes;
the protected repository stores the value and source together. An unresolved source is a named
Rejected result. Original source-only commands remain the gateway's declaration ledger.

The SecretStore manifest marks `secretstore.add-secret` as requiring an input resolver, so an
application that calls `AddSecret` without registering one fails `IApplicationBuilder.Build()` (a
set member fails at set start), naming the
`Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration` package and
`builder.UseSecretStore(...)`, before anything is delivered. The store's refusal of an unresolved
add-secret remains as defense in depth. `IssueCertificate` is not flagged and is still delivered as
declared when no resolver is registered.

IssueCertificate honors the supplied subject and SAN set. An existing certificate with different
identity is rejected until deleted; renewal preserves its identity. Private key and leaf storage
reuse the existing protected CA repository.

The control plane also accepts `cohesion.trust.add`, which the SDK manifest deliberately does not
advertise. It is the gateway's trust-store channel, sent only by the `SecretStoreTrustedIssuerStore`
provider a gateway registers with `builder.UseSecretStore(store).AsTrustStore()`
(`SecretStore.ApplicationModel.Orchestration`), never a Build-declared application command. Trust keeps owner `issuer@subject`, POST-only behavior, empty 204 success,
empty 409 conflict and existing 403 authorization refusals. New commands use owner `issuer`, accept
POST and DELETE, return 200 application/octet-stream on success, and JSON `{status,detail}` refusals.
The client accepts empty successful responses as Applied (Deleted for DELETE); legacy SendCommandAsync
continues to work. This owner split lets local gateway declarations authenticate end to end.

Restricted trust grants accept `{trustKey,allowedCommandKinds}` while unrestricted grants retain
the bare JWK payload. The protected trust document round-trips the optional string array; absent
or empty means every command kind is allowed. Enroll(platformStore) is deferred to item 31t:
automatic Platform enrollment needs a gateway-owned mediator and Platform-audience signer.

Blank required strings, invalid single-segment keys and malformed argument values throw argument
exceptions naming the offending parameter. Build rejects unadvertised kinds or duplicate target
keys through ResourceCommandValidator. Serialization uses the internal generated JSON context.
