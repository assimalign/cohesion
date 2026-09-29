# SecretStoreAddSecretInputResolver

Namespace: `Assimalign.Cohesion.ApplicationModel`

Assembly: `Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration`

## Purpose

`IResourceCommandInputResolver` for `secretstore.add-secret` (`CommandKind`). It turns the declared,
secret-free payload into the payload the store applies, at delivery time. `UseSecretStore` registers
one instance.

## ResolveAsync

`ResolveAsync(declared, sources, cancellationToken)` reads the declared `source`, resolves it
through the gateway's `IResourceSourceResolver` as a `Secret` value, and returns
`{"path":<command key>,"source":<source>,"resolvedValue":<base64>}`.

Accepted sources are `parameter:<name>` and `<resource>:<key>` (exactly one `:`, both sides
nonblank). The resolved bytes appear only in the returned payload, never in the model.

## Refusals

A refusal is an `InvalidOperationException` whose message the gateway records as the command's
rejection detail:

- `secretstore.add-secret requires a nonblank source.`
- `secretstore.add-secret sources must use parameter:<name> or <resource>:<key>; literal sources are forbidden.`
- `secretstore.add-secret parameter '<name>' is not bound for application '<owner>'.`
- `secretstore.add-secret source '<source>' is unresolved: <reason>`

`ArgumentNullException` / `ArgumentException` report a `null` argument or a command of another kind;
`JsonException` reports a declared payload that is not JSON.

## Links

- [Assembly overview](../OVERVIEW.md)
- [Project design](../../../DESIGN.md)
