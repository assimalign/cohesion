# Assimalign.Cohesion.Security Design

## Design Intent

Provide a small, platform-agnostic surface for **loading** X.509 certificates that any Cohesion
component (most immediately the TLS connection layer) can depend on without re-implementing the
BCL's certificate-loading quirks. Every operation resolves to a platform-neutral BCL loader, so the
behavior is identical on Windows, Linux, and macOS, and the library stays NativeAOT- and
trimming-safe.

## Scope (this iteration)

Certificate **loading** only:

- PKCS#12 / PFX from a file path or from bytes (`LoadPkcs12FromFile`, `LoadPkcs12`).
- PEM from a file, optionally paired with a private-key PEM (`LoadPemFromFile`).

## Surface

- `ICertificateManager` — the contract (the three load operations).
- `CertificateManager` (internal) — the default implementation, backed by
  `X509CertificateLoader` (PKCS#12) and `X509Certificate2.CreateFromPemFile` (PEM).
- `CertificateManagerFactory.Create()` — the public entry point that returns the default manager.
- `CertificateException` — the area-scoped exception root; load failures (missing file, malformed
  content) surface as this type, wrapping the underlying `CryptographicException` where applicable.

## Out of Scope (tracked separately)

Deliberately deferred so this iteration stays minimal and buildable:

- Operating-system certificate **store** access — the Windows certificate store, the macOS keychain,
  and Linux trust directories.
- Self-signed certificate generation, store import/export, and thumbprint lookup.

These were the focus of the previous (removed) `Security.Cryptography` attempt, which was
store-centric, stale (pre-rename namespace), and did not compile. They are tracked as a backlog
item for a fresh, platform-agnostic design.

## Error Model

- Argument problems (null/empty path, empty data) throw `ArgumentException`.
- A missing file or content that cannot be parsed as the requested format throws
  `CertificateException` (wrapping the platform `CryptographicException` when there is one).

## AOT Posture

Uses `X509CertificateLoader` — the AOT- and trimming-safe certificate loader — and the
`CreateFromPemFile` BCL API. No reflection, dynamic code, or runtime type inspection.

## Relationships

- **`Assimalign.Cohesion.Connections.Security`** (the TLS connection layer) is the primary consumer:
  it sources the certificate for `TlsServerOptions` / `TlsClientOptions` from a loaded
  `X509Certificate2`. That layer takes no dependency on this library — certificates are supplied to
  it by the application composition root.
