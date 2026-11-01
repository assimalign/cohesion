# Assimalign.Cohesion.ApplicationModel — Overview

## Purpose

Declares the identity every plane of the application model shares: what a resource is (its
provider), which one it is (its name), the ids derived from them, and the platform it runs on. The
logical model, the gateway, and every instance carry the same names without loading each other's
code. The design record is [DESIGN.md](DESIGN.md).

## Scope

- In scope: `IApplicationResource`, the base of a resource's definition, deployment, and instances;
  `ProviderName`, `ResourceName`, `ProviderId`, `ResourceId`; and `PlatformName`.
- Out of scope: what a gateway and an instance exchange (ApplicationModel.Protocol), the logical
  model (ApplicationModel.Definition), the instance runtime (ApplicationModel.Hosting), gateways
  (ApplicationModel.Gateway), and every transport, which belongs to the platform packages.

## Dependencies

`Assimalign.Cohesion.Core` only. Its `shared/Names/` module, the hyphenated-words name grammar, is
compiled into this assembly, Protocol, and Definition.

## Usage

Every form of a resource derives the same id from its provider and name:

```csharp
ResourceId id = ResourceId.FromName("Web", "orders-api");
PlatformName platform = "kubernetes";
```
