# Assimalign.Cohesion.IdentityHub

## Summary

The IdentityHub area root. IdentityHub is a code-first identity server: its identity objects are
defined in code, and the same objects can be created at run time through an API. The root defines
the two seams the rest of the area builds against, and the first of those objects:

- **The application seam**: the code-first composition and lifecycle contracts of an IdentityHub
  resource. The concrete issuer is supplied by `Assimalign.Cohesion.IdentityHub.Hosting`.
- **The store seam**: `IIdentityHubStore`, the one contract between the hub and the data layer
  that keeps its state. The hub depends on no concrete store.
- **App registrations**: `IdentityClient`, an application the hub knows as a client, as an API,
  or as both, and `IIdentityClientRegistry`, which serves the registrations defined in code
  (locked) over the ones kept in the store.

The reasoning behind all three is in [DESIGN.md](./DESIGN.md).

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

`AddAudience`, `AddClient`, and `IdentityHubClientOptions` predate app registrations and are
transitional: they are what Hosting runs on until it adopts the registry.

### App registrations

| Type | Role |
| --- | --- |
| `IdentityClient` | A registration: a concrete, immutable type with no interface, created by `IdentityClient.Create(descriptor)`, which checks every rule |
| `IdentityClientDescriptor` | The mutable definition: set it with an object initializer, or copy one with `From` to edit a stored registration |
| `IIdentityClientRegistry`, `IdentityClientRegistry` | Find by identifier or audience, list, and the conditional writes. Created over a store and the registrations defined in code |
| `IdentityClientPage`, `IdentityClientWriteResult`, `IdentityClientWriteStatus` | A page of a listing, and the outcome of a write: `Written`, `Removed`, `Locked`, `IdConflict`, `VersionConflict`, `AudienceConflict` |
| `IdentityClientGrants` | The grants an application may use |
| `IdentityClientRedirect`, `IdentityClientPlatform` | A redirect URI, matched exactly, and the kind of application it returns to |
| `IdentityClientCredential` and `IdentityClientSecretCredential`, `IdentityClientCertificateCredential`, `IdentityClientFederatedCredential` | What an application proves its identity with. A secret is kept only as a salted PBKDF2 verifier |
| `IdentityClientScope`, `IdentityClientRole`, `IdentityClientRoleMembers` | What a registration's API exposes: delegated scopes, and roles for users or applications |
| `IdentityClientPermission`, `IdentityClientPermissionKind` | A scope or role, of another registration or its own, that a registration may be issued |
| `IdentityClientId` | A registration's identifier and OAuth `client_id`: a GUID. `FromName` derives it from a name, the same on every machine; `New` makes a random one |
| `IdentityClientName`, `IdentityClientCredentialId`, `IdentityClientPermissionName`, `IdentityClientAudience` | Generated value objects; each carries its grammar in `IsValid`. A client name compares without regard to case |

```csharp
IdentityClientRegistry registry = IdentityClientRegistry.Create(store,
[
    IdentityClient.Create(new()
    {
        Id = IdentityClientId.FromName("orders-api"),
        DisplayName = "Orders API",
        Audiences = { "api://orders" },
        Scopes = { IdentityClientScope.Create("orders.read") },
    }),
    IdentityClient.Create(new()
    {
        Id = IdentityClientId.FromName("orders-web"),
        DisplayName = "Orders web",
        Grants = IdentityClientGrants.AuthorizationCode | IdentityClientGrants.RefreshToken,
        Redirects = { IdentityClientRedirect.Web("https://orders.example/signin") },
        Permissions = { IdentityClientPermission.Delegated(IdentityClientId.FromName("orders-api"), "orders.read") },
    }),
]);

IdentityClient? web = await registry.FindAsync(IdentityClientId.FromName("orders-web"), cancellationToken);   // web.IsLocked is true
IdentityClientWriteResult result = await registry.TryAddAsync(runtimeClient, cancellationToken);
```

A registration defined in code is locked: it is never written to the store, and every write that
names it is answered `Locked`. A registration added through the registry is stored, and changes
only at the version that was read.

`FromName` derives the same identifier from a name everywhere (`orders-api` is always
`bec0a3ee-3f3c-599e-bd0b-3b7194e035ba`), so a registration created at run time under a name is
adopted by code that later defines that name: from that deployment it is served from the code,
locked, and whole. What the stored registration held and the code does not define, credentials
included, is no longer served.

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

Only the app registrations consume the store seam today, and nothing consumes the registrations.
`IdentityHub.Hosting` still keeps its device authorizations, signing key, and clients in its own
internal types and issues tokens from `AddClient`: a registration defined with
`IdentityClient.Create` is not yet a client the token endpoint knows. The builder has no verb for
a store, a registry, or a code-first registration (`AddClient(IdentityClientId, ...)` is designed
and lands with the token endpoint's move), no web API manages stored registrations, and durable stores are separate
packages that do not exist yet. Each is tracked as a follow-up work item, and DESIGN.md lists the
decisions that are still open, tenancy in the record address first among them.

## Hosting-free application contract (O34)

The root contracts are hosting-free (O34): `IIdentityHubApplication` exposes `Context`, `StartAsync`, and `StopAsync`; `IIdentityHubApplicationContext` exposes `ContentRootPath`. `IIdentityHubApplicationBuilder` owns area declarations and `Build()`. The root and feature packages reference no `Assimalign.Cohesion.Hosting*` library; COHRES004 enforces the boundary.

`IdentityHubApplication.CreateBuilder(args)` returns the public concrete `IdentityHubApplicationBuilder`; its `Build()` returns the public `IdentityHubApplication : Host<IdentityHubApplicationContext>`. The public `IdentityHubApplicationContext` implements `IIdentityHubApplicationContext`, reading `ContentRootPath` from the host environment. The application explicitly forwards the root lifecycle contract to `IHost`, and consumers use the concrete application for `RunAsync` and `await using`. Runtime options and supporting services remain internal.

Background-work registration (`AddService`) is available only on the concrete Hosting builder.
