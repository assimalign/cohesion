# Assimalign.Cohesion.Security.DataProtection — Design

Purpose-bound data protection with a rotating key ring. This document captures why the
library looks the way it does so future readers don't re-derive it from diffs.

## Design intent

Give Cohesion **one** shared primitive for persisted, rotating, purpose-bound symmetric
protection, so every consumer that needs to protect a small blob against tampering (and
optionally read it back on another node or after a restart) stops hand-rolling its own key
handling. The motivating gap was live in shipped code: `Http.Antiforgery` defaulted its HMAC
key to per-process random bytes and documented that multi-node deployments must hand-distribute
a raw static `byte[]` with no rotation. Cookie-auth tickets, sessions, and TempData-equivalents
would each have reinvented the same thing. This library is the foundation those consumers build
on instead.

The shape mirrors the parts of ASP.NET Core Data Protection that matter here — a
provider/protector split, purpose chaining, a key ring with rotation and a grace window — while
staying inside Cohesion's constraints: **BCL `System.Security.Cryptography` only**, no
`Microsoft.Extensions.*`, AOT/trim-safe with zero reflection, interface-first with internal
implementations, and builder-time composition pushed out to consumers' `*.Hosting` projects.

## Surface

- `IDataProtectionProvider` — creates purpose-scoped protectors. The composition-root entry
  point.
- `IDataProtector : IDataProtectionProvider` — `Protect`/`Unprotect` over
  `ReadOnlySpan<byte>`; because it *extends* the provider, calling `CreateProtector` on a
  protector derives a further-scoped child, so purposes compose into a chain.
- `DataProtectionProviderExtensions.CreateProtector(params string[])` — builds a multi-segment
  chain in one call (an `extension(...)` member), equivalent to chaining segment by segment.
- `IKey` — read-only key metadata (id, created/activated/expires, revoked). Never exposes
  material.
- `IKeyRepository` + `KeyDocument` — the persistence seam; a pure opaque-blob store.
- `KeyRepository.CreateFileSystem(path)` — the default file-system repository.
- `DataProtectionOptions` — discriminator, key lifetime, unprotect grace period, unknown-key
  reload interval.
- `DataProtectionProvider.Create(...)` — factory that assembles the ring + provider.
- `DataProtectionException` — the area-scoped exception root.

Everything else (`AesGcmDataProtector`, `KeyRing`, `KeyRingProtectionProvider`, `ManagedKey`,
`KeySerializer`, `PurposeChain`, `FileSystemKeyRepository`) is `internal`.

## Cryptographic construction

**Payload layout** (`AesGcmDataProtector`):

```
[version:1=0x01][keyId:16][nonce:12][ciphertext:n][tag:16]
```

- **AES-256-GCM** is the authenticated cipher. Overhead is a fixed 45 bytes.
- The **key id** in the header is what makes rotation transparent: `Unprotect` reads it and
  asks the ring for that exact key, so payloads minted under a now-retired key still verify.
- The **version+keyId header is the GCM associated data**, so neither the format version nor
  the key id can be altered without failing authentication (prevents version downgrade and
  key-id swapping).
- The **subkey** is derived per operation with **HKDF-SHA256** from the selected ring key's
  256-bit master, using the protector's purpose chain as HKDF `info` (see below). The master
  is never used as an AES key directly. Derived subkeys are zeroed
  (`CryptographicOperations.ZeroMemory`) immediately after each `Protect`/`Unprotect`.
- A fresh random **96-bit nonce** is drawn per `Protect`. Because the subkey is unique per
  `(ring key, purpose chain)`, the nonce space is scoped to a single subkey, which keeps the
  random-nonce collision bound (birthday ≈ 2⁴⁸ messages) comfortable for token-sized workloads.
  A deterministic counter was rejected because it would require persisting per-key nonce state,
  defeating the stateless-node goal.

**Purpose binding** (`PurposeChain`): the HKDF `info` is
`context ‖ (uint32-BE length ‖ UTF-8 bytes) for each purpose`, where `context` is a
version-stamped label. Length-prefixing makes the encoding unambiguous, so `["ab","c"]` and
`["a","bc"]` derive different subkeys. The **application discriminator is the first element of
every chain**, so two applications that share a repository (and therefore ring keys) but use
different discriminators cannot read each other's payloads — crypto isolation, not storage
partitioning.

## Key ring, rotation, and grace

`KeyRing` holds the deserialized keys in memory and owns the lifecycle rules. Time is read
through an injected `TimeProvider` (BCL) so rotation, grace, and the reload throttle are
unit-testable without real delays.

- **Active key selection** (`GetActiveKey`, protect path): the newest non-revoked key whose
  `[ActivatedAt, ExpiresAt)` window contains "now". If none qualifies (first run, or the active
  key just expired), a fresh key is created, persisted, and cached. Rotation is therefore
  **lazy** — it happens on the first protect after expiry, with no background scheduler — which
  fits the "no hosted services in the core library" posture.
