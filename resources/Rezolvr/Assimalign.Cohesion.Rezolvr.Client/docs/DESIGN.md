# Assimalign.Cohesion.Rezolvr.Client design

## Design intent and family boundary

The command client implements the generic item-23b resource protocol for Rezolvr. The public
surface is interface-first, with an internal HTTP implementation. Its own command and observation
value types keep consumers independent of Hosting.Resources and of other resource clients.

Dependencies point from ApplicationModel.Gateway to this client, and from this client to Core.
The area ApplicationModel and Hosting packages never reference a gateway implementation.
ApplicationModel and Client remain NuGet-only; the runtime framework contains Hosting separately.

## Protocol and ownership

POST and DELETE target `<controlPlaneAddress>/commands`. Requests carry
`Authorization: Bearer <credential>` and JSON `{id,kind,owner,key,payload}`, where `payload` is a
base64 string. The owner and key are sent unchanged. The serving control plane controls ownership,
replay, idempotency and deletion; the client never invents an identity or retries a mutation.

A successful request returns handler bytes with HTTP 200 `application/octet-stream`. The current
Rezolvr handlers return an empty body. Refusals return JSON `{status,detail}`: conflict is 409,
unsupported kind is 501 and malformed arguments are 400. The observation preserves a refusal's
detail; an HTTP failure without an observation receives a named HTTP fallback detail.

## Lifecycle and transport

Each factory-created client owns an HttpMessageInvoker and its SocketsHttpHandler, and is disposable. Factory creation is synchronous;
I/O starts only in a command method. Cancellation tokens flow through send and response reads.
Redirects and cookies are disabled to keep the bearer credential on its requested endpoint.
The factory accepts guarded HTTP(S) addresses; production transport policy belongs to the host.

## Error model

Invalid factory and command arguments throw argument exceptions before delivery. Valid server
refusals are observations, not transport exceptions. Socket failures and cancellation propagate.
The gateway adapter maps Applied or Deleted to ResourceCommandStatus.Applied and all refusal
statuses to Rejected, while retaining detail. No shared contract assembly is introduced.

## AOT and extension points

The net10.0 package inherits IsAotCompatible=true from the repository build. Envelopes are written
with Utf8JsonWriter; observations are parsed with JsonDocument. There is no reflection serializer,
assembly scanning, runtime code generation, DI activation or Microsoft.Extensions dependency.
Add a new domain command in the area's ApplicationModel and Hosting, advertise its wire kind in
the SDK manifest, and retain this generic wire envelope. A new client protocol needs a reviewed
public interface change and a loopback test against the actual endpoint.

## Non-goals

The package does not host services, store domain state, issue credentials, resolve mount sources,
serve DNS, or implement identity token issuance. Those behaviors remain with the owning areas.
