# Assimalign.Cohesion.ConfigurationStore.Client

## Summary

This project provides the small client surface used to list ConfigurationStore namespaces and read
nullable string values from a named namespace. It is independent of the ConfigurationStore hosting
runtime and is delivered as a standalone NuGet package.

## Public surface

- `IConfigurationStoreClient` exposes asynchronous namespace listing and namespace reads.
- `ConfigurationStoreClient` creates a client for an endpoint `Uri` and credential.
- `ClientCredential` carries an opaque Bearer token and redacts it when formatted.

## Behavior

`ListNamespacesAsync` issues an authenticated GET request and deserializes the direct JSON string
array into `IReadOnlyList<string>`. `GetNamespaceAsync` issues an authenticated GET request and
deserializes the direct JSON object into `IReadOnlyDictionary<string, string?>`. Both preserve an
endpoint base path, propagate cancellation, and reject non-success status codes; the named read
also query-escapes its namespace name.

`ConfigurationStoreClient.Create` accepts a Cohesion endpoint `Uri`: an absolute URI with a host and
valid port, without user information, a query, or a fragment. It additionally restricts the scheme
to HTTP or HTTPS before creating the internal transport client.

The client reads; it changes no store state (see [Design](./DESIGN.md)).

## Links

- [Package README](../README.md)
- [Design](./DESIGN.md)
- [API reference (cohesion-docs)](https://github.com/assimalign/cohesion-docs)
