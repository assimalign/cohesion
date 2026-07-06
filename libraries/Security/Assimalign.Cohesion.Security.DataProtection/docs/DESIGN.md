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
- `DataProtectionOptions` — discriminator, key lifetime, unprotect grace period.
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
through an injected `TimeProvider` (BCL) so rotation and grace are unit-testable without real
delays.

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
  reloads from the repository once before failing — this is how a node picks up a key another
  node created after it last loaded.

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
wires it at builder time in its `*.Hosting` project: choose a repository, set the discriminator
and rotation policy, construct the provider, and adapt the resulting `IDataProtector` to
whatever seam the consumer exposes. For antiforgery that seam is
`IHttpAntiforgeryProtector` on `HttpAntiforgeryOptions`; the Antiforgery package takes **no**
dependency on this library, and the composition root supplies the ~5-line adapter. This keeps
request-path code free of service location and keeps each library's dependency tree lean.

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
  `IHttpAntiforgeryProtector` seam. It does not reference this library; a `*.Hosting` project
  adapts a protector to the seam.
- Future consumers: auth cookie handlers (#790), sessions (#785), and any TempData-equivalent —
  each asks the provider for its own purpose instead of hand-rolling key handling.