- **Unprotect resolution** (`ResolveForUnprotect`): the producing key is accepted until
  `ExpiresAt + UnprotectGracePeriod`, so payloads minted just before a rotation (or under a node
  with a slightly skewed clock) keep validating across the fleet. **Revoked** keys are rejected
  immediately regardless of the window. Unknown/expired/revoked each raise a
  `DataProtectionException` with a distinct (safe) message — AEAD has no padding oracle, so
  distinguishing lifecycle failures leaks nothing about plaintext and aids operators.
- **Cross-node freshness**: if a payload names a key not in the in-memory snapshot, the ring
  may reload from the repository before failing — this is how a node picks up a key another
  node created after it last loaded. The payload's sender chooses that key id, so the reload is
  throttled (next section).

## Reloads and the unknown-key throttle

The key id in a payload header comes from the client. Antiforgery tokens and authentication
cookies arrive from unauthenticated clients, and the ring has to resolve the id before the GCM
tag can be checked, because the tag needs the key. Until #1155, every unknown id reloaded the
whole repository under the ring's only lock, and every other protect and unprotect waited behind
that read. One request with a random key id bought a full repository read and a process-wide
stall.

- **Snapshot.** The keys live in an immutable `FrozenDictionary` held in a volatile field.
  `Protect` and `Unprotect` read it without a lock. A reload or a key creation builds a new
  snapshot and swaps it in, one at a time under the reload lock, so snapshots are published in
  the order their reads ran. Protecting or unprotecting with a key the ring holds never waits
  on a repository read.
- **Throttle.** A miss may reload only once `DataProtectionOptions.UnknownKeyReloadInterval`
  (default 30 seconds, must be positive) has passed since the previous miss-triggered read
  began. Inside that window a miss costs one timestamp read and a second lookup in the current
  snapshot, and an id still not there is reported unknown without taking the lock. The window is
  measured from the start of the read, and a read that throws closes it too, so a failing
  repository is not read again on every miss.
- **Snapshot before window.** A reload publishes its snapshot before it closes the window, and
  a miss that finds the window closed looks its id up again after reading it. A miss whose first
  lookup ran before another miss's reload published, and that reached the window after it
  closed, therefore still finds the key that read loaded. The second lookup also sees a snapshot
  a protect-path reload published after the first one.
- **Single flight.** Misses that arrive while a reload runs wait for it on the lock, then look
  their id up in the snapshot it published. They share its result instead of reading again, so
  a burst of payloads under a freshly rotated key costs one read.
