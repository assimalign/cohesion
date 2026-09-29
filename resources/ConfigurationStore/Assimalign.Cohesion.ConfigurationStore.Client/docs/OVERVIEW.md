# Assimalign.Cohesion.ConfigurationStore.Client

## Summary

This project provides the small client surface used to list ConfigurationStore namespaces, read
nullable string values from a named namespace, and submit declarative command envelopes. It is
independent of the ConfigurationStore hosting runtime and is delivered as a standalone NuGet package.

## Public surface

- `IConfigurationStoreClient` exposes asynchronous namespace listing, namespace reads, and command
  submission.
- `ConfigurationStoreClient` creates a client for an endpoint `Uri` and credential.
- `ClientCredential` carries an opaque Bearer token and redacts it when formatted.
- `ResourceCommand` carries the id, kind, owner, key, and payload bytes sent to the control plane.

## Behavior

`ListNamespacesAsync` issues an authenticated GET request and deserializes the direct JSON string
array into `IReadOnlyList<string>`. `GetNamespaceAsync` issues an authenticated GET request and
deserializes the direct JSON object into `IReadOnlyDictionary<string, string?>`. `SendCommandAsync`
issues an authenticated POST request with a camel-case JSON command envelope. These existing operations preserve
an endpoint base path, propagate cancellation, and reject non-success status codes; the named read
also query-escapes its namespace name.

`ConfigurationStoreClient.Create` accepts a Cohesion endpoint `Uri`: an absolute URI with a host and
valid port, without user information, a query, or a fragment. It additionally restricts the scheme
to HTTP or HTTPS before creating the internal transport client.

## Links

- [Package README](../README.md)
- [Design](./DESIGN.md)
- [Assembly reference](./Assembly/Assimalign.Cohesion.ConfigurationStore.Client/OVERVIEW.md)


## Declarative commands

ObserveCommandAsync and DeleteCommandAsync return ResourceCommandObservation (Status and Detail),
including structured provider refusals from non-success HTTP statuses. Existing SendCommandAsync
retains its Task and EnsureSuccessStatusCode behavior. CreateForControlPlane accepts the full manifest
control-plane URI and makes those observation methods append only /commands, including custom paths.
The ordinary Create factory continues appending the existing /cohesion/v1 routes to its resource
endpoint base path. JSON parsing is explicit and transport cancellation remains caller-controlled.
