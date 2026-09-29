# SecretStoreSourceProvider

Namespace: `Assimalign.Cohesion.ApplicationModel`

Assembly: `Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration`

## Purpose

`IResourceSourceProvider` for `<store>:<key>` Secret mounts backed by a SecretStore resource of the
application. `ResourceKind` is `SecretStore`.

## Members

- `ReadSecretAsync(request, cancellationToken)` — `GET <control plane>/secrets?path=<key>`;
  returns the bytes.
- `ReadCertificateAsync(request, cancellationToken)` —
  `GET <control plane>/certificates?name=<key>`, then `GET <control plane>/certificates?name=ca/root`;
  returns `ResourceCertificate(bundle, root)`. A `certs/<name>` key makes the store issue the leaf
  on first resolution.
- `ReadConfigurationAsync` — not supplied; the interface default throws `NotSupportedException`.

Each call uses `request.Store` (the gateway's connection to the store), presents its bearer
credential, validates the store's TLS certificate with its validator, and disposes its transport
when done.

## Exceptions

- `ArgumentNullException` — `request` is `null`.
- `ArgumentException` — `request.Store` is `null` or not a SecretStore, the key is blank, or the
  address or credential is invalid.
- `NotSupportedException` — `request.Kind` is not `Secret`.
- `HttpRequestException` — the store is unreachable or refuses; `StatusCode` carries its status.
- `InvalidDataException` — the store returns an empty certificate.

## Links

- [Assembly overview](../OVERVIEW.md)
- [Project design](../../../DESIGN.md)
