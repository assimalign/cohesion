# SecretStoreResourceCommandExtensions

Extension members on `ISecretStoreResourceDescriptor` attach typed commands and return the same
descriptor for fluent chaining. Each method accepts `optional = false`; optional controls gateway
reconciliation behavior, without relaxing payload validation or ownership.

| Wire kind | Descriptor verb | Ownership key |
|---|---|---|
| `secretstore.add-secret` | `AddSecret` | secret path |
| `secretstore.issue-certificate` | `IssueCertificate` | certificate name |

AddSecret declarations carry only a source reference. `parameter:<name>` resolves through the
gateway's existing parameter provider before delivery. `<resource>:<key>` uses the existing store
resolver and requires a declared dependency and an available source endpoint. `literal:<value>`
is rejected during declaration construction: literal secret material never enters the desired
model, deterministic id or manifest. Only the transient delivery envelope contains resolved bytes;
the protected repository stores the value and source together. An unresolved source is a named
Rejected result. Original source-only commands remain the gateway's declaration ledger.

IssueCertificate honors the supplied subject and SAN set. An existing certificate with different
identity is rejected until deleted; renewal preserves its identity. Private key and leaf storage
reuse the existing protected CA repository.

The control plane also accepts `cohesion.trust.add`, which the SDK manifest deliberately does not
advertise. It is the gateway-owned trust channel through IGatewayStoreClient, never a Build-declared
application command. Trust keeps owner `issuer@subject`, POST-only behavior, empty 204 success,
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
