# Assimalign.Cohesion.SecretStore.Client

## Purpose

The namespace contains the gateway-side SecretStore protocol contract and factory.

## Types

- `ISecretStoreClient` exposes asynchronous secret, certificate, and generic command operations.
- `SecretStoreClient` creates the internal HTTP implementation from a `Uri` and
  `ClientCredential`.
- `ClientCredential` carries an opaque bearer token and redacts its formatted representation.
- `ResourceCommand` carries an id, kind, owner, key, and payload bytes serialized as base64 JSON.

All asynchronous operations accept an optional `CancellationToken`. Invalid names are rejected
before transport I/O, non-success HTTP responses surface as `HttpRequestException`, and a missing
certificate body surfaces as `InvalidDataException`.
