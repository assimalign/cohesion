# Summary

`Assimalign.Cohesion.Core` is the dependency-light base library for all Cohesion libraries. It
owns the frozen `COHESION_*` runtime variable names through `AppEnvironment.Variables`, typed
endpoint values through `System.Uri` plus `UriExtensions`, and the shared application-environment
resolution rule and runtime-contract readers through `AppEnvironment`.



# Types

- `AppEnvironment.Variables` — version 1 runtime-contract constants and name builders; the
  contract's only .NET home.
- `UriExtensions` — endpoint construction, parsing, validation, path access, and canonical formatting
  on `System.Uri`.
- `AppEnvironment` — Cohesion, .NET, then Production environment-name resolution, plus the raw-value
  and typed port, URI, endpoint, dependency, and mount readers.

## Path

## Size