- **What single flight costs.** The window closes only when the read ends, so every miss that
  arrives during the read, invented ids included, holds its thread on the lock until the read
  finishes. Under a flood at rate *r* that parks about *r* × *d* threads once per interval, where
  *d* is the read's duration. With the file-system repository *d* is milliseconds. `Unprotect`
  runs synchronously on request threads, so a remote repository (#806) must keep its reads short
  or move the repository seam to an async read. Closing the window before the read would let
  those misses fail without the lock, but it would also reject the genuine burst under a freshly
  rotated key that arrives during the read, which is what single flight exists to serve, so it
  was not taken.
- **No per-id negative cache.** The throttle counts reloads, not ids. An id that is still
  unknown after a reload is reported unknown without another read until the window passes,
  which is all a negative cache would add. Against invented ids, which are new every time, a
  per-id cache would add nothing and would need its own memory bound.
- **The first miss after startup reloads at once.** The constructor's load does not open the
  window, so a node that starts just before another node rotates picks up the new key on first
  sight.
- **Monotonic time.** The window runs on `TimeProvider.GetTimestamp()`, so a wall clock stepped
  backward cannot hold it shut. Key lifetimes and grace still use `GetUtcNow()`, because they
  are persisted instants.
- **Protect-path reloads are not throttled.** `GetActiveKey` reloads only while its snapshot
  holds no active key, and the key it then finds or creates ends that, so while the repository
  works a client cannot make it repeat. It takes the reload lock, so its snapshot is ordered with
  the miss reloads, but it does not move the miss window. Rotation is exactly when misses are
  legitimate, and a protect that reloaded a moment earlier must not make another node's new key
  wait.
- **A failing repository makes every protect retry.** When the read, or the store of a new key,
  throws while no active key exists (an outage at key expiry, or a first start on a repository
  that cannot be written), no active key is published. Every later `Protect` then repeats the
  read and the write attempt under the reload lock until the repository recovers, and misses
  outside the window queue behind it. A client can drive that on any endpoint that hands
  antiforgery tokens to anonymous callers. `Protect` cannot succeed in that state anyway, and
  unprotecting with a key the ring holds stays lock-free. A failure backoff was not added,
  because it would keep every `Protect` failing for its length after the repository recovers.

**The propagation bound.** A key another node writes at time *t* resolves here no later than
*t* + `UnknownKeyReloadInterval`. If the last miss-triggered read started after *t*, it already
loaded the key. Otherwise it started before *t*, so its window closes before
*t* + `UnknownKeyReloadInterval`, and the payload that names the key is the miss that reloads.
With no other miss inside the preceding interval, the key resolves on first sight, as it did
before the throttle. Under a flood of invented ids, the repository is read once per interval,
and a payload under a just-rotated key can be rejected as unknown for up to one interval on
nodes that did not create the key. A shorter interval narrows that window and raises the
worst-case read rate in proportion.

## Persistence: opaque documents

`IKeyRepository` deals only in `KeyDocument` (name + opaque bytes); it never interprets content.
Serialization lives in the internal `KeySerializer` (a hand-written, line-oriented text format —
no reflection-based serializer, so it stays AOT/trim-safe and is debuggable on disk). This keeps
the repository contract minimal and makes the planned SecretStore-backed repository a pure blob
store with no key-format knowledge. A malformed or foreign document is skipped on load so one
bad file can't wedge the ring.

`FileSystemKeyRepository` writes one `<keyId>.key` file via a temp-then-atomic-move so a
concurrent reader never sees a partial document.

## Composition happens elsewhere

This library ships **no** DI, logging, configuration, or hosted-service integration. A consumer
wires it at builder time: choose a repository, set the discriminator and rotation policy,
construct the provider, and adapt the resulting `IDataProtector` to whatever seam the consumer
exposes. For antiforgery that seam is `IHttpAntiforgeryProtector` on `HttpAntiforgeryOptions`;
the `Http.Antiforgery` package takes **no** dependency on this library, and the adapter ships
in the Web composition layer: `AddAntiforgery(dataProtectionProvider)` in
`Assimalign.Cohesion.Web.Antiforgery` derives a protector for the purpose chain
`("Assimalign.Cohesion.Web.Antiforgery", "v1")` and maps `DataProtectionException` to an
invalid token. This keeps request-path code free of service location and keeps each library's
dependency tree lean.

## AOT posture

BCL crypto throughout (`AesGcm`, `HKDF`, `RandomNumberGenerator`,
`CryptographicOperations`, `HMACSHA256` is not used here). No reflection, no dynamic code, no
runtime type inspection, no reflection-based (de)serialization. `IsAotCompatible=true` is
inherited from the libraries build props.

## Error model

- Argument problems (null repository/options, out-of-range option values, empty repository
  path) throw `ArgumentException`/`ArgumentNullException`.
- Every protection/verification/key-lifecycle failure surfaces as `DataProtectionException`
  (the area root), wrapping the underlying `CryptographicException` on authentication failure.
  Messages never reveal key material or plaintext.
- A repository read that fails during an unknown-key reload surfaces from `Unprotect` as
  `DataProtectionException`, with the repository's exception as `InnerException`. `Unprotect` is
  fed untrusted input, its contract names only that type, and its callers (the antiforgery
  adapter, the cookie handler) catch only that type, so a token or cookie that names an invented
  key id is rejected rather than turned into an unhandled fault. Only the caller whose miss ran
  the read gets that message. Callers that waited on that read, and every miss inside the window
  it closes, get the ordinary unknown-key `DataProtectionException`.
- A repository failure on the protect path (`GetActiveKey`'s reload, or the store of a new key)
  still propagates unchanged from `Protect`, although `IDataProtector.Protect` documents
  `DataProtectionException` for an unreadable repository. That gap predates the throttle.

## Non-goals (this iteration)

- **At-rest encryption of key documents.** v1 stores master material base64-encoded in the
  clear; the repository medium is the confidentiality boundary (file-system permissions for the
  default). At-rest ring encryption is a tracked follow-up aligned with #99/#277/#278.
- **SecretStore-backed repository and key escrow.** The `IKeyRepository` seam is designed for
  it; the implementation is a separate follow-up in the SecretStore client integration.
- **Public key revocation/administration API.** The ring *honors* a revoked flag on unprotect;
  populating it (an admin revoke operation) is deferred.
- **Asymmetric protection, key wrapping, and cross-service key sharing.** Out of scope for a
  single-application symmetric primitive.
- **Scheduled/background rotation.** Rotation is lazy on the protect path by design; a hosted
  rotation service, if ever wanted, belongs in a `*.Hosting` layer, not here.

## Relationships

- **`Assimalign.Cohesion.Http.Antiforgery`** is the first consumer, via its
  `IHttpAntiforgeryProtector` seam. It does not reference this library;
  `Assimalign.Cohesion.Web.Antiforgery` does, and adapts a purpose-bound protector to the seam
  when the application passes a provider to `AddAntiforgery`.
- Future consumers: auth cookie handlers (#790), sessions (#785), and any TempData-equivalent —
  each asks the provider for its own purpose instead of hand-rolling key handling.
