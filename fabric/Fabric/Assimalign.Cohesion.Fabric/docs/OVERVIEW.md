# Assimalign.Cohesion.Fabric

The fabric root: the contracts every part of the new application model shares. The
ResourceManager, the AppHost, each fabric area, each area's resource provider, and each platform
build on it.

## What it holds

| Group | Types |
| --- | --- |
| Identity | `ResourceId` (the address), `ResourceUid` (the identity, UUIDv7), `ResourceName`, `ResourceTypeName`, `PlatformName`, `ApiVersion` |
| Resource model | `ResourceEnvelope`, `ResourceMetadata`, `ResourceStatus`, `FabricCondition`, `ProvisioningState`, `ResourceVisibility`, `ManagedFieldsEntry` |
| References | `FabricRef`, `FabricValue<T>` |
| Faults | `FabricFault`, `FaultCode`, `FaultDisposition`, `PlatformDetail`, `PlatformEvent`, `IFabricFaultClassifier` |
| Validation | `FabricDiagnostic`, `FabricDiagnostics`, `FabricValidationContext` |
| Providers | `IResourceProvider` and its inputs and results, `ResourceTypeAttribute`, `ResourceActionAttribute` |
| Platforms | `IFabricPlatform`, `FabricPlatform`, `IFabricPlatformBuilder`, `IFabricExtension`, `FabricExtension<THub>`, `FabricPlatformRegistration` |
| JSON | `FabricJsonContext`, `FabricRefJsonConverter`, `FabricStringValueJsonConverter` |

## Dependencies

None outside the BCL. See [DESIGN.md](DESIGN.md) for the decisions and what is deliberately absent.
