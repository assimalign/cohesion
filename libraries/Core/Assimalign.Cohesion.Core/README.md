# Summary

`Assimalign.Cohesion.Core` is the dependency-light base library for all Cohesion libraries. It
owns the frozen `COHESION_*` runtime variable names through `ResourceEnvironment`, typed endpoint
values through `System.Uri` plus `UriExtensions`, and the shared application-environment resolution
rule.



# Types

- `ResourceEnvironment` — version 1 runtime-contract constants, name builders, and typed readers.
- `UriExtensions` — endpoint construction, parsing, validation, path access, and canonical formatting
  on `System.Uri`.
- `AppEnvironment` — Cohesion, .NET, then Production environment-name resolution.

## Path

## Size
