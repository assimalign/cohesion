# Public API

`RezolvrResource` inherits `PlannedResource`; `RezolvrResourceOptions` inherits `ResourceOptions`. `IRezolvrResourceDescriptor` exposes the typed resource, commands, and dependency edges. `AddRezolvr` extends `IApplicationBuilder`; `RezolvrResourceControlPlane.Create` returns an isolated `IResourceControlPlane` accepting `rezolvr.add-a-record` and `rezolvr.add-cname-record`.

## Declarative command extensions

[RezolvrResourceCommandExtensions](RezolvrResourceCommandExtensions/OVERVIEW.md)
adds typed command declarations with deterministic ids and source-generated payload metadata.
