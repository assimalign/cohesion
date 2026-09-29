# SecretStoreTrustedIssuerStore

Namespace: `Assimalign.Cohesion.ApplicationModel`

Assembly: `Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration`

## Purpose

`ITrustedIssuerStore` that persists an application's trusted peer issuers in a SecretStore resource.
`ResourceKind` is `SecretStore`. Bind it with `builder.UseSecretStore(store).AsTrustStore()`.

## Members

- `ReadAsync(store, cancellationToken)` — `GET <control plane>/secrets?path=trusted-issuers.json`
  and parses `{"issuers":[{"issuer","trustKey","allowedCommandKinds"?}]}`. Returns `null` when the
  store answers `404` (nothing persisted yet).
- `AddAsync(store, owner, issuer, cancellationToken)` — `POST <control plane>/commands` with a
  `cohesion.trust.add` command: key = issuer name, payload = the public JWK (or
  `{"trustKey":...,"allowedCommandKinds":[...]}` for a restricted grant), id = `trust-` + lowercase
  hex SHA-256 of `owner\nissuer\n` and the payload. The store accepts the grant only when `owner`
  is the authenticated caller's `<application>@<subject>`.

## Exceptions

- `ArgumentNullException` — `store` or `issuer` is `null`.
- `ArgumentException` — `owner` is blank, or `store` is not a SecretStore connection or carries an
  invalid address or credential.
- `HttpRequestException` — the store is unreachable or refuses (for a grant, `403` for another
  owner and `409` when another owner holds the issuer); `ReadAsync` maps `404` to `null`.
- `JsonException` / `InvalidDataException` — the exported document is not JSON or is malformed.

## Links

- [Assembly overview](../OVERVIEW.md)
- [Project design](../../../DESIGN.md)
