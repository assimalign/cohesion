# ISecretStore

Namespace: `Assimalign.Cohesion.SecretStore`
Assembly: `Assimalign.Cohesion.SecretStore`

## Purpose

`ISecretStore` is the common disposable lifetime boundary for an opened secret store. Feature
contracts add storage, versioning, policy, and certificate operations without depending on the
Hosting implementation.

## Surface

- `Dispose()` releases resources owned by the opened store.

The root interface intentionally carries no protocol or persistence operations. Those belong to
the feature contract that owns them.
