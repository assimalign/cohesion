# Assimalign.Cohesion.IdentityHub

## Summary

The IdentityHub area root. It defines the two seams the rest of the area builds against:

- **The application seam**: the code-first composition and lifecycle contracts of an IdentityHub
  resource. The concrete issuer is supplied by `Assimalign.Cohesion.IdentityHub.Hosting`.
- **The store seam**: `IIdentityHubStore`, the one contract between the hub and the data layer
  that keeps its state. The hub depends on no concrete store.

The reasoning behind both is in [DESIGN.md](./DESIGN.md).

## Dependencies

`Assimalign.Cohesion.Core`, `Assimalign.Cohesion.IdentityModel`, and
`Assimalign.Cohesion.IdentityHub.Models`. No `Assimalign.Cohesion.Hosting*` library (COHRES004) and
never `Assimalign.Cohesion.IdentityHub.Hosting` (COHRES001).

## Public surface

### Application seam

- `AddAudience(string)` declares an exact access-token audience.
- `AddClient(string, Action<IdentityHubClientOptions>)` registers client credentials, device authorization, token lifetime, and allowed audiences.
- `Build()` produces an `IIdentityHubApplication`, which exposes `Context`, `StartAsync`, and `StopAsync`.

At least one grant and one declared audience are required for every registered client. The code-first registration verbs ship here, and Hosting issues tokens for the clients they register.

### Store seam

| Type | Role |
| --- | --- |
| `IIdentityHubStore` | The contract: `FindAsync`, `QueryAsync`, `TryAddAsync`, `TryReplaceAsync`, `TryRemoveAsync` over named record sets |
| `IdentityHubStore` | The guided base a store derives from. It refuses `default` and `null` arguments and observes cancellation before the store's own code runs |
| `InMemoryIdentityHubStore` | The reference store: for tests, and for a hub on one node that may forget everything on restart |
| `IIdentityHubRecordCodec<TRecord>` | Written by the package that owns a value: names the record set and maps the value to and from a record |
| `IdentityHubRecordSet<TRecord>` | The typed view hub code holds: a store and a codec, with `FindByIndexAsync` for unique lookups |
| `IdentityHubRecord`, `IdentityHubStoredRecord`, `IdentityHubRecordIndex` | A record (key, opaque payload, optional expiry, index entries), a record with the version the store issued, and one index entry |
| `IdentityHubRecordQuery`, `IdentityHubRecordPage` | Every record of a set or index equality, a page at a time with an opaque continuation |
| `IdentityHubEntry<TRecord>`, `IdentityHubEntryPage<TRecord>` | The decoded forms a record set returns |
| `IdentityHubWriteResult`, `IdentityHubWriteStatus` | The outcome of a conditional write: `Written` with the new version, or `KeyConflict`, `VersionConflict`, `IndexConflict` |
| `IdentityHubRecordSetName`, `IdentityHubRecordIndexName`, `IdentityHubRecordKey`, `IdentityHubRecordVersion` | Generated value objects; each carries its grammar in `IsValid` |
| `IdentityHubException`, `IdentityHubStoreException`, `IdentityHubErrorCode` | The area's exception root, the store's exception, and the stable error codes |

Every write is conditional and a refused write is a result, never an exception. That is how the
hub enforces its rules without locks: a device code is redeemed by removing its record at the
version that was read, and of two concurrent redemptions the store grants exactly one.

```csharp
IIdentityHubStore store = new InMemoryIdentityHubStore();
IdentityHubRecordSet<DeviceGrant> grants = IdentityHubRecordSet<DeviceGrant>.Create(store, new DeviceGrantCodec());

IdentityHubWriteResult added = await grants.TryAddAsync(grant, cancellationToken);
IdentityHubEntry<DeviceGrant>? found = await grants.FindAsync(grant.DeviceCodeDigest, cancellationToken);
bool redeemed = found is not null && await grants.TryRemoveAsync(found.Key, found.Version, cancellationToken);
```

Keys and index values are never secrets, and a payload never holds a secret in the clear: a bearer
value is hashed before it becomes a key (DESIGN.md, "Security rules").

### What is not here yet

Nothing consumes the store seam today. `IdentityHub.Hosting` still keeps its device
authorizations, signing key, and clients in its own internal types, and the builder has no verb
for a store. Durable stores are separate packages that do not exist yet. Both are tracked as
follow-up work items, and DESIGN.md lists the decisions that are still open, tenancy in the record
address first among them.

## Hosting-free application contract (O34)

The root contracts are hosting-free (O34): `IIdentityHubApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IIdentityHubApplicationContext` exposes `ContentRootPath`. `IIdentityHubApplicationBuilder` owns area declarations and `Build()`. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`IdentityHubApplication.CreateBuilder(args)` returns the public concrete `IdentityHubApplicationBuilder`; its `Build()` returns the public `IdentityHubApplication : Host<IdentityHubApplicationContext>`. The public `IdentityHubApplicationContext` implements `IIdentityHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
