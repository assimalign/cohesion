# Assimalign.Cohesion.ConfigurationStore.Client

## Summary

This project provides the small client surface used to read nullable string values from a named
ConfigurationStore namespace and to submit declarative command envelopes. It is independent of the
ConfigurationStore hosting runtime and is delivered as a standalone NuGet package.

## Public surface

- `IConfigurationStoreClient` exposes asynchronous namespace reads and command submission.
- `ConfigurationStoreClient` creates a client for an endpoint `Uri` and credential.
- `ClientCredential` carries an opaque Bearer token and redacts it when formatted.
- `ResourceCommand` carries the id, kind, owner, key, and payload bytes sent to the control plane.

## Behavior

`GetNamespaceAsync` issues an authenticated GET request and deserializes the response as a direct
JSON object into `IReadOnlyDictionary<string, string?>`. `SendCommandAsync` issues an authenticated
POST request with a camel-case JSON command envelope. Both operations preserve an endpoint base path,
escape their query values, propagate cancellation, and reject non-success status codes.

`ConfigurationStoreClient.Create` accepts a Cohesion endpoint `Uri`: an absolute URI with a host and
valid port, without user information, a query, or a fragment. It additionally restricts the scheme
to HTTP or HTTPS before creating the internal transport client.

## Links

- [Package README](../README.md)
- [Design](./DESIGN.md)
- [Assembly reference](./Assembly/Assimalign.Cohesion.ConfigurationStore.Client/OVERVIEW.md)
