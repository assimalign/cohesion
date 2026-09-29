# SecretStoreCertificateAuthority

Namespace: `Assimalign.Cohesion.ApplicationModel`

Assembly: `Assimalign.Cohesion.SecretStore.ApplicationModel.Orchestration`

## Purpose

`IResourceCertificateAuthority` that issues endpoint TLS leaves from a SecretStore resource's
private authority. `ResourceKind` is `SecretStore`. Bind it with
`builder.UseSecretStore(store).AsCertificateAuthority()`.

## IssueAsync

`IssueAsync(request, authority, cancellationToken)` requests
`GET <control plane>/certificates?name=certs/<request.LeafName>` — the store creates the leaf on
first resolution and returns the same leaf afterwards — then `GET <control plane>/certificates?name=ca/root`,
and returns `ResourceCertificate(bundle, root)`.

The store chooses the leaf's subject alternative names (the leaf name, `localhost`, `127.0.0.1`,
`::1`); `request.SubjectAlternativeNames` is not forwarded. Honouring it is a recorded follow-up in
the project design.

## Exceptions

- `ArgumentNullException` — `request` or `authority` is `null`.
- `ArgumentException` — the leaf name is blank, or `authority` is not a SecretStore connection or
  carries an invalid address or credential.
- `HttpRequestException` — the store is unreachable or refuses.
- `InvalidDataException` — the store returns an empty certificate.

## Links

- [Assembly overview](../OVERVIEW.md)
- [Project design](../../../DESIGN.md)
