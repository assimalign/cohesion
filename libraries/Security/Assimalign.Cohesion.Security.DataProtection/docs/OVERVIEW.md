# Assimalign.Cohesion.Security.DataProtection — Overview

Purpose-bound data protection for Cohesion: a shared primitive for protecting small blobs
(antiforgery tokens, auth cookies, session ids, …) with authenticated encryption, a rotating
key ring, and pluggable persistence — so consumers stop hand-rolling key management.

## What it provides

- **Purpose-scoped protectors.** Ask the provider for a protector bound to a purpose; protectors
  for different purposes are cryptographically isolated even though they share one key ring.
- **Authenticated encryption.** AES-256-GCM payloads with a versioned, key-id header; HKDF-SHA256
  derives a per-purpose subkey from the ring's active key.
- **A rotating key ring.** Keys have a lifetime; the ring rotates lazily and keeps unprotecting
  retired keys for a configurable grace window, so tokens survive restarts, rotation, and
  multi-node fan-out. A payload naming a key the ring does not hold reloads the repository at
  most once per `UnknownKeyReloadInterval`, so a client cannot force a read per request, and a
  key another node writes resolves within one interval.
- **Pluggable persistence.** An `IKeyRepository` seam with a file-system default; point every
  node at one shared directory to share keys with no raw bytes copied by hand.

`DataProtectionProvider` and `FileSystemKeyRepository` are public sealed implementations with
private constructors. Their `Create` methods return the concrete type; application code can
continue to use `IDataProtectionProvider` and `IKeyRepository` contracts.

Custom repositories receive `KeyDocument` values with a validated `KeyDocumentName`.
Valid strings convert implicitly (`new KeyDocument("key-123", content)`); invalid names are
refused before construction. Use `KeyDocumentName.TryCreate(input, out var name)` when
invalid input should return a failure without throwing. The rule retains the current
platform's file-name restrictions, rejects empty/rooted names and any `..` substring, and
compares names ordinally with case preserved. `default(KeyDocumentName)` is not a document
name and is refused by the constructor. When using the file-system repository, foreign files
with invalid document names are skipped during loading.

## Quick use

```csharp
using Assimalign.Cohesion.Security.DataProtection;

// Composition root (a *.Hosting project), created once and shared:
IDataProtectionProvider provider = DataProtectionProvider.Create(
    FileSystemKeyRepository.Create("/var/lib/myapp/keys"),
    options =>
    {
        options.ApplicationDiscriminator = "myapp";        // isolates co-located apps
        options.KeyLifetime = TimeSpan.FromDays(90);
        options.UnprotectGracePeriod = TimeSpan.FromDays(7);
        options.UnknownKeyReloadInterval = TimeSpan.FromSeconds(30); // the default
    });

// Consumer code:
IDataProtector protector = provider.CreateProtector("Cohesion.Http.Antiforgery.v1");
byte[] wire = protector.Protect(payload);
byte[] back = protector.Unprotect(wire);   // throws DataProtectionException if invalid
```

## Scope

- **In:** the protector contracts, AES-256-GCM + HKDF-SHA256 core, key ring with rotation and
  grace, the `IKeyRepository` contract, and the file-system repository.
- **Out (follow-ups):** at-rest encryption of key documents, a SecretStore-backed repository and
  escrow, and a public key-revocation/administration API.

## Dependencies

- BCL `System.Security.Cryptography` only. No `Microsoft.Extensions.*`. AOT/trim-safe.

## Further reading

- [DESIGN.md](DESIGN.md) — the cryptographic construction, rotation/grace rules, and why
  composition lives in `*.Hosting`.
