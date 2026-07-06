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
  multi-node fan-out.
- **Pluggable persistence.** An `IKeyRepository` seam with a file-system default; point every
  node at one shared directory to share keys with no raw bytes copied by hand.

## Quick use

```csharp
using Assimalign.Cohesion.Security.DataProtection;

// Composition root (a *.Hosting project), created once and shared:
IDataProtectionProvider provider = DataProtectionProvider.Create(
    KeyRepository.CreateFileSystem("/var/lib/myapp/keys"),
    options =>
    {
        options.ApplicationDiscriminator = "myapp";        // isolates co-located apps
        options.KeyLifetime = TimeSpan.FromDays(90);
        options.UnprotectGracePeriod = TimeSpan.FromDays(7);
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
